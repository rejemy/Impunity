// ───────── Transact scope: delete, replace, depends-on ─────────
//
// Covers what a Transact body can do through its TransactScope besides setting fields: make the write a guarded delete
// (tx.Delete) or a guarded replace (tx.ReplaceWith), and name fields it only read (tx.DependsOn). A delete or replace
// commits, reruns on a stale guard after the rejected attempt's conditional has reported, fails a transaction queued
// behind it once the entity is gone, sees the result of an update queued ahead of it, refuses an empty guard, and
// refuses being mixed with field sets. A dependency makes a change to a read-only field rerun the body.
//
// Making a client stale: local connections hand every action to the in-proc server at once, but a client only sees
// what the server pushed it when the test pumps that client. So A writes, only A is pumped, and B acts on old values.
#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class TransactScopeTests : ImpunityTestHarness
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

		sealed class World
		{
			public LocalGameConnection ConnA;
			public LocalGameConnection ConnB;
			public IntegrationTestChannel ChannelA;
			public IntegrationTestChannel ChannelB;
			/// <summary>The entity as A sees it (A created it).</summary>
			public IntegrationTestEntity A;
			/// <summary>The same entity as B sees it.</summary>
			public IntegrationTestEntity B;
		}

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

		/// <summary>Two distinct clients on one channel, and one entity (unique name <c>tile</c>, Health 1, DisplayName
		/// "hole") that both have.</summary>
		async Task<World> Setup(string channelName)
		{
			CreateServer();
			var w = new World();
			w.ConnA = await ConnectClient(channelName + "_a");
			w.ConnB = await ConnectClient(channelName + "_b");
			w.ChannelA = await Pump(w.ConnA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			w.ChannelB = await Pump(w.ConnB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			w.A = new IntegrationTestEntity { UniqueName = "tile" };
			w.A.Health.Set(1);
			w.A.DisplayName.Set("hole");
			await Pump(w.ConnA.EntityManager.CreateObjectAsync(w.A, w.ChannelA, false), AllConnections());
			await PumpUntil(() => w.ChannelB.DistributedObjects.Count == 1, AllConnections());
			w.B = (IntegrationTestEntity)w.ChannelB.DistributedObjects.Values.First();
			Assert.AreEqual(1, w.B.Health.Get());
			return w;
		}

		/// <summary>A writes a field and only A is pumped until A has its own echo, so B still holds the old value.</summary>
		async Task ChangeOnAOnly(World w, Action write, Func<bool> applied)
		{
			write();
			await PumpUntil(applied, w.ConnA);
		}

		Task<BsonDocument> FindDoc(BaseGameConnection conn, string id)
		{
			return Pump(conn.FindDocumentByIdAsync(ITEMS, id), AllConnections());
		}

		static InsertDocumentAction Loot(string id, ImpunityCallback<BsonValue> onComplete)
		{
			return new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = id }, onComplete);
		}

		static InsertDocumentAction Loot(string id, CallbackProbe<BsonValue> probe)
		{
			return Loot(id, probe.OnComplete);
		}

		// ───────── Delete ─────────

		/// <summary>Nothing guarded changed: the delete goes through, the conditional runs, and the transaction commits
		/// once the entity is gone (its delete push arrives before the reply).</summary>
		[Test, Category("Transact")]
		public async Task Delete_Unchanged_DeletesAndCommits()
		{
			var w = await Setup("txs_delete");

			var probe = new TxProbe();
			var lootProbe = new CallbackProbe<BsonValue>();
			bool deletedAtCommit = false;
			w.B.Transact(tx =>
			{
				tx.Delete(w.B.Health.Mask, "felled", Loot("txs_loot", lootProbe));
				return true;
			}, (err, result) => { deletedAtCommit = w.B.WasDeleted; probe.OnComplete(err, result); });

			await PumpUntil(() => probe.Fired && lootProbe.Fired && w.A.WasDeleted, AllConnections());
			Assert.IsNull(probe.Error, probe.Error?.Message);
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			Assert.IsTrue(deletedAtCommit, "Committed was reported before the entity was gone");
			Assert.IsNull(lootProbe.Error, lootProbe.Error?.Message);
			Assert.IsNotNull(await FindDoc(w.ConnB, "txs_loot"));
			Assert.AreEqual(1, probe.FireCount);
		}

		/// <summary>A guarded field changed first: the delete is stale, its conditional is skipped, and the body reruns
		/// against the winner's value only after that skip has reported. The second attempt's delete goes through.</summary>
		[Test, Category("Transact")]
		public async Task Delete_StaleGuard_RerunsAfterTheSkippedConditional()
		{
			var w = await Setup("txs_delete_stale");
			await ChangeOnAOnly(w, () => w.A.Health.Set(5), () => w.A.Health.Get() == 5);
			Assert.AreEqual(1, w.B.Health.Get(), "B must still be stale for this test");

			var order = new List<string>();
			var codes = new List<ImpunityErrorCode?>();
			var probe = new TxProbe();
			w.B.Transact(tx =>
			{
				int attempt = tx.Attempt;
				order.Add("body" + attempt + ":" + w.B.Health.Get());
				tx.Delete(w.B.Health.Mask, null, Loot("txs_stale_loot" + attempt, (err, _) =>
				{
					order.Add("loot" + attempt);
					codes.Add(err?.ErrorCode);
				}));
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired && order.Count == 4, AllConnections());
			Assert.IsNull(probe.Error, probe.Error?.Message);
			Assert.AreEqual(TransactResult.Committed, probe.Result);
			CollectionAssert.AreEqual(new[] { "body1:1", "loot1", "body2:5", "loot2" }, order);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, codes[0]);
			Assert.IsNull(codes[1]);
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
			Assert.IsNull(await FindDoc(w.ConnB, "txs_stale_loot1"), "The rejected attempt's conditional ran");
			Assert.IsNotNull(await FindDoc(w.ConnB, "txs_stale_loot2"));
		}

		/// <summary>A delete that stays stale runs out of attempts: <see cref="TransactResult.Conflict"/>, nothing deleted.</summary>
		[Test, Category("Transact")]
		public async Task Delete_AlwaysStale_Conflicts()
		{
			var w = await Setup("txs_delete_conflict");
			await ChangeOnAOnly(w, () => w.A.Health.Set(5), () => w.A.Health.Get() == 5);

			var probe = new TxProbe();
			w.B.Transact(tx => { tx.Delete(w.B.Health.Mask); return true; }, probe.OnComplete, 1);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Conflict, probe.Result);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A transaction queued behind a deleting one finds the entity gone: it fails and its body never runs.</summary>
		[Test, Category("Transact")]
		public async Task QueuedBehindADelete_FailsWithoutRunning()
		{
			var w = await Setup("txs_queued_behind");

			var first = new TxProbe();
			var second = new TxProbe();
			bool secondRan = false;
			w.B.Transact(tx => { tx.Delete(w.B.Health.Mask); return true; }, first.OnComplete);
			w.B.Transact(tx => { secondRan = true; w.B.Health.Set(w.B.Health.Get() + 1); return true; }, second.OnComplete);

			await PumpUntil(() => first.Fired && second.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, first.Result, first.Error?.Message);
			Assert.AreEqual(TransactResult.Failed, second.Result);
			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, second.Error.ErrorCode);
			Assert.IsFalse(secondRan);
		}

		/// <summary>A delete queued behind an update on the same connection runs against that update's result, so its
		/// guard is not stale against this client's own write.</summary>
		[Test, Category("Transact")]
		public async Task DeleteQueuedBehindAnUpdate_SeesItsResult()
		{
			var w = await Setup("txs_queued_delete");

			var first = new TxProbe();
			var second = new TxProbe();
			var seen = new List<int>();
			w.B.Transact(_ => { w.B.Health.Set(w.B.Health.Get() + 1); return true; }, first.OnComplete);
			w.B.Transact(tx => { seen.Add(w.B.Health.Get()); tx.Delete(w.B.Health.Mask); return true; }, second.OnComplete);

			await PumpUntil(() => first.Fired && second.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, first.Result, first.Error?.Message);
			Assert.AreEqual(TransactResult.Committed, second.Result, second.Error?.Message);
			CollectionAssert.AreEqual(new[] { 2 }, seen, "The delete ran before the update was confirmed, or reran");
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>Another connection's lock blocks the delete: the transaction fails with
		/// <see cref="ImpunityErrorCode.ActionBlockedByLock"/> and the conditional is skipped.</summary>
		[Test, Category("Transact")]
		public async Task Delete_ForeignLock_Fails()
		{
			var w = await Setup("txs_foreign_lock");
			Assert.IsTrue(await Pump(w.A.TryLockAsync(), AllConnections()));

			var probe = new TxProbe();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.Transact(tx => { tx.Delete(w.B.Health.Mask, null, Loot("txs_lock_loot", lootProbe)); return true; }, probe.OnComplete);

			await PumpUntil(() => probe.Fired && lootProbe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, probe.Result);
			Assert.AreEqual(ImpunityErrorCode.ActionBlockedByLock, probe.Error.ErrorCode);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>An empty guard throws inside the body, so the transaction fails and nothing is sent.</summary>
		[Test, Category("Transact")]
		public async Task Delete_EmptyOrForeignMask_FailsWithoutSending()
		{
			var w = await Setup("txs_empty_mask");

			var empty = new TxProbe();
			var foreign = new TxProbe();
			w.B.Transact(tx => { tx.Delete(0); return true; }, empty.OnComplete);
			w.B.Transact(tx => { tx.Delete(1ul << 40); return true; }, foreign.OnComplete);

			await PumpUntil(() => empty.Fired && foreign.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, empty.Result);
			StringAssert.Contains("guard", empty.Error.Message);
			Assert.AreEqual(TransactResult.Failed, foreign.Result);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A body that both sets a field and deletes fails: nothing is sent, the set is dropped, and the
		/// conditional is skipped.</summary>
		[Test, Category("Transact")]
		public async Task Delete_MixedWithFieldSets_Fails()
		{
			var w = await Setup("txs_mixed");

			var probe = new TxProbe();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.Transact(tx =>
			{
				w.B.Health.Set(99);
				tx.Delete(w.B.Health.Mask, null, Loot("txs_mixed_loot", lootProbe));
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired && lootProbe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, probe.Result);
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, probe.Error.ErrorCode);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
			Assert.AreEqual(1, w.A.Health.Get(), "The dropped Set was sent");
			Assert.AreEqual(0ul, w.B.DirtyBits);
		}

		/// <summary>A body that asks for a delete and then declines sends nothing and skips the conditional.</summary>
		[Test, Category("Transact")]
		public async Task Delete_ThenDecline_SkipsConditional()
		{
			var w = await Setup("txs_decline");

			var probe = new TxProbe();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.Transact(tx => { tx.Delete(w.B.Health.Mask, null, Loot("txs_decline_loot", lootProbe)); return false; }, probe.OnComplete);

			await PumpUntil(() => probe.Fired && lootProbe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Declined, probe.Result);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A scope is only good while its body runs; a second Delete in one body is an error.</summary>
		[Test, Category("Transact")]
		public async Task Scope_MisuseThrows()
		{
			var w = await Setup("txs_misuse");

			TransactScope kept = null;
			var probe = new TxProbe();
			var twice = new TxProbe();
			w.B.Transact(tx => { kept = tx; return false; }, probe.OnComplete);
			w.B.Transact(tx => { tx.Delete(w.B.Health.Mask); tx.Delete(w.B.Health.Mask); return true; }, twice.OnComplete);
			await PumpUntil(() => probe.Fired && twice.Fired, AllConnections());

			Assert.Throws<InvalidOperationException>(() => kept.Delete(w.B.Health.Mask));
			Assert.Throws<InvalidOperationException>(() => kept.DependsOn(w.B.Health.Mask));
			Assert.AreEqual(TransactResult.Failed, twice.Result);
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>Without a connection there is nothing to delete through: the transaction fails at once and the
		/// conditional is failed too.</summary>
		[Test, Category("Transact")]
		public void Delete_Offline_Fails()
		{
			var offline = new IntegrationTestEntity();
			var probe = new TxProbe();
			var lootProbe = new CallbackProbe<BsonValue>();
			offline.Transact(tx => { tx.Delete(offline.Health.Mask, null, Loot("txs_offline", lootProbe)); return true; }, probe.OnComplete);

			Assert.IsTrue(probe.Fired);
			Assert.AreEqual(TransactResult.Failed, probe.Result);
			Assert.IsTrue(lootProbe.Fired);
			Assert.IsNotNull(lootProbe.Error);
		}

		// ───────── Replace ─────────

		/// <summary>An unchanged guard: the replacement takes the entity's place (and its unique name), the conditional
		/// runs, and the new object is registered by the time the transaction commits.</summary>
		[Test, Category("Transact")]
		public async Task ReplaceWith_Unchanged_Replaces()
		{
			var w = await Setup("txs_replace");

			var probe = new TxProbe();
			var seedProbe = new CallbackProbe<BsonValue>();
			IntegrationTestEntity crop = null;
			bool registeredAtCommit = false;
			w.B.Transact(tx =>
			{
				crop = new IntegrationTestEntity();
				crop.DisplayName.Set("crop");
				tx.ReplaceWith(w.B.DisplayName.Mask, crop, Loot("txs_seed", seedProbe));
				return true;
			}, (err, result) => { registeredAtCommit = crop.DistributedEntityId != 0; probe.OnComplete(err, result); });

			await PumpUntil(() => probe.Fired && seedProbe.Fired && w.A.WasDeleted && w.ChannelA.DistributedObjects.Count == 1
				&& ((IntegrationTestEntity)w.ChannelA.DistributedObjects.Values.First()).DisplayName.Get() == "crop", AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, probe.Error?.Message);
			Assert.IsTrue(registeredAtCommit, "Committed was reported before the replacement was registered");
			Assert.AreEqual("tile", crop.UniqueName);
			Assert.IsNull(seedProbe.Error, seedProbe.Error?.Message);
		}

		/// <summary>The replaced entity's guarded field changed first: the replace is stale and the body reruns against
		/// the winner's value.</summary>
		[Test, Category("Transact")]
		public async Task ReplaceWith_StaleGuard_Reruns()
		{
			var w = await Setup("txs_replace_stale");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("dug"), () => w.A.DisplayName.Get() == "dug");
			Assert.AreEqual("hole", w.B.DisplayName.Get(), "B must still be stale for this test");

			var seen = new List<string>();
			var probe = new TxProbe();
			w.B.Transact(tx =>
			{
				seen.Add(w.B.DisplayName.Get());
				var crop = new IntegrationTestEntity();
				crop.DisplayName.Set("crop after " + w.B.DisplayName.Get());
				tx.ReplaceWith(w.B.DisplayName.Mask, crop);
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, probe.Error?.Message);
			CollectionAssert.AreEqual(new[] { "hole", "dug" }, seen);
			await PumpUntil(() => w.A.WasDeleted && w.ChannelA.DistributedObjects.Count == 1
				&& ((IntegrationTestEntity)w.ChannelA.DistributedObjects.Values.First()).DisplayName.Get() == "crop after dug", AllConnections());
		}

		/// <summary>The replacement must carry the replaced entity's unique name (or none, and it is given it).</summary>
		[Test, Category("Transact")]
		public async Task ReplaceWith_DifferentName_Fails()
		{
			var w = await Setup("txs_replace_name");

			var probe = new TxProbe();
			w.B.Transact(tx => { tx.ReplaceWith(w.B.Health.Mask, new IntegrationTestEntity { UniqueName = "elsewhere" }); return true; }, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Failed, probe.Result);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}

		// ───────── DependsOn ─────────

		/// <summary>Contrast for the next test: a body that only reads DisplayName is not rerun when DisplayName changes,
		/// because only the fields it writes are guarded.</summary>
		[Test, Category("Transact")]
		public async Task ReadOnlyField_WithoutDependsOn_IsNotGuarded()
		{
			var w = await Setup("txs_no_depends");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("watered"), () => w.A.DisplayName.Get() == "watered");

			var seen = new List<string>();
			var probe = new TxProbe();
			w.B.Transact(_ => { seen.Add(w.B.DisplayName.Get()); w.B.Health.Set(w.B.DisplayName.Get().Length); return true; }, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, probe.Error?.Message);
			CollectionAssert.AreEqual(new[] { "hole" }, seen);
			await PumpUntil(() => w.A.Health.Get() == "hole".Length, AllConnections());
		}

		/// <summary>With <c>DependsOn</c>, a change to a field the body only read makes the update stale, and the body
		/// reruns against the new value.</summary>
		[Test, Category("Transact")]
		public async Task DependsOn_ReadFieldChanged_Reruns()
		{
			var w = await Setup("txs_depends");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("watered"), () => w.A.DisplayName.Get() == "watered");

			var seen = new List<string>();
			var probe = new TxProbe();
			w.B.Transact(tx =>
			{
				tx.DependsOn(w.B.DisplayName.Mask);
				seen.Add(w.B.DisplayName.Get());
				w.B.Health.Set(w.B.DisplayName.Get().Length);
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, probe.Error?.Message);
			CollectionAssert.AreEqual(new[] { "hole", "watered" }, seen);
			await PumpUntil(() => w.A.Health.Get() == "watered".Length, AllConnections());
		}

		/// <summary>A dependency with no field writes but a replicated action still sends, so the action runs only if
		/// the read field is unchanged: stale here, so the first attempt's action is skipped and the rerun's runs.</summary>
		[Test, Category("Transact")]
		public async Task DependsOn_ActionOnly_IsGuarded()
		{
			var w = await Setup("txs_depends_action");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("watered"), () => w.A.DisplayName.Get() == "watered");

			var codes = new List<ImpunityErrorCode?>();
			var probe = new TxProbe();
			w.B.Transact(tx =>
			{
				tx.DependsOn(w.B.DisplayName.Mask);
				w.B.AddReplicatedAction(Loot("txs_buy" + tx.Attempt, (err, _) => codes.Add(err?.ErrorCode)));
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired && codes.Count == 2, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, probe.Error?.Message);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, codes[0]);
			Assert.IsNull(codes[1]);
			Assert.IsNull(await FindDoc(w.ConnB, "txs_buy1"));
			Assert.IsNotNull(await FindDoc(w.ConnB, "txs_buy2"));
		}

		/// <summary>A dependency adds to a delete's guard: an unguarded-by-the-mask field named by DependsOn makes the
		/// delete stale.</summary>
		[Test, Category("Transact")]
		public async Task DependsOn_AddsToTheDeleteGuard()
		{
			var w = await Setup("txs_depends_delete");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("watered"), () => w.A.DisplayName.Get() == "watered");

			var seen = new List<string>();
			var probe = new TxProbe();
			w.B.Transact(tx =>
			{
				seen.Add(w.B.DisplayName.Get());
				tx.DependsOn(w.B.DisplayName.Mask);
				tx.Delete(w.B.Health.Mask);
				return true;
			}, probe.OnComplete);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(TransactResult.Committed, probe.Result, probe.Error?.Message);
			CollectionAssert.AreEqual(new[] { "hole", "watered" }, seen);
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}
	}
}
