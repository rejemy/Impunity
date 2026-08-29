// ───────── Exclusive scopes ─────────
//
// RunExclusive: acquire an entity's server-side lock, run some logic while holding it, release the lock.
//
// The point of the helper is a handoff guarantee. If client A runs a scope, edits fields and exits, and client B
// is next in the server's waiter queue, B's body must observe every one of A's edits. That falls out of two
// things:
//
//   • On exit we flush A's dirty fields BEFORE sending the unlock. Field writes only mark dirty bits and are
//     normally flushed by the next Update(), so a plain "Set(); Unlock();" would put the unlock on the wire
//     first and let B read stale values.
//   • The server hands the lock to the head of its waiter queue rather than announcing "it's free, race for it".
//     The grant is emitted from the live worker thread after A's relayed updates, and everything the server
//     sends one connection travels a single ordered path, so B's grant cannot overtake A's edits.
//
// Unguaranteed fields (SetUnguaranteed) are outside this guarantee: they leave the ordered path for UDP and may
// land after the next holder's body has run, or not at all.

using System;
using System.Collections.Generic;

using Impunity.GameState;

namespace Impunity.Connection
{

	/// <summary>Outcome of a <c>RunExclusive</c> scope.</summary>
	public enum RunExclusiveResult
	{
		/// <summary>The lock was acquired, the body ran to completion, its edits were flushed and the lock released.</summary>
		Ran,
		/// <summary>The lock was not acquired before the timeout elapsed. The body did not run.</summary>
		TimedOut,
		/// <summary>The body threw, the entity was deleted, or the request failed. The error argument carries the
		/// detail. If the body had started, its edits are still flushed and the lock is still released.</summary>
		Failed,
	}

	public partial class ClientEntityManager
	{
		/// <summary>Timeout applied to <c>RunExclusive</c> calls that do not name one (the default). Counts only the
		/// wait to acquire the lock, never the time the body spends running.</summary>
		public float DefaultExclusiveTimeoutSeconds = 10f;

		/// <summary>One in-flight or queued exclusive scope.</summary>
		private class ExclusiveScope
		{
			public IDistributedEntity Entity = default!;
			/// <summary>Invoked once the lock is held. Must call the supplied completion exactly once, with null on
			/// success or the thrown exception on failure.</summary>
			public Action<Action<Exception?>> Body = default!;
			public ImpunityCallback<RunExclusiveResult>? OnComplete;
			public float TimeoutSeconds;

			public DateTimeOffset Deadline;
			/// <summary>True while a deadline is still worth checking — cleared once the body starts or the wait is
			/// being cancelled, so <see cref="TickExclusiveScopes"/> never fires twice for one scope.</summary>
			public bool DeadlinePending;
			/// <summary>True once the body has been entered. Guards the grant-versus-cancel race: both paths can
			/// legitimately try to start the body, and exactly one must win.</summary>
			public bool Running;
			/// <summary>True once the scope has been completed, so a late reply cannot complete it twice.</summary>
			public bool Finished;
		}

		/// <summary>The scope currently holding or waiting for each entity's lock, keyed by entity id.</summary>
		private Dictionary<uint, ExclusiveScope>? ActiveScopes;
		/// <summary>Scopes this connection queued behind its own active scope on the same entity. Kept local so the
		/// server only ever sees one lock/unlock pair per scope from this connection.</summary>
		private Dictionary<uint, Queue<ExclusiveScope>>? PendingScopes;
		/// <summary>Callbacks registered by <see cref="WaitForEntityLock"/>, keyed by entity id.</summary>
		private Dictionary<uint, List<ImpunityCallback<LockWaitResult>>>? EntityLockWaiters;

		// ═══════════════════════════════════════════════════════════
		// RunExclusive
		// ═══════════════════════════════════════════════════════════

		/// <summary>Acquires the entity's lock, runs <paramref name="body"/> while holding it, then flushes the body's
		/// edits and releases the lock. Backs <see cref="IDistributedEntity.RunExclusive"/>.</summary>
		/// <param name="entity">The entity to hold the lock on.</param>
		/// <param name="body">Invoked on the main thread once the lock is held.</param>
		/// <param name="onComplete">Invoked with the scope's outcome. Never runs reentrantly.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock. Negative uses
		/// <see cref="DefaultExclusiveTimeoutSeconds"/>; zero does not wait at all.</param>
		internal void RunExclusive(IDistributedEntity entity, Action body,
					ImpunityCallback<RunExclusiveResult>? onComplete, float timeoutSeconds)
		{
			RunExclusiveDeferred(entity, finish =>
			{
				try
				{
					body();
					finish(null);
				}
				catch (Exception e)
				{
					finish(e);
				}
			}, onComplete, timeoutSeconds);
		}

