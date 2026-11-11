// ───────── Guarded delete and replace ─────────
//
// Covers entity.DeleteExclusive and the replaceGuard argument of CreateObject: an unchanged guard lets the operation
// through, a changed guarded field makes it stale (nothing changes, the conditional is skipped), an unguarded field
// changing does not block it, a lock holder bypasses the guard, this connection's own unechoed write counts as a
// change, and an empty or foreign mask throws before anything is sent.
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
	public class GuardedDeleteTests : ImpunityTestHarness
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

		static InsertDocumentAction Loot(string id, CallbackProbe<BsonValue> probe)
		{
			return new InsertDocumentAction(ITEMS, new BsonDocument { ["_id"] = id }, probe.OnComplete);
		}

		// ───────── DeleteExclusive ─────────

		/// <summary>Nothing guarded has changed: the delete goes through, everyone sees it, and the conditional runs.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_Unchanged_DeletesAndRunsConditional()
		{
			var w = await Setup("gd_unchanged");

			var deleteProbe = new CallbackProbe<bool>();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.DeleteExclusive(w.B.Health.Mask | w.B.DisplayName.Mask, "killed", deleteProbe.OnComplete, Loot("gd_loot", lootProbe));

			await PumpUntil(() => deleteProbe.Fired && lootProbe.Fired && w.A.WasDeleted, AllConnections());
			Assert.IsNull(deleteProbe.Error, deleteProbe.Error?.Message);
			Assert.IsTrue(deleteProbe.Value);
			Assert.IsNull(lootProbe.Error, lootProbe.Error?.Message);
			Assert.IsNotNull(await FindDoc(w.ConnB, "gd_loot"));
		}

		/// <summary>A guarded field changed after B last saw it: the delete is stale, nothing is deleted, the conditional
		/// is skipped, and B already has the winner's value when its callback runs.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_GuardedFieldChanged_IsStaleAndSkipsConditional()
		{
			var w = await Setup("gd_stale");
			await ChangeOnAOnly(w, () => w.A.Health.Set(5), () => w.A.Health.Get() == 5);
			Assert.AreEqual(1, w.B.Health.Get(), "B must still be stale for this test");

			int healthAtCallback = -1;
			var deleteProbe = new CallbackProbe<bool>();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.DeleteExclusive(w.B.Health.Mask, null, (err, deleted) =>
			{
				healthAtCallback = w.B.Health.Get();
				deleteProbe.OnComplete(err, deleted);
			}, Loot("gd_stale_loot", lootProbe));

			await PumpUntil(() => deleteProbe.Fired && lootProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, deleteProbe.Code);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			Assert.AreEqual(5, healthAtCallback, "The winner's value should be applied before the stale callback");
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
			Assert.AreEqual(1, w.ChannelB.DistributedObjects.Count);
			Assert.IsNull(await FindDoc(w.ConnB, "gd_stale_loot"), "The skipped conditional ran");

			// Now that B has caught up, the same guard passes.
			Assert.IsTrue(await Pump(w.B.DeleteExclusiveAsync(w.B.Health.Mask, null), AllConnections()));
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>Only the named fields are guarded: a change to another field doesn't block the delete.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_UnguardedFieldChanged_StillDeletes()
		{
			var w = await Setup("gd_unrelated");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("wandered"), () => w.A.DisplayName.Get() == "wandered");
			Assert.AreEqual("hole", w.B.DisplayName.Get(), "B must still be stale for this test");

			Assert.IsTrue(await Pump(w.B.DeleteExclusiveAsync(w.B.Health.Mask, null), AllConnections()));
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>The guard is this client's last-received view, so its own write still awaiting the echo counts as a
		/// change it hasn't seen.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_OwnUnechoedWrite_IsStale()
		{
			var w = await Setup("gd_self");

			w.B.Health.Set(9);
			w.B.UpdateExclusive(null);
			var deleteProbe = new CallbackProbe<bool>();
			w.B.DeleteExclusive(w.B.Health.Mask, null, deleteProbe.OnComplete);

			await PumpUntil(() => deleteProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, deleteProbe.Code);
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A lock holder bypasses the guard, as it does for exclusive updates: the same self-stale delete
		/// succeeds while B holds the lock.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_LockHolder_BypassesGuard()
		{
			var w = await Setup("gd_lock");
			Assert.IsTrue(await Pump(w.B.TryLockAsync(), AllConnections()));

			w.B.Health.Set(9);
			w.B.UpdateExclusive(null);
			var deleteProbe = new CallbackProbe<bool>();
			w.B.DeleteExclusive(w.B.Health.Mask, null, deleteProbe.OnComplete);

			await PumpUntil(() => deleteProbe.Fired, AllConnections());
			Assert.IsNull(deleteProbe.Error, deleteProbe.Error?.Message);
			Assert.IsTrue(deleteProbe.Value);
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>Another client's lock blocks a guarded delete the same way it blocks a plain one: false, no error.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_ForeignLock_ReturnsFalse()
		{
			var w = await Setup("gd_foreign_lock");
			Assert.IsTrue(await Pump(w.A.TryLockAsync(), AllConnections()));

			var lootProbe = new CallbackProbe<BsonValue>();
			Assert.IsFalse(await Pump(w.B.DeleteExclusiveAsync(w.B.Health.Mask, null, Loot("gd_lock_loot", lootProbe)), AllConnections()));
			await PumpUntil(() => lootProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>An empty mask, or one naming bits that aren't the entity's fields, throws before anything is sent;
		/// the conditional is left unresolved for the caller.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteExclusive_BadMask_ThrowsWithoutSending()
		{
			var w = await Setup("gd_bad_mask");

			var lootProbe = new CallbackProbe<BsonValue>();
			Assert.Throws<ArgumentException>(() => w.B.DeleteExclusive(0, null, (_, __) => Assert.Fail("Callback fired"), Loot("gd_bad", lootProbe)));
			Assert.Throws<ArgumentException>(() => w.B.DeleteExclusive(1ul << 40, null, (_, __) => Assert.Fail("Callback fired")));

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(lootProbe.Fired, "A throwing call must leave its conditional to the caller");
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>Masks are the field bits, distinct per field and stable across instances of the type.</summary>
		[Test, Category("GuardedDelete")]
		public async Task FieldMask_IsTheFieldsBit()
		{
			var w = await Setup("gd_masks");

			Assert.AreNotEqual(0ul, w.A.Health.Mask);
			Assert.AreNotEqual(w.A.Health.Mask, w.A.DisplayName.Mask);
			Assert.AreEqual(w.A.Health.Mask, w.B.Health.Mask);
			Assert.AreEqual(w.A.Health.Mask, new IntegrationTestEntity().Health.Mask);

			// A field's bit is what Set marks dirty.
			var fresh = new IntegrationTestEntity();
			fresh.DisplayName.Set("x");
			Assert.AreEqual(fresh.DisplayName.Mask, fresh.DirtyBits);
		}

		// ───────── Guarded replace ─────────

		/// <summary>The replaced entity's guarded fields are unchanged: the replace goes through and the conditional runs.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceGuard_Unchanged_Replaces()
		{
			var w = await Setup("gr_unchanged");

			var seedProbe = new CallbackProbe<BsonValue>();
			var crop = new IntegrationTestEntity { UniqueName = "tile" };
			crop.DisplayName.Set("crop");
			await Pump(w.ConnB.EntityManager.CreateObjectAsync(crop, w.ChannelB, false, Loot("gr_seed", seedProbe), w.B.DistributedEntityId,
				w.B.DisplayName.Mask), AllConnections());

			await PumpUntil(() => seedProbe.Fired && w.A.WasDeleted && w.ChannelA.DistributedObjects.Count == 1
				&& ((IntegrationTestEntity)w.ChannelA.DistributedObjects.Values.First()).DisplayName.Get() == "crop", AllConnections());
			Assert.IsNull(seedProbe.Error, seedProbe.Error?.Message);
		}

		/// <summary>A guarded field of the entity being replaced changed first: the create is stale, nothing changes, and
		/// the conditional is skipped.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceGuard_GuardedFieldChanged_IsStale()
		{
			var w = await Setup("gr_stale");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("dug"), () => w.A.DisplayName.Get() == "dug");
			Assert.AreEqual("hole", w.B.DisplayName.Get(), "B must still be stale for this test");

			var createProbe = new CallbackProbe<IntegrationTestEntity>();
			var seedProbe = new CallbackProbe<BsonValue>();
			var crop = new IntegrationTestEntity { UniqueName = "tile" };
			crop.DisplayName.Set("crop");
			w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, createProbe.OnComplete, Loot("gr_stale_seed", seedProbe),
				w.B.DistributedEntityId, w.B.DisplayName.Mask);

			await PumpUntil(() => createProbe.Fired && seedProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, createProbe.Code);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, seedProbe.Code);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
			Assert.AreEqual("dug", w.B.DisplayName.Get());
			Assert.AreEqual(1, w.ChannelA.DistributedObjects.Count);
			Assert.IsNull(await FindDoc(w.ConnB, "gr_stale_seed"), "The skipped conditional ran");
		}

		/// <summary>A replace guard needs the replaced entity's id, and that entity must be one this client has.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceGuard_WithoutAKnownEntity_Throws()
		{
			var w = await Setup("gr_bad");

			var crop = new IntegrationTestEntity { UniqueName = "tile" };
			Assert.Throws<ArgumentException>(() => w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, (_, __) => Assert.Fail("Callback fired"),
				null, 0, w.B.Health.Mask));
			Assert.Throws<ArgumentException>(() => w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, (_, __) => Assert.Fail("Callback fired"),
				null, 999999, w.B.Health.Mask));
			Assert.Throws<ArgumentException>(() => w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, (_, __) => Assert.Fail("Callback fired"),
				null, w.B.DistributedEntityId, 1ul << 40));

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}
	}
}
