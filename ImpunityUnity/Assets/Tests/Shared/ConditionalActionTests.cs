// ───────── Conditional Action Tests ─────────
//
// Covers the conditional database action that can ride along with a CreateObject / DeleteEntity: it runs on the
// database thread only if the entity operation succeeded for this client, it gets its own reply (always delivered
// after the entity operation's), and a skipped conditional still gets a reply (ActionConditionNotMet) so nothing
// waits on a timeout. The headline case is the pickup race: two clients delete the same world item, each with an
// "add to my inventory" conditional, and exactly one inventory ends up with it.
//
// These run over local connections. TransportSuite has the over-the-wire cases, which are what exercise the second
// correlation id on the remote path.
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
	/// <summary>Records one callback invocation — whether it fired, its error and value, and (optionally) its place
	/// in a shared order log. Test code can't use the runtime's internal task-completion helpers for a conditional's
	/// callback, and the ordering tests need the log anyway.</summary>
	public sealed class CallbackProbe<T>
	{
		readonly List<string> Log;
		readonly string Tag;

		public bool Fired;
		public int FireCount;
		public ImpunityErrorResponse Error;
		public T Value;

		public CallbackProbe(List<string> log = null, string tag = null)
		{
			Log = log;
			Tag = tag;
		}

		public void OnComplete(ImpunityErrorResponse err, T value)
		{
			Fired = true;
			FireCount++;
			Error = err;
			Value = value;
			Log?.Add(Tag);
		}

		public ImpunityErrorCode? Code => Error?.ErrorCode;
	}

	public class ConditionalActionTests : ImpunityTestHarness
	{
		const int ITEMS = IntegrationTestCollections.ITEMS;

		protected override GameStateFormat CreateFormat()
		{
			return MakeFormat(1);
		}

		static GameStateFormat MakeFormat(int version)
		{
			return new GameStateFormat(
				version,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(IntegrationTestEntity),
					typeof(IntegrationTestChannel),
					typeof(MigTestChannel),
					typeof(MigTestObject)
				}
			);
		}

		// ───────── Helpers ─────────

		/// <summary>Opens a local client with its own ConnectionKey. Every LocalGameConnection otherwise shares
		/// "local_key", and the server would treat them as one lock owner.</summary>
		async Task<LocalGameConnection> ConnectClient(string key)
		{
			var conn = Track(new LocalGameConnection(GameServer, Format));
			conn.ConnectionKey = key;
			await Pump(conn.ConnectAsync(), AllConnections());
			Assert.IsTrue(conn.Connected, "Local connection failed");
			return conn;
		}

		/// <summary>Creates the server, subscribes <paramref name="count"/> distinct clients to one channel, creates a
		/// single "world item" entity on the first, and waits until every client has it. Returns each client's own
		/// instance of the entity (index-aligned with the connections).</summary>
		async Task<(List<LocalGameConnection> conns, List<IntegrationTestEntity> items, IntegrationTestChannel firstChannel)> SetupWorldItem(string channelName, int count)
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

			var item = new IntegrationTestEntity();
			item.DisplayName.Set("sword");
			await Pump(channels[0].Manager.CreateObjectAsync(item, channels[0], false), AllConnections());

			await PumpUntil(() => channels.TrueForAll(c => c.DistributedObjects.Count == 1), TimeSpan.FromSeconds(5), AllConnections());

			var items = new List<IntegrationTestEntity>();
			foreach (var channel in channels)
			{
				foreach (var obj in channel.DistributedObjects.Values)
				{
					items.Add((IntegrationTestEntity)obj);
				}
			}
			return (conns, items, channels[0]);
		}

		Task<BsonDocument> FindDoc(BaseGameConnection conn, string id)
		{
			return Pump(conn.FindDocumentByIdAsync(ITEMS, id), AllConnections());
		}

		static BsonDocument InventoryDoc(string id, string item)
		{
			return new BsonDocument { ["_id"] = id, ["item"] = item };
		}

		// ───────── Delete (pickup) ─────────

		[Test, Category("Conditional")]
		public async Task Delete_WithConditionalInsert_RunsAndRepliesAfterDelete()
		{
			var (conns, items, _) = await SetupWorldItem("cond_pickup", 1);

			var log = new List<string>();
			var deleteProbe = new CallbackProbe<bool>(log, "delete");
			var insertProbe = new CallbackProbe<BsonValue>(log, "insert");

			items[0].Delete(null, deleteProbe.OnComplete, new InsertDocumentAction(ITEMS, InventoryDoc("inv_a", "sword"), insertProbe.OnComplete));

			await PumpUntil(() => deleteProbe.Fired && insertProbe.Fired, AllConnections());

			Assert.IsNull(deleteProbe.Error, "Delete failed: " + deleteProbe.Error?.Message);
			Assert.IsTrue(deleteProbe.Value, "Delete should report it removed the entity");
			Assert.IsNull(insertProbe.Error, "Conditional insert failed: " + insertProbe.Error?.Message);
			Assert.AreEqual("inv_a", insertProbe.Value.AsString, "Conditional reply should carry the insert's own result");
			CollectionAssert.AreEqual(new[] { "delete", "insert" }, log, "The entity operation's callback must fire before the conditional's");

			Assert.IsNotNull(await FindDoc(conns[0], "inv_a"), "Conditional insert did not land");

			// Exactly one reply each.
			await PumpFor(TimeSpan.FromMilliseconds(100), AllConnections());
			Assert.AreEqual(1, deleteProbe.FireCount);
			Assert.AreEqual(1, insertProbe.FireCount);
		}

		/// <summary>The headline guarantee: two clients pick up the same item in the same frame, each adding it to its
		/// own inventory. The live thread serializes the deletes, so exactly one wins, and only the winner's
		/// conditional runs.</summary>
		[Test, Category("Conditional")]
		public async Task Delete_Race_OnlyWinnerGetsItem()
		{
			var (conns, items, _) = await SetupWorldItem("cond_race", 2);

			var deletes = new[] { new CallbackProbe<bool>(), new CallbackProbe<bool>() };
			var inserts = new[] { new CallbackProbe<BsonValue>(), new CallbackProbe<BsonValue>() };

			// Both sent before either client pumps, so neither has seen the other's delete.
			for (int i = 0; i < 2; i++)
			{
				items[i].Delete(null, deletes[i].OnComplete, new InsertDocumentAction(ITEMS, InventoryDoc("inv_" + i, "sword"), inserts[i].OnComplete));
			}

			await PumpUntil(() => deletes[0].Fired && deletes[1].Fired && inserts[0].Fired && inserts[1].Fired, AllConnections());

			int winner = -1;
			for (int i = 0; i < 2; i++)
			{
				if (deletes[i].Error == null && deletes[i].Value)
				{
					Assert.AreEqual(-1, winner, "Both deletes reported success");
					winner = i;
				}
			}
			Assert.AreNotEqual(-1, winner, "Neither delete succeeded");
			int loser = 1 - winner;

			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, deletes[loser].Code, "The loser's delete should find the entity already gone");
			Assert.IsNull(inserts[winner].Error, "Winner's conditional failed: " + inserts[winner].Error?.Message);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, inserts[loser].Code, "The loser's conditional must be skipped, not run");

			Assert.IsNotNull(await FindDoc(conns[0], "inv_" + winner), "Winner's inventory is missing the item");
			Assert.IsNull(await FindDoc(conns[0], "inv_" + loser), "Loser's inventory got the item too — duplicated");
		}

		[Test, Category("Conditional")]
		public async Task Delete_BlockedByLock_SkipsConditional()
		{
			var (conns, items, _) = await SetupWorldItem("cond_locked", 2);

			Assert.IsTrue(await Pump(items[1].TryLockAsync(), AllConnections()), "Client 1 should take the lock");

			var deleteProbe = new CallbackProbe<bool>();
			var insertProbe = new CallbackProbe<BsonValue>();
			items[0].Delete(null, deleteProbe.OnComplete, new InsertDocumentAction(ITEMS, InventoryDoc("inv_locked", "sword"), insertProbe.OnComplete));

			await PumpUntil(() => deleteProbe.Fired && insertProbe.Fired, AllConnections());

			Assert.IsNull(deleteProbe.Error);
			Assert.IsFalse(deleteProbe.Value, "Delete should be refused while another client holds the lock");
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, insertProbe.Code);
			Assert.IsNull(await FindDoc(conns[0], "inv_locked"), "Conditional ran even though the delete was refused");
		}

		[Test, Category("Conditional")]
		public async Task Delete_ParentWithoutCallback_StillDeliversConditional()
		{
			var (conns, items, channel) = await SetupWorldItem("cond_nocb", 1);

			var insertProbe = new CallbackProbe<BsonValue>();
			conns[0].DeleteEntity(items[0].DistributedEntityId, null, null, new InsertDocumentAction(ITEMS, InventoryDoc("inv_nocb", "sword"), insertProbe.OnComplete));

			await PumpUntil(() => insertProbe.Fired, AllConnections());

			Assert.IsNull(insertProbe.Error, "Conditional failed: " + insertProbe.Error?.Message);
			await PumpUntil(() => channel.DistributedObjects.Count == 0, AllConnections());
			Assert.IsNotNull(await FindDoc(conns[0], "inv_nocb"));
		}

		[Test, Category("Conditional")]
		public async Task Conditional_WithoutCallback_StillRuns()
		{
			var (conns, items, _) = await SetupWorldItem("cond_fireforget", 1);

			bool deleted = await Pump(items[0].DeleteAsync(null, new InsertDocumentAction(ITEMS, InventoryDoc("inv_ff", "sword"))), AllConnections());
			Assert.IsTrue(deleted);

			// The conditional was queued on the database thread before the delete replied, and database actions run in
			// FIFO order, so this read runs after it.
			Assert.IsNotNull(await FindDoc(conns[0], "inv_ff"), "A callback-less conditional must still run");
		}

		[Test, Category("Conditional")]
		public async Task Delete_CompoundConditional_ReturnsPerActionResults()
		{
			var (conns, items, _) = await SetupWorldItem("cond_compound", 1);

			var compoundProbe = new CallbackProbe<List<ActionResult>>();
			var compound = new CompoundDatabaseAction(new GameStateActionBase[]
			{
				new InsertDocumentAction(ITEMS, InventoryDoc("inv_c1", "sword")),
				new InsertDocumentAction(ITEMS, InventoryDoc("inv_c2", "scabbard"))
			}, compoundProbe.OnComplete);

			bool deleted = await Pump(items[0].DeleteAsync(null, compound), AllConnections());
			Assert.IsTrue(deleted);

			await PumpUntil(() => compoundProbe.Fired, AllConnections());
			Assert.IsNull(compoundProbe.Error, "Compound conditional failed: " + compoundProbe.Error?.Message);
			Assert.AreEqual(2, compoundProbe.Value.Count);

			Assert.IsNotNull(await FindDoc(conns[0], "inv_c1"));
			Assert.IsNotNull(await FindDoc(conns[0], "inv_c2"));
		}

		/// <summary>A conditional that isn't a permitted database action rejects the whole request before anything is
		/// applied: the entity survives, and the conditional's callback hears why.</summary>
		[Test, Category("Conditional")]
		public async Task InvalidConditional_RejectsWholeRequest()
		{
			var (conns, items, channel) = await SetupWorldItem("cond_invalid", 1);
			uint entityId = items[0].DistributedEntityId;

			// A live action, and a database action outside the allow-list.
			var lockProbe = new CallbackProbe<bool>();
			var invalids = new GameStateActionBase[]
			{
				new LockEntityAction(entityId, false, lockProbe.OnComplete),
				new MigrationScanAction("Items", 0, 10)
			};

			foreach (var invalid in invalids)
			{
				var deleteTask = conns[0].DeleteEntityAsync(entityId, null, invalid);
				var err = await PumpExpectingError(deleteTask, AllConnections());
				Assert.IsNotNull(err, "Delete with a " + invalid.GetType().Name + " conditional should fail");
				Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, err.ErrorId);
			}

			await PumpUntil(() => lockProbe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, lockProbe.Code, "The invalid conditional should get the rejection reason");

			await PumpFor(TimeSpan.FromMilliseconds(100), AllConnections());
			Assert.AreEqual(1, channel.DistributedObjects.Count, "The entity must not be deleted when its conditional is rejected");
		}

		// ───────── Create (drop) ─────────

		[Test, Category("Conditional")]
		public async Task Create_WithConditionalDelete_RemovesDocAfterObjectIsRegistered()
		{
			CreateServer();
			var conn = await ConnectClient("cond_drop");
			var channel = await Pump(conn.EntityManager.SubscribeToChannelAsync("cond_drop", new IntegrationTestChannel()), AllConnections());
			await Pump(conn.InsertDocumentAsync(ITEMS, InventoryDoc("bag_sword", "sword")), AllConnections());

			var log = new List<string>();
			var createProbe = new CallbackProbe<IntegrationTestEntity>(log, "create");
			bool registeredWhenConditionalFired = false;
			var removeProbe = new CallbackProbe<bool>(log, "remove");

			var dropped = new IntegrationTestEntity();
			dropped.DisplayName.Set("sword");
			conn.EntityManager.CreateObject(dropped, channel, false, createProbe.OnComplete,
				new DeleteDocumentAction(ITEMS, "bag_sword", (err, removed) =>
				{
					registeredWhenConditionalFired = channel.DistributedObjects.Count == 1 && dropped.DistributedEntityId != 0;
					removeProbe.OnComplete(err, removed);
				}));

			await PumpUntil(() => createProbe.Fired && removeProbe.Fired, AllConnections());

			Assert.IsNull(createProbe.Error, "Create failed: " + createProbe.Error?.Message);
			Assert.AreSame(dropped, createProbe.Value);
			Assert.IsNull(removeProbe.Error, "Conditional delete failed: " + removeProbe.Error?.Message);
			Assert.IsTrue(removeProbe.Value, "Conditional delete should report it removed the inventory row");
			CollectionAssert.AreEqual(new[] { "create", "remove" }, log);
			Assert.IsTrue(registeredWhenConditionalFired, "The created object should already be registered when the conditional's callback runs");

			Assert.IsNull(await FindDoc(conn, "bag_sword"), "Inventory row was not removed");
		}

		[Test, Category("Conditional")]
		public async Task Create_UniqueNameConflict_SkipsConditional()
		{
			CreateServer();
			var conn = await ConnectClient("cond_dropdupe");
			var channel = await Pump(conn.EntityManager.SubscribeToChannelAsync("cond_dropdupe", new IntegrationTestChannel()), AllConnections());
			await Pump(conn.InsertDocumentAsync(ITEMS, InventoryDoc("bag_gem", "gem")), AllConnections());

			var first = new IntegrationTestEntity { UniqueName = "item_gem" };
			await Pump(conn.EntityManager.CreateObjectAsync(first, channel, false), AllConnections());

			var createProbe = new CallbackProbe<IntegrationTestEntity>();
			var removeProbe = new CallbackProbe<bool>();
			var second = new IntegrationTestEntity { UniqueName = "item_gem" };
			conn.EntityManager.CreateObject(second, channel, false, createProbe.OnComplete,
				new DeleteDocumentAction(ITEMS, "bag_gem", removeProbe.OnComplete));

			await PumpUntil(() => createProbe.Fired && removeProbe.Fired, AllConnections());

			Assert.AreEqual(ImpunityErrorCode.ActionUniqueNameExists, createProbe.Code);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, removeProbe.Code);
			Assert.IsNotNull(await FindDoc(conn, "bag_gem"), "Conditional ran even though the create failed");
		}

		// ───────── Persistence and rejection paths ─────────

		/// <summary>A persisted world item's row delete is queued ahead of the conditional on the FIFO database thread;
		/// after a restart the item is gone from the world and present in the inventory.</summary>
		[Test, Category("Conditional")]
		public async Task PersistedDelete_WithConditional_BothSurviveRestart()
		{
			CreateServer();
			await ConnectLocal();

			var channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("cond_persist", new MigTestChannel { IsPersisted = true }), LocalGame);
			var loot = new MigTestObject { IsPersisted = true, UniqueName = "loot" };
			loot.Score.Set(5);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(loot, channel, false), LocalGame);

			var insertProbe = new CallbackProbe<BsonValue>();
			bool deleted = await Pump(loot.DeleteAsync(null, new InsertDocumentAction(ITEMS, InventoryDoc("inv_loot", "loot"), insertProbe.OnComplete)), LocalGame);
			Assert.IsTrue(deleted);
			await PumpUntil(() => insertProbe.Fired, LocalGame);
			Assert.IsNull(insertProbe.Error, "Conditional failed: " + insertProbe.Error?.Message);

			DisposeConnection(LocalGame);
			GameServer.Dispose();
			GameServer = GameStateServer.Open("test", null, GameStatePath, Options);
			await ConnectLocal();

			var reloaded = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync<MigTestChannel>("cond_persist", null), LocalGame);
			Assert.IsNotNull(reloaded, "Persisted channel did not reload");
			Assert.AreEqual(0, reloaded.DistributedObjects.Count, "The picked-up item came back after a restart");
			Assert.IsNotNull(await FindDoc(LocalGame, "inv_loot"), "The inventory row did not survive a restart");
		}

		/// <summary>A create/delete rejected before it runs (here by the migration gate) must still resolve its
		/// conditional, or the client would wait on it until the action timeout.</summary>
		[Test, Category("Conditional")]
		public async Task ParentRejectedByMigrationGate_ConditionalStillReplies()
		{
			CreateServer();

			// A higher-version client is offered a migration; until it begins or declines, everything else it sends is
			// rejected at the gate in QueueAction.
			var migrator = Track(new LocalGameConnection(GameServer, MakeFormat(2)));
			await Pump(migrator.ConnectAsync(), migrator);
			Assert.IsNotNull(migrator.PendingMigration, "Expected a migration offer");

			var deleteProbe = new CallbackProbe<bool>();
			var insertProbe = new CallbackProbe<BsonValue>();
			migrator.DeleteEntity(1, null, deleteProbe.OnComplete, new InsertDocumentAction(ITEMS, InventoryDoc("inv_gate", "x"), insertProbe.OnComplete));

			await PumpUntil(() => deleteProbe.Fired && insertProbe.Fired, migrator);

			Assert.AreEqual(ImpunityErrorCode.ServerMigrationInProgress, deleteProbe.Code);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, insertProbe.Code);
		}

		// ───────── Update (replicated actions) ─────────

		/// <summary>Potion: a client-authoritative player entity changes and the potion leaves the inventory, both
		/// riding the ordinary per-frame update.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_ClientAuthoritativeUpdate_RunsActionWithIt()
		{
			CreateServer();
			var owner = await ConnectClient("potion_owner");
			var watcher = await ConnectClient("potion_watcher");
			var ownerChannel = await Pump(owner.EntityManager.SubscribeToChannelAsync("potion", new IntegrationTestChannel()), AllConnections());
			var watcherChannel = await Pump(watcher.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("potion", null), AllConnections());
			await Pump(owner.InsertDocumentAsync(ITEMS, InventoryDoc("potion_1", "potion")), AllConnections());

			var player = new IntegrationTestEntity { IsClientAuthoritative = true };
			player.Health.Set(10);
			await Pump(owner.EntityManager.CreateObjectAsync(player, ownerChannel, false), AllConnections());
			await PumpUntil(() => watcherChannel.DistributedObjects.Count == 1, AllConnections());
			IntegrationTestEntity watched = null;
			foreach (var obj in watcherChannel.DistributedObjects.Values)
			{
				watched = (IntegrationTestEntity)obj;
			}

			var drinkProbe = new CallbackProbe<bool>();
			player.Health.Set(60);
			player.AddReplicatedAction(new DeleteDocumentAction(ITEMS, "potion_1", drinkProbe.OnComplete));

			await PumpUntil(() => drinkProbe.Fired, AllConnections());

			Assert.IsNull(drinkProbe.Error, "Replicated action failed: " + drinkProbe.Error?.Message);
			Assert.IsTrue(drinkProbe.Value, "The potion should have been removed from the inventory");
			await PumpUntil(() => watched.Health.Get() == 60, AllConnections());
			Assert.IsNull(await FindDoc(owner, "potion_1"));
		}

		/// <summary>Chest: an optimistic exclusive update puts the item in, and the item leaves the inventory only
		/// because the update was accepted. The update's callback fires before the action's.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_UpdateExclusiveAccepted_RunsAction()
		{
			var (conns, items, _) = await SetupWorldItem("chest_ok", 2);
			await Pump(conns[0].InsertDocumentAsync(ITEMS, InventoryDoc("gem_a", "gem")), AllConnections());

			var log = new List<string>();
			bool updateDone = false;
			ImpunityErrorResponse updateErr = null;
			var removeProbe = new CallbackProbe<bool>(log, "remove");

			items[0].DisplayName.Set("gem");
			items[0].UpdateExclusive(err => { updateDone = true; updateErr = err; log.Add("update"); },
				new DeleteDocumentAction(ITEMS, "gem_a", removeProbe.OnComplete));

			await PumpUntil(() => updateDone && removeProbe.Fired, AllConnections());

			Assert.IsNull(updateErr, "Exclusive update failed: " + updateErr?.Message);
			Assert.IsNull(removeProbe.Error, "Replicated action failed: " + removeProbe.Error?.Message);
			Assert.IsTrue(removeProbe.Value);
			CollectionAssert.AreEqual(new[] { "update", "remove" }, log);
			await PumpUntil(() => items[1].DisplayName.Get() == "gem", AllConnections());
			Assert.IsNull(await FindDoc(conns[0], "gem_a"));
		}

		/// <summary>Two players put different items in the same chest slot at once. The live thread accepts one
		/// exclusive update and rejects the other as stale; only the winner's item leaves its inventory, so the loser
		/// keeps theirs rather than losing it.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_UpdateExclusiveRejected_SkipsAction()
		{
			var (conns, items, _) = await SetupWorldItem("chest_race", 2);
			for (int i = 0; i < 2; i++)
			{
				await Pump(conns[0].InsertDocumentAsync(ITEMS, InventoryDoc("gem_" + i, "gem")), AllConnections());
			}

			var updateDone = new bool[2];
			var updateErr = new ImpunityErrorResponse[2];
			var removes = new[] { new CallbackProbe<bool>(), new CallbackProbe<bool>() };

			// Both sent before either client pumps.
			for (int i = 0; i < 2; i++)
			{
				int idx = i;
				items[i].DisplayName.Set("gem_" + i);
				items[i].UpdateExclusive(err => { updateDone[idx] = true; updateErr[idx] = err; },
					new DeleteDocumentAction(ITEMS, "gem_" + i, removes[i].OnComplete));
			}

			await PumpUntil(() => updateDone[0] && updateDone[1] && removes[0].Fired && removes[1].Fired, AllConnections());

			int winner = updateErr[0] == null ? 0 : 1;
			int loser = 1 - winner;
			Assert.IsNull(updateErr[winner], "Neither exclusive update was accepted");
			Assert.IsNotNull(updateErr[loser], "Both exclusive updates were accepted");
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, updateErr[loser].ErrorCode);

			Assert.IsNull(removes[winner].Error);
			Assert.IsTrue(removes[winner].Value);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, removes[loser].Code, "A rejected update must not run its action");

			Assert.IsNull(await FindDoc(conns[0], "gem_" + winner), "Winner's item should have left the inventory");
			Assert.IsNotNull(await FindDoc(conns[0], "gem_" + loser), "Loser's item was removed from the inventory but never went in the chest");
		}

		/// <summary>A plain update to an entity another client has locked is dropped by the server without a reply;
		/// its action is skipped, and that skip is the one signal the client gets.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_PlainUpdateBlockedByLock_SkipsAction()
		{
			var (conns, items, _) = await SetupWorldItem("cond_upd_locked", 2);
			Assert.IsTrue(await Pump(items[1].TryLockAsync(), AllConnections()), "Client 1 should take the lock");

			var probe = new CallbackProbe<BsonValue>();
			items[0].Health.Set(5);
			items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("inv_blocked", "x"), probe.OnComplete));

			await PumpUntil(() => probe.Fired, AllConnections());

			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, probe.Code);
			Assert.IsNull(await FindDoc(conns[0], "inv_blocked"));
		}

		/// <summary>An action attached when nothing is dirty — here after a Set that was a no-op — rides an empty
		/// update rather than waiting for an unrelated change. Same for an exclusive update with nothing to write.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_NothingDirty_SendsEmptyUpdateCarryingAction()
		{
			var (conns, items, _) = await SetupWorldItem("cond_upd_empty", 1);

			var sweepProbe = new CallbackProbe<BsonValue>();
			items[0].Health.Set(items[0].Health.Get());   // unchanged value: not dirty
			Assert.AreEqual(0UL, items[0].DirtyBits, "Precondition: nothing should be dirty");
			items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("inv_sweep", "x"), sweepProbe.OnComplete));

			await PumpUntil(() => sweepProbe.Fired, AllConnections());
			Assert.IsNull(sweepProbe.Error, "Action on the per-frame sweep failed: " + sweepProbe.Error?.Message);

			bool exclusiveDone = false;
			ImpunityErrorResponse exclusiveErr = null;
			var exclusiveProbe = new CallbackProbe<BsonValue>();
			items[0].UpdateExclusive(err => { exclusiveDone = true; exclusiveErr = err; },
				new InsertDocumentAction(ITEMS, InventoryDoc("inv_excl", "x"), exclusiveProbe.OnComplete));

			await PumpUntil(() => exclusiveDone && exclusiveProbe.Fired, AllConnections());
			Assert.IsNull(exclusiveErr);
			Assert.IsNull(exclusiveProbe.Error, "Action on an empty exclusive update failed: " + exclusiveProbe.Error?.Message);

			Assert.IsNotNull(await FindDoc(conns[0], "inv_sweep"));
			Assert.IsNotNull(await FindDoc(conns[0], "inv_excl"));
		}

		/// <summary>Several actions attached before one send travel as a single compound, and each still gets its own
		/// callback with its own result — including one that fails without stopping the others.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_SeveralPending_EachCallbackGetsOwnResult()
		{
			var (conns, items, _) = await SetupWorldItem("cond_upd_multi", 1);
			await Pump(conns[0].InsertDocumentAsync(ITEMS, InventoryDoc("dupe", "x")), AllConnections());

			var first = new CallbackProbe<BsonValue>();
			var duplicate = new CallbackProbe<BsonValue>();
			var remove = new CallbackProbe<bool>();

			items[0].Health.Set(3);
			items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("multi_1", "a"), first.OnComplete));
			items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("dupe", "b"), duplicate.OnComplete));   // duplicate _id: fails
			items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("multi_3", "c")));                       // no callback
			items[0].AddReplicatedAction(new DeleteDocumentAction(ITEMS, "dupe", remove.OnComplete));

			await PumpUntil(() => first.Fired && duplicate.Fired && remove.Fired, AllConnections());

			Assert.IsNull(first.Error, "First action failed: " + first.Error?.Message);
			Assert.AreEqual("multi_1", first.Value.AsString, "Each callback should get its own action's result");
			Assert.IsNotNull(duplicate.Error, "Inserting a duplicate _id should fail");
			Assert.IsNull(remove.Error, "A failed sibling should not stop later actions");
			Assert.IsTrue(remove.Value);

			Assert.IsNotNull(await FindDoc(conns[0], "multi_3"), "The callback-less action should still run");

			await PumpFor(TimeSpan.FromMilliseconds(100), AllConnections());
			Assert.AreEqual(1, first.FireCount);
			Assert.AreEqual(1, duplicate.FireCount);
			Assert.AreEqual(1, remove.FireCount);
		}

		/// <summary>An action attached inside a RunExclusive scope rides the flush sent just before the unlock.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_InsideRunExclusive_RidesUnlockFlush()
		{
			var (conns, items, channel) = await SetupWorldItem("cond_upd_scope", 1);

			var probe = new CallbackProbe<BsonValue>();
			var result = await Pump(items[0].RunExclusiveAsync(() =>
			{
				items[0].Health.Set(9);
				items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("inv_scope", "x"), probe.OnComplete));
			}, 5f), AllConnections());

			Assert.AreEqual(RunExclusiveResult.Ran, result);
			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.IsNull(probe.Error, "Action attached under the lock failed: " + probe.Error?.Message);
			Assert.IsNotNull(await FindDoc(conns[0], "inv_scope"));
		}

		/// <summary>An entity that goes away with an action still pending resolves that action locally, so its callback
		/// never waits forever; the action is not sent.</summary>
		[Test, Category("Conditional")]
		public async Task Replicated_EntityRemovedWithActionPending_FailsLocally()
		{
			var (conns, items, channel) = await SetupWorldItem("cond_upd_gone", 1);

			var probe = new CallbackProbe<BsonValue>();
			items[0].Health.Set(4);
			items[0].AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("inv_gone", "x"), probe.OnComplete));

			// Immediate mode unregisters the channel's entities synchronously, before any Update() can send.
			conns[0].EntityManager.UnsubscribeFromChannel(channel, null, immediate: true);

			await PumpUntil(() => probe.Fired, AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, probe.Code);
			Assert.IsNull(await FindDoc(conns[0], "inv_gone"));
		}

		[Test, Category("Conditional")]
		public async Task Replicated_InvalidAttachments_FailFast()
		{
			var (conns, items, _) = await SetupWorldItem("cond_upd_invalid", 1);

			Assert.Throws<ArgumentNullException>(() => items[0].AddReplicatedAction(null));
			Assert.Throws<ArgumentException>(() => items[0].AddReplicatedAction(new LockEntityAction(items[0].DistributedEntityId)),
				"A live action must be refused up front, before it can sink the update it rides");

			var loose = new IntegrationTestEntity();   // never registered with a connection
			Assert.Throws<InvalidOperationException>(() => loose.AddReplicatedAction(new InsertDocumentAction(ITEMS, InventoryDoc("inv_loose", "x"))));

			await PumpFor(TimeSpan.FromMilliseconds(50), AllConnections());
			Assert.IsNull(await FindDoc(conns[0], "inv_loose"));
		}
	}
}
