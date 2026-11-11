// ───────── Transactions ─────────
//
// Transact: a scoped read-modify-write on one entity, the optimistic sibling of RunExclusive. The body reads the
// entity's confirmed values, sets fields, and the result is sent as an exclusive update (UpdateExclusive's per-field
// staleness check). A stale rejection reruns the body against the winner's values, which are already applied locally
// when the rejection arrives, and sends again.
//
// What makes it more than "UpdateExclusive in a retry loop" is the per-entity queue kept here:
//
//   • Self-races. On an entity that is not client-authoritative a Set is not readable until the server echoes it, and
//     a second exclusive update sent before the first one's reply would be rejected as stale against this client's own
//     write. So at most one transaction per entity is active on a connection, and its body only runs while none of
//     this connection's tracked writes to the entity are in flight. Tracked writes are: plain UpdateExclusive calls
//     (always), the flush of changes already pending when a body is about to run, and per-frame sweep sends made while
//     a transaction is waiting on the entity. Each reply arrives after the echo of the write it answers, so when the
//     count reaches zero, Get() returns this connection's own writes.
//   • Replicated actions riding a tracked write. Their reply arrives after the update's own, and application state
//     often hangs on it (an inventory slot reserved for a pickup is released when the pickup's insert reports it did
//     not run). So each one also counts as in flight until its own callback has run: a rerun after a stale rejection,
//     and the next queued transaction, see the application's reaction to the previous attempt's actions.
//   • Changes pending from before the body. Fields dirtied by plain Sets are flushed as an ordinary update and the
//     body waits for its reply. The body's dirty bits are then exactly its own, which is what lets a decline drop them
//     without touching anybody else's changes, and the exclusive update's staleness check is not tripped by them.
//
// Unguaranteed writes (SetUnguaranteed) sent by the sweep are not tracked: they may never be answered, and they sit
// outside every ordering guarantee anyway.

using System;
using System.Collections.Generic;
using System.IO;

using Impunity.GameState;

namespace Impunity.Connection
{

	/// <summary>Outcome of a <c>Transact</c> call.</summary>
	public enum TransactResult
	{
		/// <summary>The body returned true and its changes were accepted by the server (or there was nothing to send).</summary>
		Committed,
		/// <summary>The body returned false. Nothing was sent; the body's pending changes and replicated actions were
		/// discarded (except on client-authoritative entities, see <c>IDistributedEntity.Transact</c>).</summary>
		Declined,
		/// <summary>Every attempt was rejected as stale: other clients kept changing the fields the body wrote. Nothing
		/// from the body was applied.</summary>
		Conflict,
		/// <summary>The body threw, the entity went away, or the update failed for another reason (locked by another
		/// connection, connection broken). The error argument carries the detail.</summary>
		Failed,
	}

	public partial class ClientEntityManager
	{
		/// <summary>How many times <c>Transact</c> runs its body before giving up with <see cref="TransactResult.Conflict"/>,
		/// for calls that do not name a limit (the default). Counts every run, the first included.</summary>
		public int DefaultTransactMaxAttempts = 5;

		/// <summary>One queued or running transaction.</summary>
		private class Transaction
		{
			public Func<bool> Body = default!;
			public ImpunityCallback<TransactResult>? OnComplete;
			public int MaxAttempts;
			/// <summary>Runs of the body so far.</summary>
			public int Attempts;
			/// <summary>True while this transaction's exclusive update is on the wire.</summary>
			public bool AwaitingReply;
			/// <summary>True once the outcome has been reported, so a late reply cannot report it twice.</summary>
			public bool Finished;
		}

		/// <summary>Per-entity bookkeeping: this connection's in-flight writes, and its transactions.</summary>
		private class EntityTransactState
		{
			public IDistributedEntity Entity = default!;
			/// <summary>Tracked writes to the entity still awaiting their reply.</summary>
			public int InFlightWrites;
			/// <summary>The transaction whose body runs next, or whose update is in flight.</summary>
			public Transaction? Active;
			/// <summary>Transactions waiting behind <see cref="Active"/>, in call order.</summary>
			public Queue<Transaction>? Queued;
			/// <summary>Guards <see cref="AdvanceTransactions"/> against reentry from a body or a synchronous outcome.</summary>
			public bool Advancing;
		}

