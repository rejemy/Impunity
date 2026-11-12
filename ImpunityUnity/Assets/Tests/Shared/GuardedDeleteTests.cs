// ───────── Guarded delete and replace ─────────
//
// Covers entity.DeleteIfUnchanged and the replaceGuard argument of CreateObject: an unchanged guard lets the operation
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

		// ───────── DeleteIfUnchanged ─────────

		/// <summary>Nothing guarded has changed: the delete goes through, everyone sees it, and the conditional runs.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_Unchanged_DeletesAndRunsConditional()
		{
			var w = await Setup("gd_unchanged");

			var deleteProbe = new CallbackProbe<GuardedResult>();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.DeleteIfUnchanged(w.B.Health.Guard | w.B.DisplayName.Guard, "killed", deleteProbe.OnComplete, Loot("gd_loot", lootProbe));

			await PumpUntil(() => deleteProbe.Fired && lootProbe.Fired && w.A.WasDeleted, AllConnections());
			Assert.IsNull(deleteProbe.Error, deleteProbe.Error?.Message);
			Assert.AreEqual(GuardedResult.Applied, deleteProbe.Value);
			Assert.IsNull(lootProbe.Error, lootProbe.Error?.Message);
			Assert.IsNotNull(await FindDoc(w.ConnB, "gd_loot"));
		}

		/// <summary>A guarded field changed after B last saw it: the delete is stale, nothing is deleted, the conditional
		/// is skipped, and B already has the winner's value when its callback runs.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_GuardedFieldChanged_IsStaleAndSkipsConditional()
		{
			var w = await Setup("gd_stale");
			await ChangeOnAOnly(w, () => w.A.Health.Set(5), () => w.A.Health.Get() == 5);
			Assert.AreEqual(1, w.B.Health.Get(), "B must still be stale for this test");

			int healthAtCallback = -1;
			var deleteProbe = new CallbackProbe<GuardedResult>();
			var lootProbe = new CallbackProbe<BsonValue>();
			w.B.DeleteIfUnchanged(w.B.Health.Guard, null, (err, deleted) =>
			{
				healthAtCallback = w.B.Health.Get();
				deleteProbe.OnComplete(err, deleted);
			}, Loot("gd_stale_loot", lootProbe));

			await PumpUntil(() => deleteProbe.Fired && lootProbe.Fired, AllConnections());
			Assert.IsNull(deleteProbe.Error, "Losing the race is a result, not an error");
			Assert.AreEqual(GuardedResult.Stale, deleteProbe.Value);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			Assert.AreEqual(5, healthAtCallback, "The winner's value should be applied before the stale callback");
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
			Assert.AreEqual(1, w.ChannelB.DistributedObjects.Count);
			Assert.IsNull(await FindDoc(w.ConnB, "gd_stale_loot"), "The skipped conditional ran");

			// Now that B has caught up, the same guard passes.
			Assert.AreEqual(GuardedResult.Applied, await Pump(w.B.DeleteIfUnchangedAsync(w.B.Health.Guard, null), AllConnections()));
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>Only the named fields are guarded: a change to another field doesn't block the delete.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_UnguardedFieldChanged_StillDeletes()
		{
			var w = await Setup("gd_unrelated");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("wandered"), () => w.A.DisplayName.Get() == "wandered");
			Assert.AreEqual("hole", w.B.DisplayName.Get(), "B must still be stale for this test");

			Assert.AreEqual(GuardedResult.Applied, await Pump(w.B.DeleteIfUnchangedAsync(w.B.Health.Guard, null), AllConnections()));
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>The guard is this client's last-received view, so its own write still awaiting the echo counts as a
		/// change it hasn't seen.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_OwnUnechoedWrite_IsStale()
		{
			var w = await Setup("gd_self");

			w.B.Health.Set(9);
			w.B.UpdateIfUnchanged(null);
			var deleteProbe = new CallbackProbe<GuardedResult>();
			w.B.DeleteIfUnchanged(w.B.Health.Guard, null, deleteProbe.OnComplete);

			await PumpUntil(() => deleteProbe.Fired, AllConnections());
			Assert.IsNull(deleteProbe.Error, "Losing the race is a result, not an error");
			Assert.AreEqual(GuardedResult.Stale, deleteProbe.Value);
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A lock holder bypasses the guard, as it does for guarded updates: the same self-stale delete
		/// succeeds while B holds the lock.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_LockHolder_BypassesGuard()
		{
			var w = await Setup("gd_lock");
			Assert.IsTrue(await Pump(w.B.TryLockAsync(), AllConnections()));

			w.B.Health.Set(9);
			w.B.UpdateIfUnchanged(null);
			var deleteProbe = new CallbackProbe<GuardedResult>();
			w.B.DeleteIfUnchanged(w.B.Health.Guard, null, deleteProbe.OnComplete);

			await PumpUntil(() => deleteProbe.Fired, AllConnections());
			Assert.IsNull(deleteProbe.Error, deleteProbe.Error?.Message);
			Assert.AreEqual(GuardedResult.Applied, deleteProbe.Value);
			await PumpUntil(() => w.A.WasDeleted, AllConnections());
		}

		/// <summary>Another client's lock makes a guarded delete fail with ActionBlockedByLock, as it does a guarded
		/// update: an explicit lock wins over an optimistic write.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_ForeignLock_FailsBlockedByLock()
		{
			var w = await Setup("gd_foreign_lock");
			Assert.IsTrue(await Pump(w.A.TryLockAsync(), AllConnections()));

			var lootProbe = new CallbackProbe<BsonValue>();
			var lockError = await PumpExpectingError(w.B.DeleteIfUnchangedAsync(w.B.Health.Guard, null, Loot("gd_lock_loot", lootProbe)), AllConnections());
			Assert.IsNotNull(lockError, "The delete went through another client's lock");
			Assert.AreEqual(ImpunityErrorCode.ActionBlockedByLock, lockError.ErrorId);
			await PumpUntil(() => lootProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, lootProbe.Code);
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>An empty guard, or one naming another entity's fields, throws before anything is sent; the
		/// conditional is left unresolved for the caller.</summary>
		[Test, Category("GuardedDelete")]
		public async Task DeleteIfUnchanged_BadGuard_ThrowsWithoutSending()
		{
			var w = await Setup("gd_bad_guard");

			var lootProbe = new CallbackProbe<BsonValue>();
			Assert.Throws<ArgumentException>(() => w.B.DeleteIfUnchanged(default, null, (_, __) => Assert.Fail("Callback fired"), Loot("gd_bad", lootProbe)));
			// The same field of a different entity object (here, another client's copy) is not this entity's guard.
			Assert.Throws<ArgumentException>(() => w.B.DeleteIfUnchanged(w.A.Health.Guard, null, (_, __) => Assert.Fail("Callback fired")));
			Assert.Throws<ArgumentException>(() => w.B.DeleteIfUnchanged(new IntegrationTestEntity().Health.Guard, null, (_, __) => Assert.Fail("Callback fired")));

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(lootProbe.Fired, "A throwing call must leave its conditional to the caller");
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A guard names fields of one entity: fields of the same entity combine, the empty guard is the
		/// identity, and combining fields of two entities throws instead of silently guarding the wrong thing.</summary>
		[Test, Category("GuardedDelete")]
		public async Task FieldGuard_CombinesFieldsOfOneEntityOnly()
		{
			var w = await Setup("gd_guards");

			Assert.IsTrue(default(FieldGuard).IsEmpty);
			Assert.IsFalse(w.A.Health.Guard.IsEmpty);
			Assert.IsFalse((w.A.Health.Guard | w.A.DisplayName.Guard).IsEmpty);
			Assert.IsFalse((default(FieldGuard) | w.A.Health.Guard).IsEmpty);
			Assert.Throws<ArgumentException>(() => { var _ = w.A.Health.Guard | w.B.DisplayName.Guard; });
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
			await Pump(w.ConnB.EntityManager.CreateObjectAsync(crop, w.ChannelB, false, Loot("gr_seed", seedProbe),
				replaceGuard: w.B.DisplayName.Guard), AllConnections());   // the guard says which entity; no id needed

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
				w.B.DistributedEntityId, w.B.DisplayName.Guard);

			await PumpUntil(() => createProbe.Fired && seedProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, createProbe.Code);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, seedProbe.Code);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
			Assert.AreEqual("dug", w.B.DisplayName.Get());
			Assert.AreEqual(1, w.ChannelA.DistributedObjects.Count);
			Assert.IsNull(await FindDoc(w.ConnB, "gr_stale_seed"), "The skipped conditional ran");
		}

		// ───────── ReplaceIfUnchanged ─────────

		/// <summary>Nothing guarded has changed: the entity is replaced under its own unique name, its delete carries
		/// the cause, the conditional runs, and the replacement is registered when Applied is reported.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceIfUnchanged_Unchanged_ReplacesWithCause()
		{
			var w = await Setup("ri_unchanged");

			var seedProbe = new CallbackProbe<BsonValue>();
			var crop = new IntegrationTestEntity();
			crop.DisplayName.Set("crop");
			bool registeredAtResult = false;
			var probe = new CallbackProbe<GuardedResult>();
			w.B.ReplaceIfUnchanged(w.B.DisplayName.Guard, crop, "planted",
				(err, result) => { registeredAtResult = crop.DistributedEntityId != 0; probe.OnComplete(err, result); }, Loot("ri_seed", seedProbe));

			await PumpUntil(() => probe.Fired && seedProbe.Fired && w.A.WasDeleted && w.ChannelA.DistributedObjects.Count == 1
				&& ((IntegrationTestEntity)w.ChannelA.DistributedObjects.Values.First()).DisplayName.Get() == "crop", AllConnections());
			Assert.IsNull(probe.Error, probe.Error?.Message);
			Assert.AreEqual(GuardedResult.Applied, probe.Value);
			Assert.IsTrue(registeredAtResult, "Applied was reported before the replacement was registered");
			Assert.AreEqual("tile", crop.UniqueName, "The replacement should take the replaced entity's unique name");
			Assert.IsNull(seedProbe.Error, seedProbe.Error?.Message);
			Assert.AreEqual("planted", w.A.LastDeletion.Data?.AsString);
			Assert.IsFalse(w.A.LastDeletion.IsOwn);
			Assert.IsTrue(w.B.LastDeletion.IsOwn);
		}

		/// <summary>A guarded field changed first: Stale with no error, nothing replaced, conditional skipped.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceIfUnchanged_GuardedFieldChanged_IsStale()
		{
			var w = await Setup("ri_stale");
			await ChangeOnAOnly(w, () => w.A.DisplayName.Set("dug"), () => w.A.DisplayName.Get() == "dug");
			Assert.AreEqual("hole", w.B.DisplayName.Get(), "B must still be stale for this test");

			var seedProbe = new CallbackProbe<BsonValue>();
			var crop = new IntegrationTestEntity();
			Assert.AreEqual(GuardedResult.Stale, await Pump(w.B.ReplaceIfUnchangedAsync(w.B.DisplayName.Guard, crop, null, Loot("ri_stale_seed", seedProbe)), AllConnections()));

			await PumpUntil(() => seedProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, seedProbe.Code);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
			Assert.AreEqual(0u, crop.DistributedEntityId, "A stale replace registered its replacement");
			Assert.AreEqual(1, w.ChannelA.DistributedObjects.Count);
		}

		/// <summary>Another client's lock fails the replace with ActionBlockedByLock, like every optimistic write.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceIfUnchanged_ForeignLock_FailsBlockedByLock()
		{
			var w = await Setup("ri_lock");
			Assert.IsTrue(await Pump(w.A.TryLockAsync(), AllConnections()));

			var lockError = await PumpExpectingError(w.B.ReplaceIfUnchangedAsync(w.B.DisplayName.Guard, new IntegrationTestEntity()), AllConnections());
			Assert.IsNotNull(lockError, "The replace went through another client's lock");
			Assert.AreEqual(ImpunityErrorCode.ActionBlockedByLock, lockError.ErrorId);
			Assert.IsFalse(w.B.WasDeleted);
		}

		/// <summary>A bad guard or a mismatched unique name throws before anything is sent.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceIfUnchanged_BadArguments_Throw()
		{
			var w = await Setup("ri_bad");

			Assert.Throws<ArgumentException>(() => w.B.ReplaceIfUnchanged(default, new IntegrationTestEntity(), null, (_, __) => Assert.Fail("Callback fired")));
			Assert.Throws<ArgumentException>(() => w.B.ReplaceIfUnchanged(w.A.Health.Guard, new IntegrationTestEntity(), null, (_, __) => Assert.Fail("Callback fired")));
			Assert.Throws<ArgumentException>(() => w.B.ReplaceIfUnchanged(w.B.Health.Guard, new IntegrationTestEntity { UniqueName = "elsewhere" }, null, (_, __) => Assert.Fail("Callback fired")));

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}

		/// <summary>A replace guard must name fields of an entity this client has, and agree with replaceEntityId when
		/// that is given too.</summary>
		[Test, Category("GuardedDelete")]
		public async Task ReplaceGuard_WithoutAKnownEntity_Throws()
		{
			var w = await Setup("gr_bad");

			var crop = new IntegrationTestEntity { UniqueName = "tile" };
			Assert.Throws<ArgumentException>(() => w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, (_, __) => Assert.Fail("Callback fired"),
				null, 0, new IntegrationTestEntity().Health.Guard));
			Assert.Throws<ArgumentException>(() => w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, (_, __) => Assert.Fail("Callback fired"),
				null, 999999, w.B.Health.Guard));
			Assert.Throws<ArgumentException>(() => w.ConnB.EntityManager.CreateObject(crop, w.ChannelB, false, (_, __) => Assert.Fail("Callback fired"),
				null, w.B.DistributedEntityId, w.A.Health.Guard));

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}
	}
}