		/// <summary>As <see cref="RunExclusive"/>, but for a body that completes later (a task or a coroutine): the
		/// lock is held until the body calls the completion it is handed.</summary>
		/// <param name="entity">The entity to hold the lock on.</param>
		/// <param name="body">Invoked once the lock is held; must call its argument exactly once when finished,
		/// passing null on success or the exception that ended it.</param>
		/// <param name="onComplete">Invoked with the scope's outcome. Never runs reentrantly.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock. Negative uses
		/// <see cref="DefaultExclusiveTimeoutSeconds"/>; zero does not wait at all.</param>
		internal void RunExclusiveDeferred(IDistributedEntity entity, Action<Action<Exception?>> body,
					ImpunityCallback<RunExclusiveResult>? onComplete, float timeoutSeconds)
		{
			ExclusiveScope scope = new ExclusiveScope
			{
				Entity = entity,
				Body = body,
				OnComplete = onComplete,
				TimeoutSeconds = timeoutSeconds < 0f ? DefaultExclusiveTimeoutSeconds : timeoutSeconds,
			};

			if (Connection == null)
			{
				ReportScope(scope, RunExclusiveResult.Failed,
					new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity manager has no connection"));
				return;
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				ReportScope(scope, RunExclusiveResult.Failed,
					new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				return;
			}

			ActiveScopes ??= new Dictionary<uint, ExclusiveScope>();

			// Already running a scope on this entity? Queue behind it rather than re-locking. The server treats a
			// second lock from the same ConnectionKey as re-entrant, so overlapping scopes would let the inner one's
			// unlock release the outer one's lock.
			if (ActiveScopes.ContainsKey(entity.DistributedEntityId))
			{
				PendingScopes ??= new Dictionary<uint, Queue<ExclusiveScope>>();
				if (!PendingScopes.TryGetValue(entity.DistributedEntityId, out var queue))
				{
					queue = new Queue<ExclusiveScope>();
					PendingScopes[entity.DistributedEntityId] = queue;
				}
				queue.Enqueue(scope);
				return;
			}

			StartScope(scope);
		}

		private void StartScope(ExclusiveScope scope)
		{
			uint entityId = scope.Entity.DistributedEntityId;

			ActiveScopes ??= new Dictionary<uint, ExclusiveScope>();
			ActiveScopes[entityId] = scope;

			// A zero timeout means "don't queue at all" — take the lock if it is free right now, otherwise give up.
			bool wait = scope.TimeoutSeconds > 0f;
			if (wait)
			{
				scope.Deadline = DateTimeOffset.UtcNow.AddSeconds(scope.TimeoutSeconds);
				scope.DeadlinePending = true;
			}

			Connection!.TryToLockEntity(entityId, wait, (err, locked) =>
			{
				if (err != null)
				{
					scope.DeadlinePending = false;
					ReportScope(scope, RunExclusiveResult.Failed, err);
					return;
				}

				if (locked)
				{
					EnterBody(scope);
				}
				else if (!wait)
				{
					ReportScope(scope, RunExclusiveResult.TimedOut, null);
				}

				// Otherwise we are queued on the server and the lock arrives as a grant push, or the deadline fires.
			});
		}

		private void EnterBody(ExclusiveScope scope)
		{
			// The grant push and a losing CancelLockWait reply can both land here; only the first may start the body.
			if (scope.Running || scope.Finished)
			{
				return;
			}

			scope.Running = true;
			scope.DeadlinePending = false;

			try
			{
				scope.Body(ex => ExitBody(scope, ex));
			}
			catch (Exception e)
			{
				// The body delegate threw before it could hand us a completion.
				ExitBody(scope, e);
			}
		}

		private void ExitBody(ExclusiveScope scope, Exception? bodyError)
		{
			if (scope.Finished)
			{
				return;
			}

			// UnlockEntity flushes the entity's pending edits before sending the release, so the next holder is
			// guaranteed to see them. That happens even for a failed body: the entity has already been mutated
			// locally, and leaving that unsent would diverge from the server.
			Connection?.UnlockEntity(scope.Entity.DistributedEntityId, (err, released) =>
			{
				if (err != null)
				{
					ImpunityLogger.LogError("Failed to release lock at end of RunExclusive: " + err.Message);
				}
			});

			if (bodyError != null)
			{
				ImpunityLogger.LogError("Exception in RunExclusive body", bodyError);
				ReportScope(scope, RunExclusiveResult.Failed,
					new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Exception in RunExclusive body: " + bodyError.Message));
				return;
			}

			ReportScope(scope, RunExclusiveResult.Ran, null);
		}

		/// <summary>Completes a scope exactly once, then starts whichever scope this connection had queued behind it.</summary>
		private void ReportScope(ExclusiveScope scope, RunExclusiveResult result, ImpunityErrorResponse? error)
		{
			if (scope.Finished)
			{
				return;
			}

			scope.Finished = true;
			scope.DeadlinePending = false;

			uint entityId = scope.Entity.DistributedEntityId;
			if (ActiveScopes != null && ActiveScopes.TryGetValue(entityId, out var active) && active == scope)
			{
				ActiveScopes.Remove(entityId);
			}

			ImpunityCallback<RunExclusiveResult>? onComplete = scope.OnComplete;
			if (onComplete != null)
			{
				// Route through the connection's local-callback queue so it is delivered on a later Update() and
				// never reentrantly, matching every other completion in the client API.
				if (Connection != null)
				{
					Connection.QueueLocalCallback(err => onComplete(err, result), error);
				}
				else
				{
					onComplete(error, result);
				}
			}

			StartNextPendingScope(entityId);
		}

		private void StartNextPendingScope(uint entityId)
		{
			if (PendingScopes == null || !PendingScopes.TryGetValue(entityId, out var queue))
			{
				return;
			}

			if (queue.Count == 0)
			{
				PendingScopes.Remove(entityId);
				return;
			}

			ExclusiveScope next = queue.Dequeue();
			if (queue.Count == 0)
			{
				PendingScopes.Remove(entityId);
			}

			StartScope(next);
		}

		/// <summary>Checks acquisition deadlines on waiting scopes. Called from <see cref="BaseGameConnection.Update"/>
		/// after inbound messages are dispatched, so a grant that arrived this frame beats its own deadline.</summary>
		public void TickExclusiveScopes()
		{
			if (ActiveScopes == null || ActiveScopes.Count == 0)
			{
				return;
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;
			List<ExclusiveScope>? expired = null;

			foreach (ExclusiveScope scope in ActiveScopes.Values)
			{
				if (scope.DeadlinePending && !scope.Running && now >= scope.Deadline)
				{
					(expired ??= new List<ExclusiveScope>()).Add(scope);
				}
			}

			if (expired == null)
			{
				return;
			}

			foreach (ExclusiveScope scope in expired)
			{
				// Clear first: the cancel is a round trip, and the deadline must not fire again while it is in flight.
				scope.DeadlinePending = false;

				Connection?.CancelLockWait(scope.Entity.DistributedEntityId, (err, cancelled) =>
				{
					if (err != null)
					{
						ReportScope(scope, RunExclusiveResult.Failed, err);
					}
					else if (cancelled)
					{
						ReportScope(scope, RunExclusiveResult.TimedOut, null);
					}
					else
					{
						// The server granted the lock before our cancel reached it. We hold it now, so run the
						// body rather than orphaning the lock. (EnterBody is a no-op if the grant push already
						// started it.)
						EnterBody(scope);
					}
				});
			}
		}

		/// <summary>Fails every scope waiting on or holding an entity's lock — the entity is gone, so the lock can
		/// neither be granted nor released.</summary>
		private void FailScopesForEntity(uint entityId, ImpunityErrorResponse error)
		{
			if (ActiveScopes != null && ActiveScopes.TryGetValue(entityId, out var active))
			{
				ActiveScopes.Remove(entityId);
				if (!active.Running)
				{
					active.DeadlinePending = false;
					// Bypass ReportScope's queue-advance: the whole queue is being failed below.
					active.Finished = true;
					InvokeScopeCallback(active, RunExclusiveResult.Failed, error);
				}
			}

			if (PendingScopes != null && PendingScopes.TryGetValue(entityId, out var queue))
			{
				PendingScopes.Remove(entityId);
				while (queue.Count > 0)
				{
					ExclusiveScope pending = queue.Dequeue();
					pending.Finished = true;
					InvokeScopeCallback(pending, RunExclusiveResult.Failed, error);
				}
			}
		}

		private void InvokeScopeCallback(ExclusiveScope scope, RunExclusiveResult result, ImpunityErrorResponse? error)
		{
			ImpunityCallback<RunExclusiveResult>? onComplete = scope.OnComplete;
			if (onComplete == null)
			{
				return;
			}

			if (Connection != null)
			{
				Connection.QueueLocalCallback(err => onComplete(err, result), error);
			}
			else
			{
				onComplete(error, result);
			}
		}

		// ═══════════════════════════════════════════════════════════
		// Entity lock waiting
		// ═══════════════════════════════════════════════════════════

		/// <summary>Acquires the entity's lock, queueing for it on the server when another client holds it. Backs
		/// <see cref="IDistributedEntity.WaitForLock"/>.</summary>
		internal void WaitForEntityLock(IDistributedEntity entity, ImpunityCallback<LockWaitResult> onComplete)
		{
			uint entityId = entity.DistributedEntityId;

			Connection?.TryToLockEntity(entityId, true, (err, locked) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, LockWaitResult.Error);
				}
				else if (locked)
				{
					onComplete?.Invoke(null, LockWaitResult.Locked);
				}
				else if (onComplete != null)
				{
					// Queued on the server; the lock arrives as a grant push, in turn.
					EntityLockWaiters ??= new Dictionary<uint, List<ImpunityCallback<LockWaitResult>>>();
					if (!EntityLockWaiters.TryGetValue(entityId, out var waiters))
					{
						waiters = new List<ImpunityCallback<LockWaitResult>>();
						EntityLockWaiters[entityId] = waiters;
					}
					waiters.Add(onComplete);
				}
			});
		}

		/// <summary>Handles an entity-lock-granted push: the server handed this connection the entity's lock from its
		/// waiter queue. Completes any pending <see cref="WaitForEntityLock"/> callbacks and starts a waiting
		/// <c>RunExclusive</c> body. Invoked by the connection's server-message dispatch on the main thread; not called
		/// by application code.</summary>
		/// <param name="entityId">The entity whose lock this connection now holds.</param>
		public void HandleEntityLockGranted(uint entityId)
		{
			if (SuppressedEntities.ContainsKey(entityId)) return;

			CompleteEntityLockWaiters(entityId, null, LockWaitResult.Locked);

			if (ActiveScopes != null && ActiveScopes.TryGetValue(entityId, out var scope))
			{
				EnterBody(scope);
			}
		}

		private void CompleteEntityLockWaiters(uint entityId, ImpunityErrorResponse? error, LockWaitResult result)
		{
			if (EntityLockWaiters == null || !EntityLockWaiters.TryGetValue(entityId, out var waiters))
			{
				return;
			}

			EntityLockWaiters.Remove(entityId);

			foreach (var waiter in waiters)
			{
				try
				{
					waiter.Invoke(error, result);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in WaitForLock callback", e);
				}
			}
		}

		/// <summary>Fails everything queued on a deleted entity's lock: pending scopes and lock waiters alike.</summary>
		private void AbandonLockWaitsForEntity(uint entityId)
		{
			ImpunityErrorResponse error = new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound,
				"Entity " + entityId + " was deleted while waiting for its lock");

			CompleteEntityLockWaiters(entityId, error, LockWaitResult.Error);
			FailScopesForEntity(entityId, error);
		}