		/// <summary>Keyed by instance rather than id: a re-created entity is a new registration, and a late reply for
		/// its previous life must not touch the new state.</summary>
		private Dictionary<IDistributedEntity, EntityTransactState>? TransactStates;

		// ═══════════════════════════════════════════════════════════
		// Transact
		// ═══════════════════════════════════════════════════════════

		/// <summary>Runs a read-modify-write on <paramref name="entity"/> and commits it as an exclusive update, rerunning
		/// on staleness. Backs <see cref="IDistributedEntity.Transact"/>; see there for the contract.</summary>
		/// <param name="entity">The entity to change.</param>
		/// <param name="body">Reads and sets the entity's fields; returns true to commit, false to send nothing.</param>
		/// <param name="onComplete">Invoked once with the outcome. Never runs reentrantly when the manager has a
		/// connection.</param>
		/// <param name="maxAttempts">Runs of the body before <see cref="TransactResult.Conflict"/>. Negative uses
		/// <see cref="DefaultTransactMaxAttempts"/>; values below 1 are raised to 1.</param>
		internal void Transact(IDistributedEntity entity, Func<bool> body, ImpunityCallback<TransactResult>? onComplete, int maxAttempts)
		{
			if (body == null)
			{
				throw new ArgumentNullException(nameof(body));
			}

			if (Connection == null)
			{
				TransactOffline(body, onComplete);
				return;
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				ImpunityCallback<TransactResult>? notRegistered = onComplete;
				if (notRegistered != null)
				{
					Connection.QueueLocalCallback(err => notRegistered(err, TransactResult.Failed),
						new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				}
				return;
			}

			Transaction tx = new Transaction
			{
				Body = body,
				OnComplete = onComplete,
				MaxAttempts = Math.Max(1, maxAttempts < 0 ? DefaultTransactMaxAttempts : maxAttempts),
			};

			EntityTransactState state = GetOrCreateTransactState(entity);
			if (state.Active != null)
			{
				(state.Queued ??= new Queue<Transaction>()).Enqueue(tx);
				return;
			}

			state.Active = tx;
			AdvanceTransactions(state);
		}

		/// <summary>Runs a transaction body for an entity that has no connection to send through. <c>Set</c> already
		/// applies locally there, so the body's own return value is the outcome; nothing can be undone on a decline.
		/// There is no queue to defer through, so <paramref name="onComplete"/> fires immediately, as every other
		/// no-connection request does.</summary>
		/// <param name="body">The transaction body.</param>
		/// <param name="onComplete">Receives <see cref="TransactResult.Committed"/>, <see cref="TransactResult.Declined"/>,
		/// or <see cref="TransactResult.Failed"/> if the body threw.</param>
		internal static void TransactOffline(Func<bool> body, ImpunityCallback<TransactResult>? onComplete)
		{
			if (body == null)
			{
				throw new ArgumentNullException(nameof(body));
			}

			bool commit;
			try
			{
				commit = body();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in Transact body", e);
				onComplete?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Exception in Transact body: " + e.Message), TransactResult.Failed);
				return;
			}

			onComplete?.Invoke(null, commit ? TransactResult.Committed : TransactResult.Declined);
		}

		private EntityTransactState GetOrCreateTransactState(IDistributedEntity entity)
		{
			TransactStates ??= new Dictionary<IDistributedEntity, EntityTransactState>();
			if (!TransactStates.TryGetValue(entity, out EntityTransactState? state))
			{
				state = new EntityTransactState { Entity = entity };
				TransactStates[entity] = state;
			}
			return state;
		}

		/// <summary>True when <paramref name="state"/> is still the live bookkeeping for its entity — false once the
		/// entity has been unregistered (and possibly registered again under a fresh state).</summary>
		private bool IsCurrentTransactState(EntityTransactState state)
		{
			return TransactStates != null && TransactStates.TryGetValue(state.Entity, out EntityTransactState? current) && current == state;
		}

		private void ForgetTransactStateIfIdle(EntityTransactState state)
		{
			if (state.Active == null && (state.Queued == null || state.Queued.Count == 0) && state.InFlightWrites == 0 && !state.Advancing
				&& IsCurrentTransactState(state))
			{
				TransactStates!.Remove(state.Entity);
			}
		}

		/// <summary>True when a transaction is waiting or running on the entity, so the per-frame sweep should track
		/// its sends.</summary>
		private bool HasTransactionPending(IDistributedEntity entity)
		{
			return TransactStates != null && TransactStates.TryGetValue(entity, out EntityTransactState? state) && state.Active != null;
		}

		/// <summary>Counts a write to <paramref name="entity"/> as in flight until its reply arrives, and returns the
		/// callback to send it with. The reply invokes <paramref name="onComplete"/> first, then releases the count, so a
		/// caller reacting to its own result is ordered ahead of any transaction that was waiting on it.</summary>
		private ImpunityCallback TrackWrite(IDistributedEntity entity, ImpunityCallback? onComplete)
		{
			EntityTransactState state = GetOrCreateTransactState(entity);
			state.InFlightWrites++;

			return err =>
			{
				try
				{
					onComplete?.Invoke(err);
				}
				finally
				{
					if (IsCurrentTransactState(state))
					{
						state.InFlightWrites--;
						AdvanceTransactions(state);
					}
				}
			};
		}

		/// <summary>Counts a replicated action riding a tracked write as in flight until it has resolved: after its own
		/// callback has run (the skip or result reply, a local failure, or a broken connection), the count is released.
		/// A transaction body on the entity therefore never runs between an update's reply and its actions' replies.</summary>
		private void TrackReplicatedAction(IDistributedEntity entity, GameStateActionBase? action)
		{
			if (action == null)
			{
				return;
			}

			EntityTransactState state = GetOrCreateTransactState(entity);
			state.InFlightWrites++;

			action.AfterCallback += () =>
			{
				if (IsCurrentTransactState(state))
				{
					state.InFlightWrites--;
					AdvanceTransactions(state);
				}
			};
		}

		/// <summary>Moves an entity's transactions forward as far as they can go right now: starts the next queued one,
		/// runs a body once nothing is in flight, and keeps going while outcomes resolve synchronously. Iterative, so a
		/// long queue of bodies that decline at once does not recurse.</summary>
		private void AdvanceTransactions(EntityTransactState state)
		{
			if (state.Advancing)
			{
				// Called from inside a body or an outcome on this entity; the outer loop picks the work up.
				return;
			}

			state.Advancing = true;
			try
			{
				while (IsCurrentTransactState(state))
				{
					if (state.Active == null)
					{
						if (state.Queued == null || state.Queued.Count == 0)
						{
							break;
						}
						state.Active = state.Queued.Dequeue();
					}

					Transaction tx = state.Active;
					if (tx.AwaitingReply || state.InFlightWrites > 0)
					{
						break;
					}

					// Changes pending from outside this transaction go first, as an ordinary update whose reply we wait
					// for. Afterwards everything dirty on the entity belongs to the body.
					if (state.Entity.DirtyBits != 0 || HasPendingReplicatedAction(state.Entity))
					{
						DirtyObjects.Remove(state.Entity);
						SendEntityUpdates(state.Entity, forTransaction: true);
						break;
					}

					RunTransactionBody(state, tx);

					if (state.Active == tx)
					{
						// Its update is on the wire; the reply moves things on.
						break;
					}
				}
			}
			finally
			{
				state.Advancing = false;
			}

			ForgetTransactStateIfIdle(state);
		}

		private void RunTransactionBody(EntityTransactState state, Transaction tx)
		{
			IDistributedEntity entity = state.Entity;
			tx.Attempts++;

			bool commit;
			try
			{
				commit = tx.Body();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in Transact body", e);
				if (!tx.Finished)
				{
					DiscardTransactionChanges(entity, "the Transact body threw");
					FinishTransaction(state, tx, TransactResult.Failed,
						new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Exception in Transact body: " + e.Message));
				}
				return;
			}

			if (tx.Finished)
			{
				// The body unsubscribed or otherwise unregistered the entity, which already reported this transaction.
				return;
			}

			if (!commit)
			{
				DiscardTransactionChanges(entity, "the Transact body declined");
				FinishTransaction(state, tx, TransactResult.Declined, null);
				return;
			}

			if (entity.DirtyBits == 0 && !HasPendingReplicatedAction(entity))
			{
				FinishTransaction(state, tx, TransactResult.Committed, null);
				return;
			}

			tx.AwaitingReply = true;
			SendExclusiveNow(entity, err => OnTransactionReply(state, tx, err));
		}

		private void OnTransactionReply(EntityTransactState state, Transaction tx, ImpunityErrorResponse? err)
		{
			tx.AwaitingReply = false;
			if (tx.Finished || !IsCurrentTransactState(state))
			{
				return;
			}

			if (err == null)
			{
				FinishTransaction(state, tx, TransactResult.Committed, null);
				return;
			}

			if (err.ErrorCode == ImpunityErrorCode.ActionStaleData)
			{
				if (tx.Attempts >= tx.MaxAttempts)
				{
					FinishTransaction(state, tx, TransactResult.Conflict, null);
					return;
				}

				// Rerun: the winner's values are already applied locally. AdvanceTransactions runs the body again once
				// nothing else of ours is in flight on this entity.
				AdvanceTransactions(state);
				return;
			}

			FinishTransaction(state, tx, TransactResult.Failed, err);
		}

		/// <summary>Reports a transaction's outcome exactly once, through the local-callback queue, and hands the entity
		/// to the next queued transaction.</summary>
		private void FinishTransaction(EntityTransactState state, Transaction tx, TransactResult result, ImpunityErrorResponse? error)
		{
			if (tx.Finished)
			{
				return;
			}

			tx.Finished = true;
			if (state.Active == tx)
			{
				state.Active = null;
			}

			ReportTransaction(tx, result, error);
			AdvanceTransactions(state);
		}

		private void ReportTransaction(Transaction tx, TransactResult result, ImpunityErrorResponse? error)
		{
			ImpunityCallback<TransactResult>? onComplete = tx.OnComplete;
			if (onComplete == null)
			{
				return;
			}

			// Through the connection's local-callback queue, so it is delivered on Update() and never reentrantly —
			// including when the first run of the body happened inside the Transact call itself.
			if (Connection != null)
			{
				Connection.QueueLocalCallback(err => onComplete(err, result), error);
			}
			else
			{
				onComplete(error, result);
			}
		}

		/// <summary>Drops what a declined or throwing body left pending on the entity, so no later send carries it.
		/// Its replicated actions are resolved as never sent. Field changes on a client-authoritative entity are kept:
		/// <c>Set</c> already applied them locally, and dropping the send would leave this client disagreeing with the
		/// server.</summary>
		private void DiscardTransactionChanges(IDistributedEntity entity, string reason)
		{
			if (!entity.IsClientAuthoritative && entity.DirtyBits != 0)
			{
				// Serializing a field's changes is what clears its pending state (WriteChangesTo), and GetPropertyBytes
				// clears the dirty bits. Write into the shared buffer and rewind, so the bytes are simply discarded.
				Stream stream = PropertyEncodingWriter.BaseStream;
				long position = stream.Position;
				GetPropertyBytes(entity, out _);
				stream.Position = position;
				DirtyObjects.Remove(entity);
			}

			if (PendingReplicatedActions.TryGetValue(entity, out List<GameStateActionBase>? pending))
			{
				PendingReplicatedActions.Remove(entity);
				foreach (GameStateActionBase action in pending)
				{
					Connection?.QueueLocalFailure(action, new ImpunityErrorResponse(ImpunityErrorCode.ActionConditionNotMet,
						"Not sent: " + reason));
				}
			}
		}

		/// <summary>Fails every transaction on an entity that is going away: the active one (unless already reported)
		/// and every one queued behind it. Their bodies do not run again. Called from <see cref="UnregisterEntity"/>.</summary>
		private void FailTransactionsForEntity(IDistributedEntity entity)
		{
			if (TransactStates == null || !TransactStates.TryGetValue(entity, out EntityTransactState? state))
			{
				return;
			}

			TransactStates.Remove(entity);

			ImpunityErrorResponse error = new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound,
				"Entity " + entity.DistributedEntityId + " was removed (deleted or unsubscribed) before its transaction completed");

			Transaction? active = state.Active;
			state.Active = null;
			if (active != null && !active.Finished)
			{
				active.Finished = true;
				ReportTransaction(active, TransactResult.Failed, error);
			}

			if (state.Queued != null)
			{
				while (state.Queued.Count > 0)
				{
					Transaction queued = state.Queued.Dequeue();
					queued.Finished = true;
					ReportTransaction(queued, TransactResult.Failed, error);
				}
			}
		}
	}

}
