// ───────── Transact Tests ─────────
//
// Covers entity.Transact: commit, decline (nothing sent, the body's Sets and replicated actions dropped), reruns on
// staleness against the winner's values, the attempt limit, no lost updates between two contending clients, per-entity
// FIFO on one connection (no self-races), waiting for this connection's in-flight UpdateExclusive and for the replicated
// actions of in-flight and rejected attempts, changes pending from
// before the transaction, client-authoritative entities, failure on delete/unsubscribe and on a throwing body, and the
// never-reentrant completion.
//
// Determinism: local connections hand every action to the in-proc server as soon as it is sent, but a client only sees
// what the server pushed it when the test pumps that client. So "keep B stale" means: let A's write reach the server and
// pump only A, then make B transact. B's body runs against the old value, its update is rejected, and the echo of A's
// write is already waiting in B's queue ahead of the rejection.
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
	public class TransactTests : ImpunityTestHarness
	{
		const int ITEMS = IntegrationTestCollections.ITEMS;

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(IntegrationTestEntity),
					typeof(IntegrationTestChannel)
				}
			);
		}

		// ───────── Helpers ─────────

		/// <summary>Records a transaction's outcome.</summary>
		sealed class TxProbe
		{
			public bool Fired;
			public int FireCount;
			public TransactResult Result;
			public ImpunityErrorResponse Error;

			public void OnComplete(ImpunityErrorResponse err, TransactResult result)
			{
				Fired = true;
				FireCount++;
				Error = err;
				Result = result;
			}
		}

		async Task<LocalGameConnection> ConnectClient(string key)
		{
			var conn = Track(new LocalGameConnection(GameServer, Format));
			conn.ConnectionKey = key;
			await Pump(conn.ConnectAsync(), AllConnections());
			Assert.IsTrue(conn.Connected, "Local connection failed");
			return conn;
		}

		/// <summary>Creates the server, subscribes <paramref name="count"/> distinct clients to one channel, creates one
		/// entity (Health 0) on the first, and waits until every client has it.</summary>
		async Task<(List<LocalGameConnection> conns, List<IntegrationTestEntity> entities, List<IntegrationTestChannel> channels)> Setup(
			string channelName, int count, bool clientAuthoritative = false)
		{
			CreateServer();

			var conns = new List<LocalGameConnection>();
			var channels = new List<IntegrationTestChannel>();
			for (int i = 0; i < count; i++)
			{
				var conn = await ConnectClient(channelName + "_client" + i);
				conns.Add(conn);
				var channel = i == 0
					? await Pump(conn.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections())
					: await Pump(conn.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());
				channels.Add(channel);
			}

			var created = new IntegrationTestEntity { IsClientAuthoritative = clientAuthoritative };
			created.DisplayName.Set("start");
			await Pump(channels[0].Manager.CreateObjectAsync(created, channels[0], false), AllConnections());
			await PumpUntil(() => channels.TrueForAll(c => c.DistributedObjects.Count == 1), TimeSpan.FromSeconds(5), AllConnections());

			var entities = new List<IntegrationTestEntity>();
			foreach (var channel in channels)
			{
				foreach (var obj in channel.DistributedObjects.Values)
				{
					entities.Add((IntegrationTestEntity)obj);
				}
			}
			Assert.AreSame(created, entities[0]);
			return (conns, entities, channels);
		}

		Task<BsonDocument> FindDoc(BaseGameConnection conn, string id)
		{
			return Pump(conn.FindDocumentByIdAsync(ITEMS, id), AllConnections());
		}

		/// <summary>A transaction body that adds one to Health, counting its runs.</summary>
		static Func<TransactScope, bool> Increment(IntegrationTestEntity entity, Action onRun = null)
		{
			return _ =>
			{
				onRun?.Invoke();
				entity.Health.Set(entity.Health.Get() + 1);
				return true;
			};
		}

		// ───────── Commit ─────────

		[Test, Category("Transact")]
		public async Task Transact_Uncontended_CommitsAndReplicates()
		{
			var (conns, entities, _) = await Setup("tx_commit", 2);
			var a = entities[0];
			var b = entities[1];

			var probe = new TxProbe();
			int runs = 0;
			a.Transact(_ =>
			{
				runs++;
				a.Health.Set(a.Health.Get() + 5);
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());

			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.IsNull(probe.Error);
			Assert.AreEqual(1, runs, "An uncontended transaction should run its body once");
			Assert.AreEqual(5, a.Health.Get(), "The committed value should be confirmed locally when the callback runs");
			await PumpUntil(() => b.Health.Get() == 5, AllConnections());
		}

		[Test, Category("Transact")]
		public async Task Transact_NothingSet_CommitsWithoutSending()
		{
			var (conns, entities, _) = await Setup("tx_noop", 1);
			var a = entities[0];
			ushort seqBefore = a.SendSeq;

			var probe = new TxProbe();
			a.Transact(_ => true, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.AreEqual(seqBefore, a.SendSeq, "A body that sets nothing should not send an update");
		}

		// ───────── Decline ─────────

		/// <summary>A declined body sends nothing: its Set never reaches anyone (the writer included), and the action it
		/// attached is resolved as never sent.</summary>
		[Test, Category("Transact")]
		public async Task Transact_Declined_SendsNothingAndSkipsAction()
		{
			var (conns, entities, _) = await Setup("tx_decline", 2);
			var a = entities[0];
			var b = entities[1];
			ushort seqBefore = a.SendSeq;

			var actionProbe = new CallbackProbe<BsonValue>();
			var probe = new TxProbe();
			a.Transact(_ =>
			{
				a.Health.Set(99);
				a.AddReplicatedAction(new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = "declined" }, actionProbe.OnComplete));
				return false;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired && actionProbe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Declined, probe.Result);
			Assert.IsNull(probe.Error);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, actionProbe.Code, "A declined body's action must be resolved as not run");

			// Give any stray send every chance to arrive.
			await PumpFor(TimeSpan.FromSeconds(0.3), AllConnections());

			Assert.AreEqual(seqBefore, a.SendSeq, "Nothing should have been sent for a declined body");
			Assert.AreEqual(0ul, a.DirtyBits, "The declined body's change must not stay pending");
			Assert.AreEqual(0, a.Health.Get());
			Assert.AreEqual(0, b.Health.Get(), "The declined Set reached another client");
			Assert.IsNull(await FindDoc(conns[0], "declined"), "The declined body's action ran");

			// A later unrelated write does not carry the declined value along.
			a.DisplayName.Set("later");
			await PumpUntil(() => b.DisplayName.Get() == "later", AllConnections());
			Assert.AreEqual(0, b.Health.Get(), "The declined Set leaked into a later update");
		}

		/// <summary>Plain Sets made before the transaction are flushed ahead of the body, so a decline keeps them.</summary>
		[Test, Category("Transact")]
		public async Task Transact_DeclineKeepsChangesPendingFromBefore()
		{
			var (conns, entities, _) = await Setup("tx_pending", 2);
			var a = entities[0];
			var b = entities[1];

			a.DisplayName.Set("before");
			a.Health.Set(3);

			string seenName = null;
			int seenHealth = -1;
			var probe = new TxProbe();
			a.Transact(_ =>
			{
				seenName = a.DisplayName.Get();
				seenHealth = a.Health.Get();
				a.Health.Set(50);
				return false;
			}, probe.OnComplete);

			Assert.IsNull(seenName, "The body must wait for the pending changes to be confirmed");

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Declined, probe.Result);
			Assert.AreEqual("before", seenName, "The body should see the changes pending from before it, confirmed");
			Assert.AreEqual(3, seenHealth);

			await PumpUntil(() => b.DisplayName.Get() == "before" && b.Health.Get() == 3, AllConnections());
			await PumpFor(TimeSpan.FromSeconds(0.2), AllConnections());
			Assert.AreEqual(3, b.Health.Get(), "The declined body's Set leaked");
		}

		/// <summary>The pre-flush means mixing a plain Set with a transaction on the same field costs no rerun.</summary>
		[Test, Category("Transact")]
		public async Task Transact_PlainSetBeforeOnSameField_NoRerun()
		{
			var (conns, entities, _) = await Setup("tx_mixed", 1);
			var a = entities[0];

			a.Health.Set(10);
			int runs = 0;
			var probe = new TxProbe();
			a.Transact(Increment(a, () => runs++), probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.AreEqual(1, runs);
			Assert.AreEqual(11, a.Health.Get());
		}

		// ───────── Contention ─────────

		/// <summary>Two clients each fire N increments at once. No update is lost, and the losing side really reran.</summary>
		[Test, Category("Transact")]
		public async Task Transact_TwoClientsConcurrentIncrements_NoLostUpdate()
		{
			const int N = 10;
			var (conns, entities, _) = await Setup("tx_race", 2);
			var a = entities[0];
			var b = entities[1];

			int runs = 0;
			var probes = new List<TxProbe>();
			for (int i = 0; i < N; i++)
			{
				var pa = new TxProbe();
				var pb = new TxProbe();
				probes.Add(pa);
				probes.Add(pb);
				a.Transact(Increment(a, () => runs++), pa.OnComplete, 50);
				b.Transact(Increment(b, () => runs++), pb.OnComplete, 50);
			}

			await PumpUntil(() => probes.TrueForAll(p => p.Fired), AllConnections());

			foreach (var p in probes)
			{
				Assert.AreEqual(TransactResult.Committed, p.Result, "Every increment should commit: " + p.Error?.Message);
			}
			await PumpUntil(() => a.Health.Get() == 2 * N && b.Health.Get() == 2 * N, AllConnections());
			Assert.Greater(runs, 2 * N, "Both clients' first transactions were sent against the same value, so one must have rerun");
		}

		/// <summary>Several transactions on one entity from one client in one frame queue instead of racing each other.</summary>
		[Test, Category("Transact")]
		public async Task Transact_SameClientBackToBack_QueuesWithoutReruns()
		{
			const int N = 8;
			var (conns, entities, _) = await Setup("tx_fifo", 2);
			var a = entities[0];
			var b = entities[1];

			int runs = 0;
			var order = new List<int>();
			var probes = new List<TxProbe>();
			for (int i = 0; i < N; i++)
			{
				int index = i;
				var p = new TxProbe();
				probes.Add(p);
				a.Transact(_ =>
				{
					runs++;
					order.Add(index);
					a.Health.Set(a.Health.Get() + 1);
					return true;
				}, p.OnComplete, 1);
			}

			Assert.AreEqual(1, runs, "Only the first body may run before its update is answered");

			await PumpUntil(() => probes.TrueForAll(p => p.Fired), AllConnections());

			foreach (var p in probes)
			{
				Assert.AreEqual(TransactResult.Committed, p.Result, "A queued transaction raced its predecessor: " + p.Error?.Message);
			}
			Assert.AreEqual(N, runs, "No transaction should have needed a rerun");
			CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, order, "Transactions must run in call order");
			Assert.AreEqual(N, a.Health.Get());
			await PumpUntil(() => b.Health.Get() == N, AllConnections());
		}

		/// <summary>The body does not run while this connection's plain UpdateExclusive is awaiting its reply.</summary>
		[Test, Category("Transact")]
		public async Task Transact_WaitsForInFlightUpdateExclusive()
		{
			var (conns, entities, _) = await Setup("tx_wait_excl", 1);
			var a = entities[0];

			bool exclusiveDone = false;
			bool exclusiveDoneWhenBodyRan = false;
			a.Health.Set(5);
			a.UpdateExclusive(err =>
			{
				Assert.IsNull(err);
				exclusiveDone = true;
			});

			int runs = 0;
			int seen = -1;
			var probe = new TxProbe();
			a.Transact(_ =>
			{
				runs++;
				exclusiveDoneWhenBodyRan = exclusiveDone;
				seen = a.Health.Get();
				a.Health.Set(seen + 1);
				return true;
			}, probe.OnComplete, 1);

			Assert.AreEqual(0, runs, "The body ran while the UpdateExclusive was in flight");

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, "With maxAttempts 1, any self-race would be a Conflict");
			Assert.IsTrue(exclusiveDoneWhenBodyRan, "The UpdateExclusive's own callback should run before the waiting body");
			Assert.AreEqual(5, seen, "The body should see the UpdateExclusive's value confirmed");
			Assert.AreEqual(6, a.Health.Get());
		}

		/// <summary>A stale client's body reruns against the winner's value.</summary>
		[Test, Category("Transact")]
		public async Task Transact_Rerun_SeesWinnersValue()
		{
			var (conns, entities, _) = await Setup("tx_rerun", 2);
			var a = entities[0];
			var b = entities[1];

			var aProbe = new TxProbe();
			a.Transact(Increment(a), aProbe.OnComplete);
			await PumpUntil(() => aProbe.Fired, conns[0]);
			Assert.AreEqual(TransactResult.Committed, aProbe.Result);
			Assert.AreEqual(0, b.Health.Get(), "B must still be stale for this test");

			var seen = new List<int>();
			var bProbe = new TxProbe();
			b.Transact(_ =>
			{
				seen.Add(b.Health.Get());
				b.Health.Set(b.Health.Get() + 1);
				return true;
			}, bProbe.OnComplete);

			await PumpUntil(() => bProbe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, bProbe.Result);
			CollectionAssert.AreEqual(new[] { 0, 1 }, seen, "The rerun should see A's committed value");
			await PumpUntil(() => a.Health.Get() == 2 && b.Health.Get() == 2, AllConnections());
		}

		/// <summary>Out of attempts against a stale client: Conflict, and nothing from the body is applied.</summary>
		[Test, Category("Transact")]
		public async Task Transact_MaxAttemptsOne_StaleIsConflict()
		{
			var (conns, entities, _) = await Setup("tx_conflict", 2);
			var a = entities[0];
			var b = entities[1];

			var aProbe = new TxProbe();
			a.Transact(_ => { a.Health.Set(7); return true; }, aProbe.OnComplete);
			await PumpUntil(() => aProbe.Fired, conns[0]);
			Assert.AreEqual(0, b.Health.Get(), "B must still be stale for this test");

			int runs = 0;
			var bProbe = new TxProbe();
			b.Transact(_ =>
			{
				runs++;
				b.Health.Set(100);
				return true;
			}, bProbe.OnComplete, maxAttempts: 1);

			await PumpUntil(() => bProbe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Conflict, bProbe.Result);
			Assert.AreEqual(1, runs);
			await PumpFor(TimeSpan.FromSeconds(0.2), AllConnections());
			Assert.AreEqual(7, a.Health.Get(), "The conflicting write must not apply");
			Assert.AreEqual(7, b.Health.Get());
		}

		/// <summary>A replicated action attached in the body is skipped on the stale run and runs exactly once, on the
		/// committed one.</summary>
		[Test, Category("Transact")]
		public async Task Transact_ReplicatedAction_RunsOnceAfterRerun()
		{
			var (conns, entities, _) = await Setup("tx_action", 2);
			var a = entities[0];
			var b = entities[1];

			var aProbe = new TxProbe();
			a.Transact(Increment(a), aProbe.OnComplete);
			await PumpUntil(() => aProbe.Fired, conns[0]);
			Assert.AreEqual(0, b.Health.Get(), "B must still be stale for this test");

			var actionProbes = new List<CallbackProbe<BsonValue>>();
			var bProbe = new TxProbe();
			b.Transact(_ =>
			{
				int next = b.Health.Get() + 1;
				b.Health.Set(next);
				var actionProbe = new CallbackProbe<BsonValue>();
				actionProbes.Add(actionProbe);
				// The same _id each run: a second successful insert would fail, so "ran once" is checked twice over.
				b.AddReplicatedAction(new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = "once", ["n"] = next }, actionProbe.OnComplete));
				return true;
			}, bProbe.OnComplete);

			await PumpUntil(() => bProbe.Fired && actionProbes.TrueForAll(p => p.Fired), AllConnections());
			Assert.AreEqual(TransactResult.Committed, bProbe.Result);
			Assert.AreEqual(2, actionProbes.Count, "Expected one stale run and one committed run");
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, actionProbes[0].Code, "The stale run's action must be skipped");
			Assert.IsNull(actionProbes[1].Error, "The committed run's action failed: " + actionProbes[1].Error?.Message);

			var doc = await FindDoc(conns[1], "once");
			Assert.IsNotNull(doc);
			Assert.AreEqual(2, doc["n"].AsInt32, "The action that ran should be the committed run's");
		}

		/// <summary>The application keeps state for an attempt's replicated action (here, a reserved inventory slot that
		/// the action's callback releases if the insert did not run). A rerun after a stale rejection must not start
		/// until that callback has run: the action's reply arrives after the update's, and a body run in between would
		/// see the slot still reserved.</summary>
		[Test, Category("Transact")]
		public async Task Transact_Rerun_WaitsForRejectedAttemptsAction()
		{
			var (conns, entities, _) = await Setup("tx_rerun_waits", 2);
			var a = entities[0];
			var b = entities[1];

			var aProbe = new TxProbe();
			a.Transact(Increment(a), aProbe.OnComplete);
			await PumpUntil(() => aProbe.Fired, conns[0]);
			Assert.AreEqual(0, b.Health.Get(), "B must still be stale for this test");

			int reserved = 0;
			var order = new List<string>();
			var reservedAtRun = new List<int>();
			var bProbe = new TxProbe();
			b.Transact(_ =>
			{
				int run = reservedAtRun.Count + 1;
				order.Add("body" + run);
				reservedAtRun.Add(reserved);

				b.Health.Set(b.Health.Get() + 1);
				reserved++;
				b.AddReplicatedAction(new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = "slot", ["run"] = run }, (err, _) =>
				{
					order.Add("action" + run);
					if (err != null)
					{
						reserved--;
					}
				}));
				return true;
			}, bProbe.OnComplete);

			await PumpUntil(() => bProbe.Fired && order.Count == 4, AllConnections());
			Assert.AreEqual(TransactResult.Committed, bProbe.Result);
			CollectionAssert.AreEqual(new[] { "body1", "action1", "body2", "action2" }, order,
				"The rerun must start only after the rejected attempt's action has reported");
			CollectionAssert.AreEqual(new[] { 0, 0 }, reservedAtRun, "The rerun saw the rejected attempt's slot still reserved");
			Assert.AreEqual(1, reserved, "Only the committed attempt's slot stays reserved");
		}

		/// <summary>A transaction queued behind this connection's plain UpdateExclusive waits for that update's replicated
		/// action to report, not just for the update's own reply. A callback-less action is waited for too.</summary>
		[Test, Category("Transact")]
		public async Task Transact_WaitsForInFlightUpdateExclusivesAction()
		{
			var (conns, entities, _) = await Setup("tx_wait_excl_action", 1);
			var a = entities[0];

			bool actionDone = false;
			a.Health.Set(5);
			a.UpdateExclusive(err => Assert.IsNull(err),
				new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = "excl_action" }, (err, _) => actionDone = true));
			// Callback-less: Impunity still asks the server for its reply so it knows when the action has resolved.
			// A different field, so the second exclusive update isn't stale against this client's own first one.
			a.DisplayName.Set("silent");
			a.UpdateExclusive(null, new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = "excl_action_silent" }, null));

			bool actionDoneWhenBodyRan = false;
			var probe = new TxProbe();
			a.Transact(_ =>
			{
				actionDoneWhenBodyRan = actionDone;
				a.Health.Set(a.Health.Get() + 1);
				return true;
			}, probe.OnComplete, 1);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.IsTrue(actionDoneWhenBodyRan, "The body ran before the UpdateExclusive's replicated action reported");
			Assert.AreEqual(6, a.Health.Get());
			Assert.IsNotNull(await FindDoc(conns[0], "excl_action_silent"));
		}

		// ───────── Client-authoritative ─────────

		/// <summary>On a client-authoritative entity a transaction commits; a declined body's Set has already applied
		/// locally, so it is still sent (the documented exception) rather than leaving this client out of step.</summary>
		[Test, Category("Transact")]
		public async Task Transact_ClientAuthoritative_CommitsAndDeclinedSetStillSent()
		{
			var (conns, entities, _) = await Setup("tx_auth", 2, clientAuthoritative: true);
			var a = entities[0];
			var b = entities[1];

			var probe = new TxProbe();
			a.Transact(Increment(a), probe.OnComplete);
			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.AreEqual(1, a.Health.Get());
			await PumpUntil(() => b.Health.Get() == 1, AllConnections());

			var declined = new TxProbe();
			a.Transact(_ => { a.Health.Set(50); return false; }, declined.OnComplete);
			await PumpUntil(() => declined.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Declined, declined.Result);
			Assert.AreEqual(50, a.Health.Get(), "Set applies locally on a client-authoritative entity");
			await PumpUntil(() => b.Health.Get() == 50, AllConnections());
		}

		// ───────── Failure ─────────

		/// <summary>Deleted (by another client) while transactions are queued: every one fails, no body runs.</summary>
		[Test, Category("Transact")]
		public async Task Transact_EntityDeletedWhileQueued_FailsAllWithoutRunning()
		{
			var (conns, entities, _) = await Setup("tx_deleted", 2);
			var a = entities[0];
			var b = entities[1];

			// B deletes it; only B is pumped, so A hasn't heard yet — the delete push sits in A's queue.
			Assert.IsTrue(await Pump(b.DeleteAsync(BsonValue.Null), conns[1]));

			// A's UpdateExclusive goes out (and will be answered ActionNotFound, after the delete push). The
			// transactions queue behind it.
			ImpunityErrorResponse exclusiveErr = null;
			bool exclusiveDone = false;
			a.Health.Set(1);
			a.UpdateExclusive(err => { exclusiveErr = err; exclusiveDone = true; });

			int runs = 0;
			var probes = new[] { new TxProbe(), new TxProbe(), new TxProbe() };
			foreach (var p in probes)
			{
				a.Transact(Increment(a, () => runs++), p.OnComplete);
			}

			await PumpUntil(() => exclusiveDone && Array.TrueForAll(probes, p => p.Fired), AllConnections());

			Assert.AreEqual(0, runs, "No body may run for a deleted entity");
			foreach (var p in probes)
			{
				Assert.AreEqual(TransactResult.Failed, p.Result);
				Assert.IsNotNull(p.Error);
				Assert.AreEqual(1, p.FireCount);
			}
			Assert.IsNotNull(exclusiveErr, "The UpdateExclusive should fail against the deleted entity");

			// Afterwards the entity is unregistered, so a new transaction fails straight away.
			var late = new TxProbe();
			a.Transact(Increment(a, () => runs++), late.OnComplete);
			await PumpUntil(() => late.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, late.Result);
			Assert.AreEqual(0, runs);
		}

		/// <summary>An immediate unsubscribe while a transaction awaits its reply fails it and everything queued behind it.</summary>
		[Test, Category("Transact")]
		public async Task Transact_UnsubscribedWhileAwaitingReply_Fails()
		{
			var (conns, entities, channels) = await Setup("tx_unsub", 1);
			var a = entities[0];

			int runs = 0;
			var first = new TxProbe();
			var second = new TxProbe();
			a.Transact(Increment(a, () => runs++), first.OnComplete);
			a.Transact(Increment(a, () => runs++), second.OnComplete);
			Assert.AreEqual(1, runs);

			conns[0].EntityManager.UnsubscribeFromChannel(channels[0], null, immediate: true);

			await PumpUntil(() => first.Fired && second.Fired, AllConnections());
			await PumpFor(TimeSpan.FromSeconds(0.2), AllConnections());
			Assert.AreEqual(TransactResult.Failed, first.Result);
			Assert.AreEqual(TransactResult.Failed, second.Result);
			Assert.AreEqual(1, first.FireCount, "A late reply must not report the transaction twice");
			Assert.AreEqual(1, runs, "The queued body must not run");
		}

		/// <summary>A throwing body fails, its Set is dropped, and the next transaction on the entity still runs.</summary>
		[Test, Category("Transact")]
		public async Task Transact_BodyThrows_FailsAndQueueMovesOn()
		{
			var (conns, entities, _) = await Setup("tx_throw", 2);
			var a = entities[0];
			var b = entities[1];

			// Hold the queue behind an in-flight UpdateExclusive, so the second transaction is genuinely queued
			// behind the throwing one.
			a.DisplayName.Set("held");
			a.UpdateExclusive(null);

			var throwing = new TxProbe();
			var next = new TxProbe();
			a.Transact(_ =>
			{
				a.Health.Set(666);
				throw new InvalidOperationException("boom");
			}, throwing.OnComplete);
			a.Transact(Increment(a), next.OnComplete);

			await PumpUntil(() => throwing.Fired && next.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, throwing.Result);
			Assert.IsNotNull(throwing.Error);
			StringAssert.Contains("boom", throwing.Error.Message);
			Assert.AreEqual(TransactResult.Committed, next.Result);
			Assert.AreEqual(1, a.Health.Get(), "The throwing body's Set must have been dropped");
			await PumpUntil(() => b.Health.Get() == 1 && b.DisplayName.Get() == "held", AllConnections());
		}

		/// <summary>An entity that was never registered with this connection fails without running the body.</summary>
		[Test, Category("Transact")]
		public async Task Transact_UnregisteredEntity_Fails()
		{
			var (conns, entities, _) = await Setup("tx_unreg", 1);
			var loose = new IntegrationTestEntity { Manager = conns[0].EntityManager };

			bool ran = false;
			var probe = new TxProbe();
			loose.Transact(_ => { ran = true; return true; }, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, probe.Result);
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, probe.Error.ErrorCode);
			Assert.IsFalse(ran);
		}

		/// <summary>An entity with no manager has nothing to send through: Set applies locally, so the body runs and its
		/// answer is the outcome.</summary>
		[Test, Category("Transact")]
		public void Transact_NoConnection_RunsBodyLocally()
		{
			var offline = new IntegrationTestEntity();

			var probe = new TxProbe();
			offline.Transact(_ => { offline.Health.Set(offline.Health.Get() + 2); return true; }, probe.OnComplete);
			Assert.IsTrue(probe.Fired);
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.AreEqual(2, offline.Health.Get());

			var declined = new TxProbe();
			offline.Transact(_ => false, declined.OnComplete);
			Assert.AreEqual(TransactResult.Declined, declined.Result);

			var failed = new TxProbe();
			offline.Transact(_ => throw new InvalidOperationException("offline boom"), failed.OnComplete);
			Assert.AreEqual(TransactResult.Failed, failed.Result);
		}

		// ───────── Completion ─────────

		/// <summary>The completion never runs inside the Transact call, even when the body ran and resolved there.</summary>
		[Test, Category("Transact")]
		public async Task Transact_OnCompleteNotReentrant()
		{
			var (conns, entities, _) = await Setup("tx_reentry", 1);
			var a = entities[0];

			bool insideCall = false;
			bool firedInside = false;
			var outcomes = new List<TransactResult>();
			ImpunityCallback<TransactResult> record = (err, result) =>
			{
				if (insideCall) firedInside = true;
				outcomes.Add(result);
			};

			bool bodyRan = false;
			insideCall = true;
			a.Transact(_ => { bodyRan = true; return false; }, record);
			a.Transact(_ => true, record);
			insideCall = false;

			Assert.IsTrue(bodyRan, "With nothing in flight the first body runs inside the call");
			Assert.AreEqual(0, outcomes.Count, "onComplete fired synchronously");

			await PumpUntil(() => outcomes.Count == 2, AllConnections());
			Assert.IsFalse(firedInside);
			CollectionAssert.AreEqual(new[] { TransactResult.Declined, TransactResult.Committed }, outcomes);
		}

		// ───────── Async wrapper ─────────

		[Test, Category("Transact")]
		public async Task TransactAsync_ReturnsOutcomeAndFaultsOnFailure()
		{
			var (conns, entities, _) = await Setup("tx_async", 2);
			var a = entities[0];
			var b = entities[1];

			Assert.AreEqual(TransactResult.Committed, await Pump(a.TransactAsync(Increment(a)), AllConnections()));
			Assert.AreEqual(1, a.Health.Get());
			Assert.AreEqual(TransactResult.Declined, await Pump(a.TransactAsync(_ => false), AllConnections()));

			var error = await PumpExpectingError(a.TransactAsync(_ => throw new InvalidOperationException("async boom")), AllConnections());
			Assert.IsNotNull(error, "A Failed transaction should fault the task");

			await PumpUntil(() => b.Health.Get() == 1, AllConnections());
		}
	}
}
