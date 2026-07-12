// ───────── Integration Tests ─────────
//
// Ported from Assets/Tests/PlayMode/ImpunityIntegrationTests.cs onto the async API + harness.
// Covers: local CRUD, TCP DB ops, live channels/entities, broadcasts, named locks, distributed
// collections, unsubscribe modes, exclusive (optimistic-concurrency) updates, delete-on-disconnect.
//
// Staleness tests deliberately pump only ONE connection (e.g. PumpUntil(..., LocalGame)) so the other
// client never processes the winner's broadcast — pass explicit connections, never AllConnections(),
// when a test needs a client to stay behind.
#nullable disable

using System;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class IntegrationTests : ImpunityTestHarness
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

		static IntegrationTestEntity FirstEntity(IntegrationTestChannel channel)
		{
			foreach (var obj in channel.DistributedObjects.Values)
			{
				return obj as IntegrationTestEntity;
			}
			return null;
		}

		/// <summary>Subscribes C1 (creating the channel) and C2, creates one entity on C1, and waits
		/// until C2 has replicated it. Returns C2's channel + entity.</summary>
		async Task<(IntegrationTestChannel c2Channel, IntegrationTestEntity c2Entity)> SetupTwoClientChannel(string channelName)
		{
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());

			var entity = new IntegrationTestEntity();
			entity.Health.Set(100);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(entity, c1Channel, false), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity, "C2 did not replicate the entity");

			return (c2Channel, c2Entity);
		}

		/// <summary>Subscribes C1 (local) and C2 (remote) to a channel, has C1 create an entity with a
		/// baseline Health, waits for C2 to replicate it, and returns both clients' instances so both
		/// sides can be asserted on.</summary>
		async Task<(IntegrationTestEntity c1Entity, IntegrationTestEntity c2Entity)> SetupTwoClientEntity(string channelName)
		{
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());

			var c1Entity = new IntegrationTestEntity();
			c1Entity.Health.Set(100);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(c1Entity, c1Channel, false), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity, "C2 did not replicate the entity");

			return (c1Entity, c2Entity);
		}

		// ═══════════════════════════════════════════════════════════
		// 1. Local Connection — Database CRUD
		// ═══════════════════════════════════════════════════════════

		[Test, Category("LocalConnection")]
		public async Task LocalConnect()
		{
			CreateServer();
			await ConnectLocal();
			Assert.IsTrue(LocalGame.Connected);
		}

		[Test, Category("LocalConnection")]
		public async Task LocalInsertAndFind()
		{
			CreateServer();
			await ConnectLocal();

			var doc = new BsonDocument { ["_id"] = "item1", ["name"] = "Sword", ["power"] = 42 };
			var insertResult = await Pump(LocalGame.InsertDocumentAsync(IntegrationTestCollections.ITEMS, doc), LocalGame);
			Assert.IsNotNull(insertResult);

			var found = await Pump(LocalGame.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, "item1"), LocalGame);
			Assert.IsNotNull(found);
			Assert.AreEqual("Sword", (string)found["name"]);
			Assert.AreEqual(42, (int)found["power"]);
		}

		[Test, Category("LocalConnection")]
		public async Task LocalUpsertAndList()
		{
			CreateServer();
			await ConnectLocal();

			var doc1 = new BsonDocument { ["_id"] = "a", ["val"] = 1 };
			var doc2 = new BsonDocument { ["_id"] = "b", ["val"] = 2 };

			await Pump(LocalGame.UpsertDocumentAsync(IntegrationTestCollections.ITEMS, doc1), LocalGame);
			await Pump(LocalGame.UpsertDocumentAsync(IntegrationTestCollections.ITEMS, doc2), LocalGame);

			var list = await Pump(LocalGame.ListDocumentsAsync(IntegrationTestCollections.ITEMS), LocalGame);
			Assert.AreEqual(2, list.Count);
		}

		[Test, Category("LocalConnection")]
		public async Task LocalDelete()
		{
			CreateServer();
			await ConnectLocal();

			var doc = new BsonDocument { ["_id"] = "del1", ["x"] = 1 };
			await Pump(LocalGame.InsertDocumentAsync(IntegrationTestCollections.ITEMS, doc), LocalGame);

			var deleted = await Pump(LocalGame.DeleteDocumentAsync(IntegrationTestCollections.ITEMS, "del1"), LocalGame);
			Assert.IsTrue(deleted);

			var found = await Pump(LocalGame.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, "del1"), LocalGame);
			Assert.IsNull(found);
		}

		[Test, Category("LocalConnection")]
		public async Task LocalCompoundAction()
		{
			CreateServer();
			await ConnectLocal();

			var doc1 = new BsonDocument { ["_id"] = "c1", ["v"] = 10 };
			var doc2 = new BsonDocument { ["_id"] = "c2", ["v"] = 20 };

			var results = await Pump(LocalGame.CompoundDatabaseActionAsync(new GameStateActionBase[]
			{
				new UpsertDocumentAction(IntegrationTestCollections.ITEMS, doc1),
				new UpsertDocumentAction(IntegrationTestCollections.ITEMS, doc2),
				new ListDocumentsAction(IntegrationTestCollections.ITEMS),
			}), LocalGame);
			Assert.AreEqual(3, results.Count);
		}

		// ═══════════════════════════════════════════════════════════
		// 2. TCP Connection
		// ═══════════════════════════════════════════════════════════

		[Test, Category("TCPConnection")]
		public async Task TCPConnect()
		{
			CreateServer();
			await StartTCPAndConnectRemote();
			Assert.IsTrue(RemoteGame.Connected);
		}

		[Test, Category("TCPConnection")]
		public async Task TCPDatabaseOps()
		{
			CreateServer();
			await StartTCPAndConnectRemote();

			var doc = new BsonDocument { ["_id"] = "tcp1", ["name"] = "Shield" };
			await Pump(RemoteGame.InsertDocumentAsync(IntegrationTestCollections.ITEMS, doc), RemoteGame);

			var found = await Pump(RemoteGame.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, "tcp1"), RemoteGame);
			Assert.AreEqual("Shield", (string)found["name"]);
		}

		[Test, Category("TCPConnection")]
		public async Task TCPMergeInsertCreatesMissingDocument()
		{
			CreateServer();
			await StartTCPAndConnectRemote();

			// Merge-insert against a non-existent _id must INSERT it. Over a remote connection this used to silently
			// degrade to a plain merge-into (wrong action-type id), leaving nothing stored.
			var doc = new BsonDocument { ["_id"] = "mi1", ["name"] = "Potion" };
			await Pump(RemoteGame.MergeInsertDocumentAsync(IntegrationTestCollections.ITEMS, doc), RemoteGame);

			var found = await Pump(RemoteGame.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, "mi1"), RemoteGame);
			Assert.IsNotNull(found, "MergeInsert should have inserted the missing document over a remote connection");
			Assert.AreEqual("Potion", (string)found["name"]);

			// A second merge-insert into the same _id must MERGE the new field while preserving the original.
			var patch = new BsonDocument { ["_id"] = "mi1", ["power"] = 7 };
			await Pump(RemoteGame.MergeInsertDocumentAsync(IntegrationTestCollections.ITEMS, patch), RemoteGame);

			var found2 = await Pump(RemoteGame.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, "mi1"), RemoteGame);
			Assert.AreEqual("Potion", (string)found2["name"], "Original field should survive the merge");
			Assert.AreEqual(7, (int)found2["power"], "New field should be merged in");
		}

		[Test, Category("TCPConnection")]
		public async Task TCPConnectBadPassword()
		{
			// Create server WITH password (no UpdateFormat — the connect fails at the password check).
			var summary = new BsonDocument { ["name"] = "PasswordTest" };
			GameServer = GameStateServer.Create("test", "secret123", GameStatePath, summary, Options);

			StartTcpServer();
			await Task.Delay(100);

			// Connect with WRONG password.
			RemoteGame = Track(RemoteGameConnection.MakeTCPRemoteConnection(
				TcpServer.TCPEndpoint, "test", "wrongpassword", Format, Options));

			var connectTask = RemoteGame.ConnectAsync();
			await PumpUntilComplete(connectTask, RemoteGame);

			Assert.IsTrue(connectTask.IsFaulted, "Expected connection to fail with bad password");
		}

		// ═══════════════════════════════════════════════════════════
		// 3. Live Channels and Entities
		// ═══════════════════════════════════════════════════════════

		[Test, Category("LiveState")]
		public async Task SubscribeToChannel()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1 creates channel
			var c1Init = new IntegrationTestChannel();
			c1Init.Status.Set("active");
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("lobby", c1Init), AllConnections());
			Assert.IsNotNull(c1Channel);

			// C2 subscribes
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("lobby", null), AllConnections());

			Assert.IsNotNull(c2Channel);
			Assert.AreEqual("active", c2Channel.Status.Get());
		}

		[Test, Category("LiveState")]
		public async Task CreateAndReplicateEntity()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1 creates channel
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("room", new IntegrationTestChannel()), AllConnections());

			// C2 subscribes
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("room", null), AllConnections());

			// C1 creates entity
			var entity = new IntegrationTestEntity();
			entity.Health.Set(100);
			entity.DisplayName.Set("Hero");
			await Pump(LocalGame.EntityManager.CreateObjectAsync(entity, c1Channel, false), AllConnections());

			// Wait for C2 to receive it
			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			Assert.AreEqual(1, c2Channel.DistributedObjects.Count);

			// Find the replicated entity on C2
			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity);
			Assert.AreEqual(100, c2Entity.Health.Get());
			Assert.AreEqual("Hero", c2Entity.DisplayName.Get());
		}

		[Test, Category("LiveState")]
		public async Task DistributedValueReplication()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// Setup channel + entity on C1
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("sync", new IntegrationTestChannel()), AllConnections());

			var entity = new IntegrationTestEntity();
			entity.Health.Set(50);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(entity, c1Channel, false), AllConnections());

			// C2 subscribes
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("sync", null), AllConnections());

			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity);
			Assert.AreEqual(50, c2Entity.Health.Get());

			// C1 changes value
			int newHealthReceived = -1;
			c2Entity.Health.OnChanged += (oldVal, newVal) => { newHealthReceived = newVal; };

			entity.Health.Set(75);

			// Tick until C2 sees the change
			await PumpUntil(() => newHealthReceived == 75, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(75, c2Entity.Health.Get());
		}

		[Test, Category("LiveState")]
		public async Task EntityDeletion()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// Setup
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("del", new IntegrationTestChannel()), AllConnections());

			var entity = new IntegrationTestEntity();
			entity.Health.Set(1);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(entity, c1Channel, false), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("del", null), AllConnections());

			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			// Track deletion on C2
			var c2Entity = FirstEntity(c2Channel);

			// The channel must be notified when the object is removed. This relies on the manager
			// populating IDistributedObject.Channel at create time and reaching it on delete.
			IDistributedObject removedObj = null;
			c2Channel.OnObjectRemovedEvent += o => removedObj = o;

			// C1 deletes entity
			entity.Delete("goodbye", null);

			// Wait for C2 to see deletion
			await PumpUntil(() => c2Entity.WasDeleted, TimeSpan.FromSeconds(3), AllConnections());
			Assert.IsTrue(c2Entity.WasDeleted, "OnDeleted did not fire on the deleted object");
			Assert.AreEqual(1, c2Entity.UndistributedCount, "OnUndistributed not fired exactly once on the deleted object");
			Assert.AreSame(c2Entity, removedObj, "Channel OnObjectRemoved did not fire for the deleted object");
			Assert.IsFalse(c2Channel.DistributedObjects.ContainsKey(c2Entity.DistributedEntityId),
				"Deleted object was not removed from the channel's DistributedObjects");
		}

		// ═══════════════════════════════════════════════════════════
		// 4. Broadcasts
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Broadcasts")]
		public async Task BroadcastMessage()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			int receivedType = -1;
			string receivedBody = null;

			LocalGame.OnBroadcastMessage = (type, body, sender) =>
			{
				receivedType = type;
				receivedBody = body.AsString;
			};

			RemoteGame.SendBroadcastMessage(42, "Hello from remote!");

			await PumpUntil(() => receivedType == 42, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(42, receivedType);
			Assert.AreEqual("Hello from remote!", receivedBody);
		}

		// ═══════════════════════════════════════════════════════════
		// 5. Named Locks
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Locks")]
		public async Task NamedLock_TryLock()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1 locks
			var locked1 = await Pump(LocalGame.TryToLockAsync("myLock"), AllConnections());
			Assert.IsTrue(locked1, "C1 should acquire lock");

			// C2 tries to lock — should fail
			var locked2 = await Pump(RemoteGame.TryToLockAsync("myLock"), AllConnections());
			Assert.IsFalse(locked2, "C2 should fail to acquire held lock");

			// C1 unlocks
			var unlocked = await Pump(LocalGame.UnlockAsync("myLock"), AllConnections());
			Assert.IsTrue(unlocked);

			// C2 can now lock
			var locked3 = await Pump(RemoteGame.TryToLockAsync("myLock"), AllConnections());
			Assert.IsTrue(locked3, "C2 should acquire lock after C1 released");

			// Cleanup
			await Pump(RemoteGame.UnlockAsync("myLock"), AllConnections());
		}

		// ═══════════════════════════════════════════════════════════
		// 6. Distributed Collection Types
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Collections")]
		public async Task DistributedArray_Replication()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1 creates channel with array
			var c1Init = new IntegrationTestChannel();
			c1Init.Grid.Replace(new int[] { 10, 20, 30 });
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("arr", c1Init), AllConnections());

			// C2 subscribes
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("arr", null), AllConnections());

			Assert.AreEqual(10, c2Channel.Grid.Get(0));
			Assert.AreEqual(20, c2Channel.Grid.Get(1));
			Assert.AreEqual(30, c2Channel.Grid.Get(2));

			// C1 updates element
			int changedIndex = -1;
			c2Channel.Grid.OnChanged += (idx, oldVal, newVal) => { changedIndex = idx; };

			c1Channel.Grid.Set(1, 99);

			await PumpUntil(() => changedIndex == 1, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(99, c2Channel.Grid.Get(1));
		}

		[Test, Category("Collections")]
		public async Task DistributedQueue_Replication()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var c1Init = new IntegrationTestChannel();
			c1Init.Chat.Init(100);
			c1Init.Chat.Add("hello");
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("queue", c1Init), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("queue", null), AllConnections());

			string receivedMsg = null;
			c2Channel.Chat.OnChanged += (msg) => { receivedMsg = msg; };

			// C1 adds to queue
			c1Channel.Chat.Add("world");

			await PumpUntil(() => receivedMsg == "world", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("world", receivedMsg);
		}

		[Test, Category("Collections")]
		public async Task DistributedDictionary_Replication()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var c1Init = new IntegrationTestChannel();
			c1Init.Flags.Init();
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("dict", c1Init), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("dict", null), AllConnections());

			int changedKey = -1;
			string changedVal = null;
			c2Channel.Flags.OnChanged += (key, oldVal, newVal) => { changedKey = key; changedVal = newVal; };

			// C1 adds a key
			c1Channel.Flags.Add(7, "active");

			await PumpUntil(() => changedKey == 7, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("active", changedVal);
			Assert.AreEqual("active", c2Channel.Flags.Get(7));
		}

		[Test, Category("Collections")]
		public async Task DistributedStack_Replication()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1 creates the channel with initial stack contents (the full-set path)
			var c1Init = new IntegrationTestChannel();
			c1Init.History.Replace(new[] { "alpha", "beta" });
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("stack", c1Init), AllConnections());

			// C2 subscribes and sees the initial state; the last replaced value is the top
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("stack", null), AllConnections());
			Assert.AreEqual(2, c2Channel.History.Count);
			Assert.AreEqual("beta", c2Channel.History.Peek());

			string pushedVal = null;
			string poppedVal = null;
			string changedTop = null;
			int replacedCount = 0;
			c2Channel.History.OnPushed += v => pushedVal = v;
			c2Channel.History.OnPopped += v => poppedVal = v;
			c2Channel.History.OnTopChanged += (oldTop, newTop) => changedTop = newTop;
			c2Channel.History.OnReplaced += (oldList, newList) => replacedCount++;

			// Push replicates as a delta
			c1Channel.History.Push("gamma");
			await PumpUntil(() => pushedVal == "gamma", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("gamma", c2Channel.History.Peek());
			Assert.AreEqual(3, c2Channel.History.Count);

			// Non-client-authoritative: C1's own value applies on the server echo
			await PumpUntil(() => c1Channel.History.Count == 3, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("gamma", c1Channel.History.Peek());

			// SetTop replaces in place
			c1Channel.History.SetTop("gamma2");
			await PumpUntil(() => changedTop == "gamma2", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("gamma2", c2Channel.History.Peek());
			Assert.AreEqual(3, c2Channel.History.Count);

			// Pop removes the top
			c1Channel.History.Pop();
			await PumpUntil(() => poppedVal == "gamma2", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(2, c2Channel.History.Count);
			Assert.AreEqual("beta", c2Channel.History.Peek());

			// Mutations replicate in the other direction too
			c2Channel.History.Push("delta");
			await PumpUntil(() => c1Channel.History.Count == 3, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("delta", c1Channel.History.Peek());

			// Clear resets everyone to empty via a full replace
			c1Channel.History.Clear();
			await PumpUntil(() => replacedCount > 0, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(0, c2Channel.History.Count);
			Assert.IsFalse(c2Channel.History.TryPeek(out _));
		}

		// ═══════════════════════════════════════════════════════════
		// 7. Unsubscribe
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Unsubscribe")]
		public async Task Unsubscribe_Deferred_TearsDownWithCallbacksOnAck()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c2Channel, c2Entity) = await SetupTwoClientChannel("deferred");

			// Default (immediate == false): objects stay live until the server acks the unsubscribe.
			var unsubTask = c2Channel.UnsubscribeAsync(immediate: false);

			// Before the ack returns, nothing has been torn down yet.
			Assert.AreEqual(0, c2Channel.UndistributedCount, "Channel torn down too early");
			Assert.AreEqual(0, c2Entity.UndistributedCount, "Entity torn down too early");
			Assert.AreEqual(1, c2Channel.DistributedObjects.Count, "Objects removed too early");

			await Pump(unsubTask, AllConnections());

			// On completion: OnUndistributed fired exactly once on the entity and the channel,
			// and all references are released from the manager.
			Assert.AreEqual(1, c2Entity.UndistributedCount, "Entity OnUndistributed not fired exactly once");
			Assert.AreEqual(1, c2Channel.UndistributedCount, "Channel OnUndistributed not fired exactly once");
			Assert.AreEqual(0, c2Channel.DistributedObjects.Count, "Channel child objects not cleared");

			// Re-subscribing returns a fresh channel instance (proves the old one was fully released).
			var resub = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("deferred", null), AllConnections());
			Assert.AreNotSame(c2Channel, resub, "Re-subscribe returned the stale channel");
		}

		[Test, Category("Unsubscribe")]
		public async Task Unsubscribe_Immediate_SuppressesInFlightAndTearsDownSynchronously()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1-side handles for generating an in-flight burst.
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("immediate", new IntegrationTestChannel()), AllConnections());

			var c1Entity = new IntegrationTestEntity();
			c1Entity.Health.Set(100);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(c1Entity, c1Channel, false), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("immediate", null), AllConnections());
			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity);

			// Watch for any suppressed traffic leaking through to C2.
			bool healthChangedOnC2 = false;
			c2Entity.Health.OnChanged += (oldVal, newVal) => { healthChangedOnC2 = true; };
			bool newObjectSeenOnC2 = false;
			RemoteGame.EntityManager.OnDistributedObjectCreated = (obj, ch, created) => { newObjectSeenOnC2 = true; };

			// Immediate unsubscribe: synchronous teardown, no lifecycle callbacks.
			var unsubTask = c2Channel.UnsubscribeAsync(immediate: true);
			Assert.AreEqual(0, c2Channel.UndistributedCount, "Immediate mode must not invoke OnUndistributed");
			Assert.AreEqual(0, c2Entity.UndistributedCount, "Immediate mode must not invoke OnUndistributed");
			Assert.AreEqual(0, c2Channel.DistributedObjects.Count, "Channel not torn down synchronously");

			// Now generate an in-flight burst from C1 (update + brand-new object) that races the unsubscribe.
			c1Entity.Health.Set(999);
			var burstEntity = new IntegrationTestEntity();
			burstEntity.Health.Set(7);
			LocalGame.EntityManager.CreateObject(burstEntity, c1Channel, false, null);

			await Pump(unsubTask, AllConnections());
			// Drain a few extra frames to make sure nothing arrives late.
			await PumpFor(TimeSpan.FromSeconds(0.5), AllConnections());

			Assert.IsFalse(healthChangedOnC2, "Suppressed update leaked to C2 after immediate unsubscribe");
			Assert.IsFalse(newObjectSeenOnC2, "Suppressed object-create leaked to C2 after immediate unsubscribe");
		}

		[Test, Category("Unsubscribe")]
		public async Task Unsubscribe_Immediate_AllowsResubscribe()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c2Channel, c2Entity) = await SetupTwoClientChannel("resub");

			await Pump(c2Channel.UnsubscribeAsync(immediate: true), AllConnections());

			// Re-subscribe to the same channel: suppression must be fully lifted and fresh state replicated.
			var c2Channel2 = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("resub", null), AllConnections());
			Assert.IsNotNull(c2Channel2);
			Assert.AreNotSame(c2Channel, c2Channel2, "Re-subscribe returned the stale channel");

			await PumpUntil(() => c2Channel2.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(1, c2Channel2.DistributedObjects.Count, "Entity did not replicate after re-subscribe");

			var reEntity = FirstEntity(c2Channel2);
			Assert.IsNotNull(reEntity);
			Assert.AreEqual(100, reEntity.Health.Get(), "Re-subscribed entity has wrong state");
		}

		// ═══════════════════════════════════════════════════════════
		// 8. Exclusive (optimistic-concurrency) Updates
		// ═══════════════════════════════════════════════════════════

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_FreshClientSucceeds()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl1");

			// Settle a baseline both sides agree on.
			c1Entity.Health.Set(50);
			await PumpUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, TimeSpan.FromSeconds(3), AllConnections());

			// C2, fully up to date, exclusively writes a new value.
			bool done = false; ImpunityErrorResponse err = null; int valueInCallback = -1;
			c2Entity.Health.Set(60);
			c2Entity.UpdateExclusive((e) => { done = true; err = e; valueInCallback = c2Entity.Health.Get(); });

			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNull(err, "Fresh exclusive update should succeed");
			Assert.AreEqual(60, valueInCallback, "Winner's own echo should be applied before the success callback fires");
			await PumpUntil(() => c1Entity.Health.Get() == 60, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(60, c1Entity.Health.Get(), "Value did not replicate to C1");
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_StaleRejectsWholeUpdate()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl2");

			c1Entity.Health.Set(50);
			await PumpUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, TimeSpan.FromSeconds(3), AllConnections());

			// C1 changes Health and lands it on the server WITHOUT letting C2 process the resulting broadcast
			// (tick only C1's connection). C2's known seq for Health is now behind the server's.
			c1Entity.Health.Set(70);
			await PumpUntil(() => c1Entity.Health.Get() == 70, TimeSpan.FromSeconds(3), LocalGame);

			// Watch C1 for any leaked change from C2's doomed update.
			bool c1HealthChanged = false, c1NameChanged = false;
			c1Entity.Health.OnChanged += (o, n) => c1HealthChanged = true;
			c1Entity.DisplayName.OnChanged += (o, n) => c1NameChanged = true;

			// Stale C2 writes Health AND DisplayName in one exclusive update.
			bool done = false; ImpunityErrorResponse err = null; int healthInCallback = -1; string nameInCallback = null;
			c2Entity.Health.Set(99);
			c2Entity.DisplayName.Set("loser");
			c2Entity.UpdateExclusive((e) => { done = true; err = e; healthInCallback = c2Entity.Health.Get(); nameInCallback = c2Entity.DisplayName.Get(); });

			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNotNull(err, "Stale exclusive update should be rejected");
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, err.ErrorCode, "Rejection should be ActionStaleData");
			Assert.AreEqual(70, healthInCallback, "Winner's value should already be applied on C2 when the error callback fires");

			// Give any (erroneously) relayed update time to arrive.
			await PumpFor(TimeSpan.FromSeconds(0.5), AllConnections());

			Assert.IsFalse(c1HealthChanged, "Stale Health leaked through to C1");
			Assert.IsFalse(c1NameChanged, "Batched DisplayName was not dropped with the stale update (not all-or-nothing)");
			Assert.AreEqual(70, c1Entity.Health.Get(), "C1 Health changed despite the rejected update");
			Assert.AreNotEqual("loser", nameInCallback, "Rejected DisplayName should not have applied locally on C2 (non-client-auth applies only on echo)");
			Assert.AreNotEqual("loser", c1Entity.DisplayName.Get(), "C1 DisplayName changed despite all-or-nothing rejection");
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_UnrelatedFieldChangeDoesNotConflict()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl3");

			c1Entity.Health.Set(50);
			await PumpUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, TimeSpan.FromSeconds(3), AllConnections());

			// C1 changes Health (unseen by C2).
			c1Entity.Health.Set(70);
			await PumpUntil(() => c1Entity.Health.Get() == 70, TimeSpan.FromSeconds(3), LocalGame);

			// C2 is stale on Health, but exclusively writes only DisplayName — a different field — so it must succeed
			// (staleness is scoped to written fields only).
			bool done = false; ImpunityErrorResponse err = null;
			c2Entity.DisplayName.Set("hello");
			c2Entity.UpdateExclusive((e) => { done = true; err = e; });

			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNull(err, "Exclusive write to an unrelated field must not conflict with a concurrent change to a different field");
			await PumpUntil(() => c1Entity.DisplayName.Get() == "hello", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual("hello", c1Entity.DisplayName.Get());
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_RaceExactlyOneWinner()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl4");

			c1Entity.Health.Set(50);
			await PumpUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, TimeSpan.FromSeconds(3), AllConnections());

			// Both clients (each fully up to date) race to set the same field in the same frame.
			int c1Done = 0, c2Done = 0; int errorCount = 0;
			c1Entity.Health.Set(11);
			c1Entity.UpdateExclusive((e) => { c1Done++; if (e != null) errorCount++; });
			c2Entity.Health.Set(22);
			c2Entity.UpdateExclusive((e) => { c2Done++; if (e != null) errorCount++; });

			await PumpUntil(() => c1Done > 0 && c2Done > 0, TimeSpan.FromSeconds(3), AllConnections());

			Assert.AreEqual(1, errorCount, "Exactly one of the racing exclusive updates should be rejected as stale");
			// Both sides converge on the same (winner's) value.
			await PumpUntil(() => c1Entity.Health.Get() == c2Entity.Health.Get() && c1Entity.Health.Get() != 50, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(c1Entity.Health.Get(), c2Entity.Health.Get(), "Clients did not converge on the winner's value");
			Assert.IsTrue(c1Entity.Health.Get() == 11 || c1Entity.Health.Get() == 22, "Converged value should be one of the two contenders");
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_NotDirtyCompletesImmediately()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl5");

			ushort seqBefore = c2Entity.SendSeq;
			bool done = false; ImpunityErrorResponse err = null;
			c2Entity.UpdateExclusive((e) => { done = true; err = e; });

			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNull(err, "UpdateExclusive with nothing dirty should succeed");
			Assert.AreEqual(seqBefore, c2Entity.SendSeq, "No message should be sent when nothing is dirty");
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_LateSubscriberSeeded()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// C1 creates the entity and performs several normal updates, driving the server's OutSeq/LastModSeq well past 0.
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("excl6", new IntegrationTestChannel()), AllConnections());

			var c1Entity = new IntegrationTestEntity();
			c1Entity.Health.Set(1);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(c1Entity, c1Channel, false), AllConnections());

			for (int i = 2; i <= 6; i++)
			{
				c1Entity.Health.Set(i);
				await PumpUntil(() => c1Entity.Health.Get() == i, TimeSpan.FromSeconds(3), AllConnections());
			}

			// C2 subscribes only now — its FieldRecvSeq must be seeded from the entity's current OutSeq, or its exclusive
			// update below would be rejected forever (livelock).
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("excl6", null), AllConnections());
			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity);
			Assert.AreEqual(6, c2Entity.Health.Get(), "Late joiner did not replicate current state");

			bool done = false; ImpunityErrorResponse err = null;
			c2Entity.Health.Set(60);
			c2Entity.UpdateExclusive((e) => { done = true; err = e; });
			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNull(err, "Late subscriber's first exclusive update should succeed (FieldRecvSeq must be seeded from create-time OutSeq)");
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_CreatorOfFreshObjectSucceeds()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("excl7", new IntegrationTestChannel()), AllConnections());

			var c1Entity = new IntegrationTestEntity();
			c1Entity.Health.Set(10);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(c1Entity, c1Channel, false), AllConnections());

			// Immediately after create, the creator (FieldRecvSeq all-zero, server OutSeq/LastModSeq all-zero) writes exclusively.
			bool done = false; ImpunityErrorResponse err = null;
			c1Entity.Health.Set(20);
			c1Entity.UpdateExclusive((e) => { done = true; err = e; });
			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNull(err, "Creator of a fresh object should be able to exclusively update it");

			// And a second subscriber sees the value.
			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("excl7", null), AllConnections());
			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());
			var c2Entity = FirstEntity(c2Channel);
			Assert.IsNotNull(c2Entity);
			Assert.AreEqual(20, c2Entity.Health.Get());
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_ChannelEntityFields()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// SubscribeToChannel does NOT keep the createIfNeeded instance — the server sends a create message and a fresh
			// instance is registered and returned. Drive that one, not the throwaway.
			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("excl8", new IntegrationTestChannel()), AllConnections());

			var c2Channel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("excl8", null), AllConnections());

			// Settle a baseline channel field.
			c1Channel.Status.Set("ready");
			await PumpUntil(() => c1Channel.Status.Get() == "ready" && c2Channel.Status.Get() == "ready", TimeSpan.FromSeconds(3), AllConnections());

			// C1 changes Status, unseen by C2.
			c1Channel.Status.Set("busy");
			await PumpUntil(() => c1Channel.Status.Get() == "busy", TimeSpan.FromSeconds(3), LocalGame);

			// Stale C2 exclusive update on the channel entity is rejected.
			bool done = false; ImpunityErrorResponse err = null;
			c2Channel.Status.Set("stale");
			c2Channel.UpdateExclusive((e) => { done = true; err = e; });
			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNotNull(err, "Stale exclusive update on a channel entity should be rejected");
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, err.ErrorCode);
			Assert.AreNotEqual("stale", c1Channel.Status.Get(), "Rejected channel update must not apply");
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_LockHolderBypassesStaleness()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl9");

			c1Entity.Health.Set(50);
			await PumpUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, TimeSpan.FromSeconds(3), AllConnections());

			// Make C2 stale on Health.
			c1Entity.Health.Set(70);
			await PumpUntil(() => c1Entity.Health.Get() == 70, TimeSpan.FromSeconds(3), LocalGame);

			// C2 takes the lock, which is a stronger guarantee than the seq check and bypasses staleness.
			var locked = await Pump(c2Entity.TryLockAsync(), AllConnections());
			Assert.IsTrue(locked, "C2 should acquire the lock");

			bool done = false; ImpunityErrorResponse err = null;
			c2Entity.Health.Set(88);
			c2Entity.UpdateExclusive((e) => { done = true; err = e; });
			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNull(err, "Lock holder's exclusive update should bypass the staleness check");
			await PumpUntil(() => c1Entity.Health.Get() == 88, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(88, c1Entity.Health.Get());
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_LockedByOtherReturnsError()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var (c1Entity, c2Entity) = await SetupTwoClientEntity("excl10");

			c1Entity.Health.Set(50);
			await PumpUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, TimeSpan.FromSeconds(3), AllConnections());

			// C1 locks the entity.
			var locked = await Pump(c1Entity.TryLockAsync(), AllConnections());
			Assert.IsTrue(locked);
			await PumpUntil(() => c2Entity.IsLocked, TimeSpan.FromSeconds(3), AllConnections());

			// C2's exclusive update should get an explicit lock error (not silence).
			bool done = false; ImpunityErrorResponse err = null;
			c2Entity.Health.Set(99);
			c2Entity.UpdateExclusive((e) => { done = true; err = e; });
			await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());

			Assert.IsNotNull(err, "Exclusive update on an entity locked by another client should error");
			Assert.AreEqual(ImpunityErrorCode.ActionBlockedByLock, err.ErrorCode);
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_ClientAuthoritativeAlwaysPasses()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var c1Channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("excl11", new IntegrationTestChannel()), AllConnections());

			// Client-authoritative entities are auto-locked to their creator and never echoed, so their FieldRecvSeq never
			// advances — the lock bypass is what keeps exclusive updates working for them.
			var c1Entity = new IntegrationTestEntity();
			c1Entity.IsClientAuthoritative = true;
			c1Entity.Health.Set(1);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(c1Entity, c1Channel, false), AllConnections());

			for (int i = 0; i < 3; i++)
			{
				bool done = false; ImpunityErrorResponse err = null;
				c1Entity.Health.Set(100 + i);
				c1Entity.UpdateExclusive((e) => { done = true; err = e; });
				await PumpUntil(() => done, TimeSpan.FromSeconds(3), AllConnections());
				Assert.IsNull(err, "Client-authoritative creator's exclusive update #" + i + " should always pass");
			}
		}

		[Test, Category("ExclusiveUpdate")]
		public async Task Exclusive_UnregisteredEntityErrors()
		{
			CreateServer();
			await ConnectLocal();

			// A bare, never-registered entity has no manager/connection to flush through (its fields are initialized by
			// the DistributedEntityBase constructor, so Set works and applies locally).
			var orphan = new IntegrationTestEntity();
			orphan.Health.Set(5);

			bool done = false; ImpunityErrorResponse err = null;
			orphan.UpdateExclusive((e) => { done = true; err = e; });

			// Delivered synchronously (no manager to defer through), so it is already done here.
			Assert.IsTrue(done, "Callback should fire for an unregistered entity");
			Assert.IsNotNull(err, "Unregistered entity exclusive update should error");
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, err.ErrorCode);
			await Task.CompletedTask;
		}

		// ═══════════════════════════════════════════════════════════
		// 9. Delete-On-Disconnect
		// ═══════════════════════════════════════════════════════════

		[Test, Category("DeleteOnDisconnect")]
		public async Task DeleteOnDisconnect_Object_RemovedOnCreatorDisconnect_ControlSurvives()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// B (LocalGame) owns the channel so it survives A's disconnect.
			var bChannel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("disco", new IntegrationTestChannel()), AllConnections());

			// A (RemoteGame) subscribes, then creates one ephemeral object and one normal control object.
			var aChannel = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("disco", null), AllConnections());

			var ephemeral = new IntegrationTestEntity { DeleteOnDisconnect = true };
			ephemeral.Health.Set(11);
			await Pump(RemoteGame.EntityManager.CreateObjectAsync(ephemeral, aChannel, false), AllConnections());

			var survivor = new IntegrationTestEntity(); // no flag
			survivor.Health.Set(22);
			await Pump(RemoteGame.EntityManager.CreateObjectAsync(survivor, aChannel, false), AllConnections());

			// B replicates both objects.
			await PumpUntil(() => bChannel.DistributedObjects.Count == 2, TimeSpan.FromSeconds(3), AllConnections());

			IntegrationTestEntity bEphemeral = null, bSurvivor = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				var e = obj as IntegrationTestEntity;
				if (e != null && e.Health.Get() == 11) bEphemeral = e;
				else if (e != null && e.Health.Get() == 22) bSurvivor = e;
			}
			Assert.IsNotNull(bEphemeral, "B did not replicate the ephemeral object");
			Assert.IsNotNull(bSurvivor, "B did not replicate the control object");

			IDistributedObject removed = null;
			bChannel.OnObjectRemovedEvent += o => removed = o;

			// A drops its connection — the server must delete only the DeleteOnDisconnect object.
			DisposeConnection(RemoteGame);

			await PumpUntil(() => bEphemeral.WasDeleted, TimeSpan.FromSeconds(5), LocalGame);

			Assert.IsTrue(bEphemeral.WasDeleted, "Ephemeral object was not deleted when its creator disconnected");
			Assert.AreSame(bEphemeral, removed, "Channel OnObjectRemoved did not fire for the ephemeral object");
			Assert.IsFalse(bChannel.DistributedObjects.ContainsKey(bEphemeral.DistributedEntityId),
				"Ephemeral object was not removed from the channel");

			Assert.IsFalse(bSurvivor.WasDeleted, "Control object was wrongly deleted on disconnect");
			Assert.IsTrue(bChannel.DistributedObjects.ContainsKey(bSurvivor.DistributedEntityId),
				"Control object did not survive the creator's disconnect");
			Assert.AreEqual(1, bChannel.DistributedObjects.Count, "Only the ephemeral object should have been removed");
		}

		[Test, Category("DeleteOnDisconnect")]
		public async Task DeleteOnDisconnect_Channel_RemovedAndNameFreedOnCreatorDisconnect()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			// A (RemoteGame) creates an ephemeral channel. CreateChannel does not subscribe the creator,
			// but A still owns it for the lifetime of its connection.
			var created = await Pump(RemoteGame.EntityManager.CreateChannelAsync(
				"ghost", new IntegrationTestChannel { DeleteOnDisconnect = true }, false, null), AllConnections());
			Assert.IsTrue(created, "Ephemeral channel was not created");

			// B (LocalGame) subscribes so it can observe the deletion.
			var bGhost = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("ghost", null), AllConnections());
			Assert.IsNotNull(bGhost, "B failed to subscribe to the ephemeral channel");

			bool ghostDeleted = false;
			bGhost.OnDeletedEvent += _ => ghostDeleted = true;

			// A drops its connection — the server destroys the channel and frees its name.
			DisposeConnection(RemoteGame);

			await PumpUntil(() => ghostDeleted, TimeSpan.FromSeconds(5), LocalGame);
			Assert.IsTrue(ghostDeleted, "Ephemeral channel was not deleted when its creator disconnected");
			Assert.AreEqual(1, bGhost.UndistributedCount, "Channel OnUndistributed not fired exactly once");

			// The name must be free again: re-creating a channel with the same name must succeed. Before the
			// disconnect-cleanup unregister fix the name lingered in NamedEntities and this threw ActionUniqueNameExists.
			var recreated = await Pump(LocalGame.EntityManager.CreateChannelAsync(
				"ghost", new IntegrationTestChannel(), false, null), LocalGame);
			Assert.IsTrue(recreated, "Channel name was not freed after the creator disconnected");
		}
	}
}
