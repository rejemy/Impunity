// ───────── Exclusive Scope Tests ─────────
//
// Covers entity.RunExclusive: the handoff guarantee (the next holder's body sees the previous holder's
// edits), FIFO waiter ordering, acquisition timeouts, exception safety, same-client serialization, and
// release on disconnect.
//
// Several tests need a scope to STAY open while another client queues behind it. That is what the
// TaskCompletionSource "gate" pattern is for: the body awaits the gate, so the lock is held until the
// test completes it. The continuation resumes inline on the thread that calls SetResult — the test's own
// pumping thread — which is the main-thread contract the rest of the async API relies on.
#nullable disable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class ExclusiveScopeTests : ImpunityTestHarness
	{
		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = IntegrationTestCollections.ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(IntegrationTestEntity),
					typeof(IntegrationTestChannel)
				}
			);
		}

		// ───────── Helpers ─────────

		/// <summary>Opens an extra local client. Every LocalGameConnection shares the hard-coded "local_key"
		/// ConnectionKey, so the server would treat them as the SAME lock owner — give each its own identity.</summary>
		async Task<LocalGameConnection> ConnectExtraLocal(string key)
		{
			var conn = Track(new LocalGameConnection(GameServer, Format));
			conn.ConnectionKey = key;
			await Pump(conn.ConnectAsync(), AllConnections());
			Assert.IsTrue(conn.Connected, "Extra local connection failed");
			return conn;
		}

		/// <summary>Subscribes <paramref name="count"/> independent local clients to one channel, creates a single
		/// entity on the first, and waits until every client has replicated it. Returns each client's own instance.</summary>
		async Task<List<IntegrationTestEntity>> SetupClients(string channelName, int count)
		{
			CreateServer();

			var channels = new List<IntegrationTestChannel>();
			for (int i = 0; i < count; i++)
			{
				var conn = await ConnectExtraLocal(channelName + "_client" + i);
				var channel = i == 0
					? await Pump(conn.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections())
					: await Pump(conn.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());
				channels.Add(channel);
			}

			var created = new IntegrationTestEntity();
			created.Health.Set(0);
			await Pump(channels[0].Manager.CreateObjectAsync(created, channels[0], false), AllConnections());

			await PumpUntil(() => AllHaveEntity(channels), TimeSpan.FromSeconds(5), AllConnections());

			var entities = new List<IntegrationTestEntity>();
			foreach (var channel in channels)
			{
				entities.Add(FirstEntity(channel));
			}
			return entities;
		}

		static bool AllHaveEntity(List<IntegrationTestChannel> channels)
		{
			foreach (var channel in channels)
			{
				if (channel.DistributedObjects.Count == 0) return false;
			}
			return true;
		}

		static IntegrationTestEntity FirstEntity(IntegrationTestChannel channel)
		{
			foreach (var obj in channel.DistributedObjects.Values)
			{
				return obj as IntegrationTestEntity;
			}
			return null;
		}

		// ───────── Tests ─────────

		/// <summary>
		/// The headline guarantee. A edits fields inside its scope and exits; B, queued behind it, must observe
		/// those edits before its own body runs. Fails if the unlock is allowed to overtake the field flush.
		/// </summary>
		[Test, Category("Locks")]
		public async Task RunExclusive_NextHolderSeesPreviousEdits()
		{
			var entities = await SetupClients("scope_handoff", 2);
			var aEntity = entities[0];
			var bEntity = entities[1];

			// A takes the lock and holds it open until the gate is released. The edit is made AFTER the gate, so
			// that the write and the scope exit happen in the same turn with no Update() in between — which is
			// exactly the case the flush-before-unlock exists for. Editing before the gate would let the ordinary
			// per-frame sweep send the update while the test pumps, hiding the bug.
			var aGate = new TaskCompletionSource<bool>();
			bool aBodyStarted = false;
			var aScope = aEntity.RunExclusiveAsync(async () =>
			{
				aBodyStarted = true;
				await aGate.Task;
				aEntity.Health.Set(7);
			}, 5f);

			await PumpUntil(() => aBodyStarted, TimeSpan.FromSeconds(5), AllConnections());

			// B queues behind A.
			int bObserved = -1;
			bool bBodyRan = false;
			var bScope = bEntity.RunExclusiveAsync(() =>
			{
				bObserved = bEntity.Health.Get();
				bEntity.Health.Set(8);
				bBodyRan = true;
			}, 5f);

			await PumpFor(TimeSpan.FromSeconds(0.5), AllConnections());
			Assert.IsFalse(bBodyRan, "B's body ran while A still held the lock");

			// A exits: its edit is flushed, then the lock is released and handed to B.
			aGate.SetResult(true);

			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(aScope, AllConnections()), "A's scope should have run");
			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(bScope, AllConnections()), "B's scope should have run");

			Assert.AreEqual(7, bObserved, "B's body did not observe A's edit — the unlock overtook the field update");

			await PumpUntil(() => aEntity.Health.Get() == 8 && bEntity.Health.Get() == 8, TimeSpan.FromSeconds(5), AllConnections());
		}

		/// <summary>Waiters are granted the lock in request order, not thrown into a race when it frees.</summary>
		[Test, Category("Locks")]
		public async Task RunExclusive_WaitersRunInRequestOrder()
		{
			var entities = await SetupClients("scope_fifo", 4);

			var holderGate = new TaskCompletionSource<bool>();
			bool holderStarted = false;
			var holderScope = entities[0].RunExclusiveAsync(async () =>
			{
				holderStarted = true;
				await holderGate.Task;
			}, 10f);

			await PumpUntil(() => holderStarted, TimeSpan.FromSeconds(5), AllConnections());

			// Queue the other three, in order, while the lock is held.
			var order = new List<int>();
			var waiterScopes = new List<Task<RunExclusiveResult>>();
			for (int i = 1; i < entities.Count; i++)
			{
				int index = i;
				waiterScopes.Add(entities[index].RunExclusiveAsync(() => order.Add(index), 10f));

				// Pump between requests so each one reaches the server's queue before the next is sent.
				await PumpFor(TimeSpan.FromSeconds(0.2), AllConnections());
			}

			Assert.AreEqual(0, order.Count, "No waiter should run while the lock is held");

			holderGate.SetResult(true);
			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(holderScope, AllConnections()));

			foreach (var scope in waiterScopes)
			{
				Assert.AreEqual(RunExclusiveResult.Ran, await Pump(scope, AllConnections()), "Every queued waiter should run");
			}

			CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order, "Waiters should be granted the lock in request order");
		}

		/// <summary>A scope that never gets the lock times out, and its body is never run.</summary>
		[Test, Category("Locks")]
		public async Task RunExclusive_TimesOutWhileLockHeld()
		{
			var entities = await SetupClients("scope_timeout", 2);

			var holderGate = new TaskCompletionSource<bool>();
			bool holderStarted = false;
			var holderScope = entities[0].RunExclusiveAsync(async () =>
			{
				holderStarted = true;
				await holderGate.Task;
			}, 5f);

			await PumpUntil(() => holderStarted, TimeSpan.FromSeconds(5), AllConnections());

			bool waiterBodyRan = false;
			var waiterScope = entities[1].RunExclusiveAsync(() => waiterBodyRan = true, 0.5f);

			Assert.AreEqual(RunExclusiveResult.TimedOut, await Pump(waiterScope, AllConnections()),
				"A scope that never acquires the lock should time out");
			Assert.IsFalse(waiterBodyRan, "A timed-out scope must not run its body");

			// The holder is unaffected and still exits cleanly.
			holderGate.SetResult(true);
			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(holderScope, AllConnections()));
		}

		/// <summary>A throwing body still flushes its edits and still releases the lock.</summary>
		[Test, Category("Locks")]
		public async Task RunExclusive_BodyExceptionFlushesAndReleases()
		{
			var entities = await SetupClients("scope_throw", 2);
			var aEntity = entities[0];
			var bEntity = entities[1];

			ImpunityErrorResponse error = null;
			RunExclusiveResult result = RunExclusiveResult.Ran;
			bool done = false;

			aEntity.RunExclusive(() =>
			{
				aEntity.Health.Set(42);
				throw new InvalidOperationException("boom");
			}, (err, res) => { error = err; result = res; done = true; }, 5f);

			await PumpUntil(() => done, TimeSpan.FromSeconds(5), AllConnections());

			Assert.AreEqual(RunExclusiveResult.Failed, result, "A throwing body should report Failed");
			Assert.IsNotNull(error, "A throwing body should report an error");

			// The edit made before the throw is still flushed to everyone.
			await PumpUntil(() => bEntity.Health.Get() == 42, TimeSpan.FromSeconds(5), AllConnections());

			// And the lock was released, so the next client gets it straight away.
			bool bRan = false;
			var bScope = bEntity.RunExclusiveAsync(() => bRan = true, 2f);
			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(bScope, AllConnections()),
				"The lock should have been released despite the exception");
			Assert.IsTrue(bRan);
		}

		/// <summary>
		/// Two scopes on one entity from ONE connection run sequentially, never overlapping. The server treats a
		/// second lock from the same ConnectionKey as re-entrant, so without local serialization the inner scope's
		/// unlock would release the outer scope's lock — and the duplicate registration used to throw server-side.
		/// </summary>
		[Test, Category("Locks")]
		public async Task RunExclusive_SameClientScopesSerialize()
		{
			var entities = await SetupClients("scope_serial", 1);
			var entity = entities[0];

			var order = new List<string>();
			int overlap = 0;
			int maxOverlap = 0;

			var firstGate = new TaskCompletionSource<bool>();
			var first = entity.RunExclusiveAsync(async () =>
			{
				order.Add("first-enter");
				maxOverlap = Math.Max(maxOverlap, ++overlap);
				await firstGate.Task;
				overlap--;
				order.Add("first-exit");
			}, 5f);

			await PumpUntil(() => order.Count == 1, TimeSpan.FromSeconds(5), AllConnections());

			var second = entity.RunExclusiveAsync(() =>
			{
				order.Add("second-enter");
				maxOverlap = Math.Max(maxOverlap, ++overlap);
				overlap--;
			}, 5f);

			await PumpFor(TimeSpan.FromSeconds(0.3), AllConnections());
			Assert.AreEqual(1, order.Count, "The second scope must wait for the first to exit");

			firstGate.SetResult(true);

			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(first, AllConnections()));
			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(second, AllConnections()));

			Assert.AreEqual(1, maxOverlap, "Two scopes on one entity from one client must never overlap");
			CollectionAssert.AreEqual(new[] { "first-enter", "first-exit", "second-enter" }, order);
		}

		/// <summary>
		/// Regression: locking an entity twice from one connection. GameStateEntity.Lock is re-entrant by
		/// ConnectionKey and returns true, but the second call used to re-register the entity on the replicant —
		/// a duplicate key in LocksHeld, which threw and surfaced as an InternalServerError.
		/// </summary>
		[Test, Category("Locks")]
		public async Task TryLock_TwiceFromSameConnectionIsReentrant()
		{
			var entities = await SetupClients("scope_reentrant", 2);

			Assert.IsTrue(await Pump(entities[0].TryLockAsync(), AllConnections()), "First lock should succeed");
			Assert.IsTrue(await Pump(entities[0].TryLockAsync(), AllConnections()), "Re-locking your own lock should succeed, not error");

			// One release is enough — the second lock did not stack.
			Assert.IsTrue(await Pump(entities[0].UnlockAsync(), AllConnections()), "Unlock should release the lock");
			Assert.IsTrue(await Pump(entities[1].TryLockAsync(), AllConnections()), "The other client should be able to take it now");
		}

		/// <summary>A lock held by a client that disconnects is released and handed to whoever is queued.</summary>
		[Test, Category("Locks")]
		public async Task RunExclusive_GrantedAfterHolderDisconnects()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var localChannel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("scope_disconnect", new IntegrationTestChannel()), AllConnections());
			var remoteChannel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("scope_disconnect", null), AllConnections());

			var created = new IntegrationTestEntity();
			await Pump(LocalGame.EntityManager.CreateObjectAsync(created, localChannel, false), AllConnections());
			await PumpUntil(() => remoteChannel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(5), AllConnections());

			var localEntity = FirstEntity(localChannel);
			var remoteEntity = FirstEntity(remoteChannel);

			// The remote client takes the lock the plain way, then vanishes without releasing it.
			Assert.IsTrue(await Pump(remoteEntity.TryLockAsync(), AllConnections()), "Remote client should acquire the lock");
			await PumpUntil(() => localEntity.IsLocked, TimeSpan.FromSeconds(5), AllConnections());

			bool localRan = false;
			var localScope = localEntity.RunExclusiveAsync(() => localRan = true, 10f);

			await PumpFor(TimeSpan.FromSeconds(0.3), AllConnections());
			Assert.IsFalse(localRan, "The queued scope must not run while the lock is held");

			DisposeConnection(RemoteGame);

			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(localScope, AllConnections()),
				"The lock should have been released on disconnect and granted to the waiter");
			Assert.IsTrue(localRan);
		}

		/// <summary>
		/// The handoff guarantee is not exclusive to RunExclusive: a hand-rolled TryLock / edit / Unlock must give
		/// the next holder the same view, because Unlock flushes pending edits before releasing. Without that flush
		/// the next holder deterministically reads stale values — the server hands the lock straight over, so
		/// there is no re-race to give a late update time to land.
		/// </summary>
		[Test, Category("Locks")]
		public async Task ManualUnlock_FlushesEditsBeforeReleasing()
		{
			var entities = await SetupClients("manual_handoff", 2);
			var aEntity = entities[0];
			var bEntity = entities[1];

			Assert.IsTrue(await Pump(aEntity.TryLockAsync(), AllConnections()), "A should acquire the lock");

			int bObserved = -1;
			bool bGotLock = false;
			bEntity.WaitForLock((err, res) =>
			{
				bObserved = bEntity.Health.Get();
				bGotLock = true;
			});

			await PumpFor(TimeSpan.FromSeconds(0.3), AllConnections());
			Assert.IsFalse(bGotLock, "B should still be queued while A holds the lock");

			// The hand-rolled exit: write and release in one turn, with no Update() in between.
			aEntity.Health.Set(7);
			aEntity.Unlock(null);

			await PumpUntil(() => bGotLock, TimeSpan.FromSeconds(5), AllConnections());
			Assert.AreEqual(7, bObserved, "The next lock holder did not see the previous holder's edit");
		}

		/// <summary>
		/// Documents the cost of the FIFO queue for hand-rolled retry loops: when the lock is handed straight to a
		/// queued waiter it never becomes free, so listeners see OnLocked for the new holder and NOT OnUnlocked. A
		/// client polling TryLock on the OnUnlocked signal will not be woken — use WaitForLock or RunExclusive,
		/// which queue and are served in turn.
		/// </summary>
		[Test, Category("Locks")]
		public async Task Handoff_DoesNotRaiseOnUnlockedForOtherListeners()
		{
			var entities = await SetupClients("manual_poller", 3);

			Assert.IsTrue(await Pump(entities[0].TryLockAsync(), AllConnections()), "A should acquire the lock");

			int unlockedEvents = 0;
			int lockedEvents = 0;
			entities[2].OnUnlockedEvent += () => unlockedEvents++;
			entities[2].OnLockedEvent += () => lockedEvents++;

			bool bGotLock = false;
			entities[1].WaitForLock((err, res) => bGotLock = true);
			await PumpFor(TimeSpan.FromSeconds(0.3), AllConnections());

			await Pump(entities[0].UnlockAsync(), AllConnections());
			await PumpUntil(() => bGotLock, TimeSpan.FromSeconds(5), AllConnections());

			Assert.AreEqual(0, unlockedEvents, "A handed-off lock never becomes free, so OnUnlocked must not fire");
			Assert.AreEqual(1, lockedEvents, "Other listeners should see the new holder take the lock");
			Assert.IsTrue(entities[2].IsLocked, "The entity is still locked, now by the waiter");

			// And once the last holder releases with nobody queued, OnUnlocked does fire.
			await Pump(entities[1].UnlockAsync(), AllConnections());
			await PumpUntil(() => unlockedEvents == 1, TimeSpan.FromSeconds(5), AllConnections());
			Assert.IsFalse(entities[2].IsLocked);
		}

		/// <summary>WaitForLock now hands you the lock rather than telling you it is free to race for.</summary>
		[Test, Category("Locks")]
		public async Task WaitForLock_GrantsTheLock()
		{
			var entities = await SetupClients("scope_waitforlock", 2);

			Assert.IsTrue(await Pump(entities[0].TryLockAsync(), AllConnections()), "First client should acquire the lock");

			var waitTask = entities[1].WaitForLockAsync();
			await PumpFor(TimeSpan.FromSeconds(0.3), AllConnections());
			Assert.IsFalse(waitTask.IsCompleted, "The wait should not resolve while the lock is held");

			await Pump(entities[0].UnlockAsync(), AllConnections());

			Assert.AreEqual(LockWaitResult.Locked, await Pump(waitTask, AllConnections()),
				"A queued waiter should be handed the lock, not merely told it is available");

			// Proof it really holds it: the original holder cannot take it back.
			Assert.IsFalse(await Pump(entities[0].TryLockAsync(), AllConnections()),
				"The lock should now be held by the waiter");
		}
	}
}