		// ═══════════════════════════════════════════════════════════
		// Immediate flush
		// ═══════════════════════════════════════════════════════════

		/// <summary>Serializes and sends an entity's pending dirty fields right now, instead of waiting for the next
		/// per-frame sweep in <see cref="SendUpdates"/>. Actions leave the connection in call order, so anything sent
		/// after this call is ordered behind the update — which is what lets a lock release carry the writes made
		/// under it. Fields written with <c>SetUnguaranteed</c> still go out unreliably and carry no such ordering.
		/// No-op when the entity has nothing pending.</summary>
		/// <param name="entity">The entity whose pending changes to send.</param>
		internal void FlushEntityNow(uint entityId)
		{
			IDistributedEntity entity = DistributedObjects.GetValueOrDefault(entityId);
			if (entity != null)
			{
				FlushEntityNow(entity);
			}
		}

		/// <inheritdoc cref="FlushEntityNow(uint)"/>
		internal void FlushEntityNow(IDistributedEntity entity)
		{
			if (Connection == null || entity.DirtyBits == 0)
			{
				return;
			}

			// Drop it from the dirty set first: GetPropertyBytes clears the entity's dirty state, so leaving it
			// registered would make the next sweep send an empty update and burn a seq.
			DirtyObjects.Remove(entity);

			SendEntityUpdates(entity);
		}
	}

}
