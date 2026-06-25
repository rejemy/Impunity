using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;
using Impunity.Unity;

using UltraLiteDB;


// ───────── Test Entity Types ─────────

[DistributedEntity(IntegrationTestTypes.ENTITY)]
public partial class IntegrationTestEntity : DistributedObjectBase
{
	public enum Props : byte { HEALTH = 1, NAME = 2, ACTION = 3 }

	[Distributed((byte)Props.HEALTH)]
	public DistributedValue<int, Int32Serializer> Health;

	[Distributed((byte)Props.NAME)]
	public DistributedValue<string, StringSerializer> DisplayName;

	[Distributed((byte)Props.ACTION)]
	public DistributedTemporalValue<int, Int32Serializer> Action;

	public bool WasDeleted;
	public int UndistributedCount;

	public override void OnDeleted(BsonValue deleteData)
	{
		WasDeleted = true;
	}

	public override void OnUndistributed()
	{
		UndistributedCount++;
	}
}

[DistributedEntity(IntegrationTestTypes.CHANNEL)]
public partial class IntegrationTestChannel : DistributedChannelBase
{
	public enum Props : byte { STATUS = 1, GRID = 2, CHAT = 3, FLAGS = 4 }

	[Distributed((byte)Props.STATUS)]
	public DistributedValue<string, StringSerializer> Status;

	[Distributed((byte)Props.GRID)]
	public DistributedArray<int, Int32Serializer> Grid;

	[Distributed((byte)Props.CHAT)]
	public DistributedQueue<string, StringSerializer> Chat;

	[Distributed((byte)Props.FLAGS)]
	public DistributedIntDictionary<string, StringSerializer> Flags;

	public int UndistributedCount;

	public override void OnUndistributed()
	{
		UndistributedCount++;
	}
}

static class IntegrationTestTypes
{
	public const int ENTITY = 1;
	public const int CHANNEL = 2;
}

static class IntegrationTestCollections
{
	public const int ITEMS = 10;
}


// ───────── Integration Tests ─────────

public class ImpunityIntegrationTests
{
	string GameStatePath;
	ImpunityOptions Options;
	GameStateFormat Format;
	GameStateEntityTypeDef[] EntityDefs;

	GameStateServer GameServer;
	ImpunityServer TCPServer;
	LocalGameConnection LocalGame;
	RemoteGameConnection RemoteGame;

	[SetUp]
	public void SetUp()
	{
		BsonMapper.Global.IncludeFields = true;

		GameStatePath = Path.Combine(Application.temporaryCachePath, "ImpunityIntegrationTest_" + Guid.NewGuid().ToString("N"));

		Options = new ImpunityOptions
		{
			GameTypeCode = "Test",
			ServerPort = 39654 // Use a non-default port to avoid conflicts
		};

		Format = new GameStateFormat(
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

		var entity = new ClientEntityManager();
		EntityDefs = entity.RegisterEntityTypes(Format.EntityTypes);
	}

	[TearDown]
	public void TearDown()
	{
		LocalGame?.Dispose();
		LocalGame = null;

		RemoteGame?.Dispose();
		RemoteGame = null;

		TCPServer?.Dispose();
		TCPServer = null;

		GameServer?.Dispose();
		GameServer = null;

		try
		{
			if (Directory.Exists(GameStatePath))
			{
				Directory.Delete(GameStatePath, true);
			}
		}
		catch { }
	}

	// ───────── Helpers ─────────

	void CreateServer()
	{
		var summary = new BsonDocument { ["name"] = "IntegrationTest" };
		GameServer = GameStateServer.Create("test", null, GameStatePath, summary, Options);
		GameServer.UpdateFormat(new GameStateFormatData(Format, EntityDefs), false);
	}

	IEnumerator ConnectLocal()
	{
		LocalGame = new LocalGameConnection(GameServer, Format);
		yield return WaitForYield(LocalGame.ConnectYield(), LocalGame);
		Assert.IsTrue(LocalGame.Connected, "Local connection failed");
	}

	IEnumerator StartTCPAndConnectRemote()
	{
		TCPServer = new ImpunityServer(GameServer, Options);
		TCPServer.Start();

		yield return new WaitForSeconds(0.1f);

		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(
			TCPServer.TCPEndpoint, "test", null, Format, Options);
		RemoteGame.OnNetworkError = (err) => Debug.LogError("Network error: " + err.Message);

		yield return WaitForYield(RemoteGame.ConnectYield(), RemoteGame);
		Assert.IsTrue(RemoteGame.Connected, "Remote connection failed");
	}

	/// <summary>Ticks connection(s) each frame until the yield completes or timeout.</summary>
	IEnumerator WaitForYield(ImpunityYield yld, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			foreach (var c in connections) c.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Operation timed out");
		Assert.IsNull(yld.Error, "Operation failed: " + yld.Error?.Message);
	}

	/// <summary>Ticks connection(s) each frame until the yield completes or timeout. Returns value.</summary>
	IEnumerator WaitForYield<T>(ImpunityYield<T> yld, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			foreach (var c in connections) c.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Operation timed out");
		Assert.IsNull(yld.Error, "Operation failed: " + yld.Error?.Message);
	}

	/// <summary>Ticks connections until condition is met or timeout.</summary>
	IEnumerator TickUntil(Func<bool> condition, float timeout, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (!condition() && elapsed < timeout)
		{
			foreach (var c in connections) c.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsTrue(condition(), "Condition not met within timeout");
	}

	BaseGameConnection[] AllConnections()
	{
		var list = new List<BaseGameConnection>();
		if (LocalGame != null) list.Add(LocalGame);
		if (RemoteGame != null) list.Add(RemoteGame);
		return list.ToArray();
	}

	// ═══════════════════════════════════════════════════════════
	// 1. Local Connection — Database CRUD
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("LocalConnection")]
	public IEnumerator LocalConnect()
	{
		CreateServer();
		yield return ConnectLocal();
		Assert.IsTrue(LocalGame.Connected);
	}

	[UnityTest, Category("LocalConnection")]
	public IEnumerator LocalInsertAndFind()
	{
		CreateServer();
		yield return ConnectLocal();

		var doc = new BsonDocument { ["_id"] = "item1", ["name"] = "Sword", ["power"] = 42 };
		var insertYield = LocalGame.InsertDocumentYield(IntegrationTestCollections.ITEMS, doc);
		yield return WaitForYield(insertYield, LocalGame);
		Assert.IsNotNull(insertYield.Value);

		var findYield = LocalGame.FindDocumentByIdYield(IntegrationTestCollections.ITEMS, "item1");
		yield return WaitForYield(findYield, LocalGame);
		Assert.IsNotNull(findYield.Value);
		Assert.AreEqual("Sword", (string)findYield.Value["name"]);
		Assert.AreEqual(42, (int)findYield.Value["power"]);
	}

	[UnityTest, Category("LocalConnection")]
	public IEnumerator LocalUpsertAndList()
	{
		CreateServer();
		yield return ConnectLocal();

		var doc1 = new BsonDocument { ["_id"] = "a", ["val"] = 1 };
		var doc2 = new BsonDocument { ["_id"] = "b", ["val"] = 2 };

		var u1 = LocalGame.UpsertDocumentYield(IntegrationTestCollections.ITEMS, doc1);
		yield return WaitForYield(u1, LocalGame);
		var u2 = LocalGame.UpsertDocumentYield(IntegrationTestCollections.ITEMS, doc2);
		yield return WaitForYield(u2, LocalGame);

		var listYield = LocalGame.ListDocumentsYield(IntegrationTestCollections.ITEMS);
		yield return WaitForYield(listYield, LocalGame);
		Assert.AreEqual(2, listYield.Value.Count);
	}

	[UnityTest, Category("LocalConnection")]
	public IEnumerator LocalDelete()
	{
		CreateServer();
		yield return ConnectLocal();

		var doc = new BsonDocument { ["_id"] = "del1", ["x"] = 1 };
		var insertYield = LocalGame.InsertDocumentYield(IntegrationTestCollections.ITEMS, doc);
		yield return WaitForYield(insertYield, LocalGame);

		var delYield = LocalGame.DeleteDocumentYield(IntegrationTestCollections.ITEMS, "del1");
		yield return WaitForYield(delYield, LocalGame);
		Assert.IsTrue(delYield.Value);

		var findYield = LocalGame.FindDocumentByIdYield(IntegrationTestCollections.ITEMS, "del1");
		yield return WaitForYield(findYield, LocalGame);
		Assert.IsNull(findYield.Value);
	}

	[UnityTest, Category("LocalConnection")]
	public IEnumerator LocalCompoundAction()
	{
		CreateServer();
		yield return ConnectLocal();

		var doc1 = new BsonDocument { ["_id"] = "c1", ["v"] = 10 };
		var doc2 = new BsonDocument { ["_id"] = "c2", ["v"] = 20 };

		var compoundYield = LocalGame.CompoundDatabaseActionYield(new GameStateActionBase[]
		{
			new UpsertDocumentAction(IntegrationTestCollections.ITEMS, doc1),
			new UpsertDocumentAction(IntegrationTestCollections.ITEMS, doc2),
			new ListDocumentsAction(IntegrationTestCollections.ITEMS),
		});
		yield return WaitForYield(compoundYield, LocalGame);
		Assert.AreEqual(3, compoundYield.Value.Count);
	}

	// ═══════════════════════════════════════════════════════════
	// 2. TCP Connection
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("TCPConnection")]
	public IEnumerator TCPConnect()
	{
		CreateServer();
		yield return StartTCPAndConnectRemote();
		Assert.IsTrue(RemoteGame.Connected);
	}

	[UnityTest, Category("TCPConnection")]
	public IEnumerator TCPDatabaseOps()
	{
		CreateServer();
		yield return StartTCPAndConnectRemote();

		var doc = new BsonDocument { ["_id"] = "tcp1", ["name"] = "Shield" };
		var insertYield = RemoteGame.InsertDocumentYield(IntegrationTestCollections.ITEMS, doc);
		yield return WaitForYield(insertYield, RemoteGame);

		var findYield = RemoteGame.FindDocumentByIdYield(IntegrationTestCollections.ITEMS, "tcp1");
		yield return WaitForYield(findYield, RemoteGame);
		Assert.AreEqual("Shield", (string)findYield.Value["name"]);
	}

	[UnityTest, Category("TCPConnection")]
	public IEnumerator TCPMergeInsertCreatesMissingDocument()
	{
		CreateServer();
		yield return StartTCPAndConnectRemote();

		// Merge-insert against a non-existent _id must INSERT it. Over a remote connection this used to silently
		// degrade to a plain merge-into (wrong action-type id), leaving nothing stored.
		var doc = new BsonDocument { ["_id"] = "mi1", ["name"] = "Potion" };
		var mergeYield = RemoteGame.MergeInsertDocumentYield(IntegrationTestCollections.ITEMS, doc);
		yield return WaitForYield(mergeYield, RemoteGame);

		var findYield = RemoteGame.FindDocumentByIdYield(IntegrationTestCollections.ITEMS, "mi1");
		yield return WaitForYield(findYield, RemoteGame);
		Assert.IsNotNull(findYield.Value, "MergeInsert should have inserted the missing document over a remote connection");
		Assert.AreEqual("Potion", (string)findYield.Value["name"]);

		// A second merge-insert into the same _id must MERGE the new field while preserving the original.
		var patch = new BsonDocument { ["_id"] = "mi1", ["power"] = 7 };
		var mergeYield2 = RemoteGame.MergeInsertDocumentYield(IntegrationTestCollections.ITEMS, patch);
		yield return WaitForYield(mergeYield2, RemoteGame);

		var findYield2 = RemoteGame.FindDocumentByIdYield(IntegrationTestCollections.ITEMS, "mi1");
		yield return WaitForYield(findYield2, RemoteGame);
		Assert.AreEqual("Potion", (string)findYield2.Value["name"], "Original field should survive the merge");
		Assert.AreEqual(7, (int)findYield2.Value["power"], "New field should be merged in");
	}

	[UnityTest, Category("TCPConnection")]
	public IEnumerator TCPConnectBadPassword()
	{
		// Create server WITH password
		var summary = new BsonDocument { ["name"] = "PasswordTest" };
		GameServer = GameStateServer.Create("test", "secret123", GameStatePath, summary, Options);

		TCPServer = new ImpunityServer(GameServer, Options);
		TCPServer.Start();

		yield return new WaitForSeconds(0.1f);

		// Connect with WRONG password
		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(
			TCPServer.TCPEndpoint, "test", "wrongpassword", Format, Options);

		var connectYield = RemoteGame.ConnectYield();
		float elapsed = 0f;
		while (connectYield.keepWaiting && elapsed < 5f)
		{
			RemoteGame.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}

		Assert.IsNotNull(connectYield.Error, "Expected connection to fail with bad password");
	}

	// ═══════════════════════════════════════════════════════════
	// 3. Live Channels and Entities
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("LiveState")]
	public IEnumerator SubscribeToChannel()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// C1 creates channel
		var c1Channel = new IntegrationTestChannel();
		c1Channel.Status.Set("active");
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("lobby", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());
		Assert.IsNotNull(subYield1.Value);

		// C2 subscribes
		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("lobby", null);
		yield return WaitForYield(subYield2, AllConnections());

		var c2Channel = subYield2.Value;
		Assert.IsNotNull(c2Channel);
		Assert.AreEqual("active", c2Channel.Status.Get());
	}

	[UnityTest, Category("LiveState")]
	public IEnumerator CreateAndReplicateEntity()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// C1 creates channel
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("room", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		// C2 subscribes
		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("room", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		// C1 creates entity
		var entity = new IntegrationTestEntity();
		entity.Health.Set(100);
		entity.DisplayName.Set("Hero");
		var createYield = LocalGame.EntityManager.CreateObjectYield(entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		// Wait for C2 to receive it
		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		Assert.AreEqual(1, c2Channel.DistributedObjects.Count);

		// Find the replicated entity on C2
		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}

		Assert.IsNotNull(c2Entity);
		Assert.AreEqual(100, c2Entity.Health.Get());
		Assert.AreEqual("Hero", c2Entity.DisplayName.Get());
	}

	[UnityTest, Category("LiveState")]
	public IEnumerator DistributedValueReplication()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// Setup channel + entity on C1
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("sync", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		var entity = new IntegrationTestEntity();
		entity.Health.Set(50);
		var createYield = LocalGame.EntityManager.CreateObjectYield(entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		// C2 subscribes
		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("sync", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(c2Entity);
		Assert.AreEqual(50, c2Entity.Health.Get());

		// C1 changes value
		int newHealthReceived = -1;
		c2Entity.Health.OnChanged += (oldVal, newVal) => { newHealthReceived = newVal; };

		entity.Health.Set(75);

		// Tick until C2 sees the change
		yield return TickUntil(() => newHealthReceived == 75, 3f, AllConnections());
		Assert.AreEqual(75, c2Entity.Health.Get());
	}

	[UnityTest, Category("LiveState")]
	public IEnumerator EntityDeletion()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// Setup
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("del", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		var entity = new IntegrationTestEntity();
		entity.Health.Set(1);
		var createYield = LocalGame.EntityManager.CreateObjectYield(entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("del", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		// Track deletion on C2
		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}

		// The channel must be notified when the object is removed. This relies on the manager
		// populating IDistributedObject.Channel at create time and reaching it on delete.
		IDistributedObject removedObj = null;
		c2Channel.OnObjectRemovedEvent += o => removedObj = o;

		// C1 deletes entity
		entity.Delete("goodbye", null);

		// Wait for C2 to see deletion
		yield return TickUntil(() => c2Entity.WasDeleted, 3f, AllConnections());
		Assert.IsTrue(c2Entity.WasDeleted, "OnDeleted did not fire on the deleted object");
		Assert.AreEqual(1, c2Entity.UndistributedCount, "OnUndistributed not fired exactly once on the deleted object");
		Assert.AreSame(c2Entity, removedObj, "Channel OnObjectRemoved did not fire for the deleted object");
		Assert.IsFalse(c2Channel.DistributedObjects.ContainsKey(c2Entity.DistributedEntityId),
			"Deleted object was not removed from the channel's DistributedObjects");
	}

	// ═══════════════════════════════════════════════════════════
	// 4. Broadcasts
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Broadcasts")]
	public IEnumerator BroadcastMessage()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		int receivedType = -1;
		string receivedBody = null;

		LocalGame.OnBroadcastMessage = (type, body, sender) =>
		{
			receivedType = type;
			receivedBody = body.AsString;
		};

		RemoteGame.SendBroadcastMessage(42, "Hello from remote!");

		yield return TickUntil(() => receivedType == 42, 3f, AllConnections());
		Assert.AreEqual(42, receivedType);
		Assert.AreEqual("Hello from remote!", receivedBody);
	}

	// ═══════════════════════════════════════════════════════════
	// 5. Named Locks
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Locks")]
	public IEnumerator NamedLock_TryLock()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// C1 locks
		var lockYield1 = LocalGame.TryToLockYield("myLock");
		yield return WaitForYield(lockYield1, AllConnections());
		Assert.IsTrue(lockYield1.Value, "C1 should acquire lock");

		// C2 tries to lock — should fail
		var lockYield2 = RemoteGame.TryToLockYield("myLock");
		yield return WaitForYield(lockYield2, AllConnections());
		Assert.IsFalse(lockYield2.Value, "C2 should fail to acquire held lock");

		// C1 unlocks
		var unlockYield = LocalGame.UnlockYield("myLock");
		yield return WaitForYield(unlockYield, AllConnections());
		Assert.IsTrue(unlockYield.Value);

		// C2 can now lock
		var lockYield3 = RemoteGame.TryToLockYield("myLock");
		yield return WaitForYield(lockYield3, AllConnections());
		Assert.IsTrue(lockYield3.Value, "C2 should acquire lock after C1 released");

		// Cleanup
		var unlockYield2 = RemoteGame.UnlockYield("myLock");
		yield return WaitForYield(unlockYield2, AllConnections());
	}

	// ═══════════════════════════════════════════════════════════
	// 6. Distributed Collection Types
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Collections")]
	public IEnumerator DistributedArray_Replication()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// C1 creates channel with array
		var c1Channel = new IntegrationTestChannel();
		c1Channel.Grid.Replace(new int[] { 10, 20, 30 });
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("arr", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		// C2 subscribes
		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("arr", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		Assert.AreEqual(10, c2Channel.Grid.Get(0));
		Assert.AreEqual(20, c2Channel.Grid.Get(1));
		Assert.AreEqual(30, c2Channel.Grid.Get(2));

		// C1 updates element
		int changedIndex = -1;
		c2Channel.Grid.OnChanged += (idx, oldVal, newVal) => { changedIndex = idx; };

		subYield1.Value.Grid.Set(1, 99);

		yield return TickUntil(() => changedIndex == 1, 3f, AllConnections());
		Assert.AreEqual(99, c2Channel.Grid.Get(1));
	}

	[UnityTest, Category("Collections")]
	public IEnumerator DistributedQueue_Replication()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		var c1ChannelInit = new IntegrationTestChannel();
		c1ChannelInit.Chat.Init(100);
		c1ChannelInit.Chat.Add("hello");
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("queue", c1ChannelInit);
		yield return WaitForYield(subYield1, AllConnections());
		var c1Channel = subYield1.Value;

		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("queue", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		string receivedMsg = null;
		c2Channel.Chat.OnChanged += (msg) => { receivedMsg = msg; };

		// C1 adds to queue
		c1Channel.Chat.Add("world");

		yield return TickUntil(() => receivedMsg == "world", 3f, AllConnections());
		Assert.AreEqual("world", receivedMsg);
	}

	[UnityTest, Category("Collections")]
	public IEnumerator DistributedDictionary_Replication()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		var c1ChannelInit = new IntegrationTestChannel();
		c1ChannelInit.Flags.Init();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("dict", c1ChannelInit);
		yield return WaitForYield(subYield1, AllConnections());
		var c1Channel = subYield1.Value;

		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("dict", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		int changedKey = -1;
		string changedVal = null;
		c2Channel.Flags.OnChanged += (key, oldVal, newVal) => { changedKey = key; changedVal = newVal; };

		// C1 adds a key
		c1Channel.Flags.Add(7, "active");

		yield return TickUntil(() => changedKey == 7, 3f, AllConnections());
		Assert.AreEqual("active", changedVal);
		Assert.AreEqual("active", c2Channel.Flags.Get(7));
	}

	// ═══════════════════════════════════════════════════════════
	// 7. Unsubscribe
	// ═══════════════════════════════════════════════════════════

	/// <summary>Helper: subscribes C1 (creating the channel) and C2, creates one entity on C1,
	/// and waits until C2 has replicated it. Returns C2's channel + entity via out params.</summary>
	IEnumerator SetupTwoClientChannel(string channelName, System.Action<IntegrationTestChannel, IntegrationTestEntity> onReady)
	{
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield(channelName, c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		var entity = new IntegrationTestEntity();
		entity.Health.Set(100);
		var createYield = LocalGame.EntityManager.CreateObjectYield(entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>(channelName, null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(c2Entity, "C2 did not replicate the entity");

		onReady(c2Channel, c2Entity);
	}

	[UnityTest, Category("Unsubscribe")]
	public IEnumerator Unsubscribe_Deferred_TearsDownWithCallbacksOnAck()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		IntegrationTestChannel c2Channel = null;
		IntegrationTestEntity c2Entity = null;
		yield return SetupTwoClientChannel("deferred", (ch, e) => { c2Channel = ch; c2Entity = e; });

		// Default (immediate == false): objects stay live until the server acks the unsubscribe.
		var unsubYield = RemoteGame.EntityManager.UnsubscribeFromChannelYield(c2Channel, immediate: false);

		// Before the ack returns, nothing has been torn down yet.
		Assert.AreEqual(0, c2Channel.UndistributedCount, "Channel torn down too early");
		Assert.AreEqual(0, c2Entity.UndistributedCount, "Entity torn down too early");
		Assert.AreEqual(1, c2Channel.DistributedObjects.Count, "Objects removed too early");

		yield return WaitForYield(unsubYield, AllConnections());

		// On completion: OnUndistributed fired exactly once on the entity and the channel,
		// and all references are released from the manager.
		Assert.AreEqual(1, c2Entity.UndistributedCount, "Entity OnUndistributed not fired exactly once");
		Assert.AreEqual(1, c2Channel.UndistributedCount, "Channel OnUndistributed not fired exactly once");
		Assert.AreEqual(0, c2Channel.DistributedObjects.Count, "Channel child objects not cleared");

		// Re-subscribing returns a fresh channel instance (proves the old one was fully released).
		var resubYield = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("deferred", null);
		yield return WaitForYield(resubYield, AllConnections());
		Assert.AreNotSame(c2Channel, resubYield.Value, "Re-subscribe returned the stale channel");
	}

	[UnityTest, Category("Unsubscribe")]
	public IEnumerator Unsubscribe_Immediate_SuppressesInFlightAndTearsDownSynchronously()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// C1-side handles for generating an in-flight burst.
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("immediate", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		var c1Entity = new IntegrationTestEntity();
		c1Entity.Health.Set(100);
		var createYield = LocalGame.EntityManager.CreateObjectYield(c1Entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("immediate", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;
		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(c2Entity);

		// Watch for any suppressed traffic leaking through to C2.
		bool healthChangedOnC2 = false;
		c2Entity.Health.OnChanged += (oldVal, newVal) => { healthChangedOnC2 = true; };
		bool newObjectSeenOnC2 = false;
		RemoteGame.EntityManager.OnDistributedObjectCreated = (obj, ch, created) => { newObjectSeenOnC2 = true; };

		// Immediate unsubscribe: synchronous teardown, no lifecycle callbacks.
		var unsubYield = RemoteGame.EntityManager.UnsubscribeFromChannelYield(c2Channel, immediate: true);
		Assert.AreEqual(0, c2Channel.UndistributedCount, "Immediate mode must not invoke OnUndistributed");
		Assert.AreEqual(0, c2Entity.UndistributedCount, "Immediate mode must not invoke OnUndistributed");
		Assert.AreEqual(0, c2Channel.DistributedObjects.Count, "Channel not torn down synchronously");

		// Now generate an in-flight burst from C1 (update + brand-new object) that races the unsubscribe.
		c1Entity.Health.Set(999);
		var burstEntity = new IntegrationTestEntity();
		burstEntity.Health.Set(7);
		LocalGame.EntityManager.CreateObject(burstEntity, subYield1.Value, false, null);

		yield return WaitForYield(unsubYield, AllConnections());
		// Drain a few extra frames to make sure nothing arrives late.
		float drain = 0f;
		while (drain < 0.5f)
		{
			foreach (var c in AllConnections()) c.Update();
			yield return null;
			drain += Time.deltaTime;
		}

		Assert.IsFalse(healthChangedOnC2, "Suppressed update leaked to C2 after immediate unsubscribe");
		Assert.IsFalse(newObjectSeenOnC2, "Suppressed object-create leaked to C2 after immediate unsubscribe");
	}

	[UnityTest, Category("Unsubscribe")]
	public IEnumerator Unsubscribe_Immediate_AllowsResubscribe()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		IntegrationTestChannel c2Channel = null;
		IntegrationTestEntity c2Entity = null;
		yield return SetupTwoClientChannel("resub", (ch, e) => { c2Channel = ch; c2Entity = e; });

		var unsubYield = RemoteGame.EntityManager.UnsubscribeFromChannelYield(c2Channel, immediate: true);
		yield return WaitForYield(unsubYield, AllConnections());

		// Re-subscribe to the same channel: suppression must be fully lifted and fresh state replicated.
		var resubYield = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("resub", null);
		yield return WaitForYield(resubYield, AllConnections());
		var c2Channel2 = resubYield.Value;
		Assert.IsNotNull(c2Channel2);
		Assert.AreNotSame(c2Channel, c2Channel2, "Re-subscribe returned the stale channel");

		yield return TickUntil(() => c2Channel2.DistributedObjects.Count > 0, 3f, AllConnections());
		Assert.AreEqual(1, c2Channel2.DistributedObjects.Count, "Entity did not replicate after re-subscribe");

		IntegrationTestEntity reEntity = null;
		foreach (var obj in c2Channel2.DistributedObjects.Values)
		{
			reEntity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(reEntity);
		Assert.AreEqual(100, reEntity.Health.Get(), "Re-subscribed entity has wrong state");
	}

	// ═══════════════════════════════════════════════════════════
	// 8. Temporal Cooldown Locks
	// ═══════════════════════════════════════════════════════════

	/// <summary>Helper: like SetupTwoClientChannel but also returns C1's entity instance so both
	/// sides of the replication can be asserted on.</summary>
	IEnumerator SetupTwoClientEntity(string channelName, System.Action<IntegrationTestEntity, IntegrationTestEntity> onReady)
	{
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield(channelName, c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		var c1Entity = new IntegrationTestEntity();
		c1Entity.Health.Set(100);
		var createYield = LocalGame.EntityManager.CreateObjectYield(c1Entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>(channelName, null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;

		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(c2Entity, "C2 did not replicate the entity");

		onReady(c1Entity, c2Entity);
	}

	[UnityTest, Category("CooldownLock")]
	public IEnumerator CooldownLock_FirstUpdateWinsAndDropsWholeUpdate()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		IntegrationTestEntity c1Entity = null, c2Entity = null;
		yield return SetupTwoClientEntity("cooldown1", (e1, e2) => { c1Entity = e1; c2Entity = e2; });

		// Establish a baseline Health via a normal update so BOTH sides hold it reliably.
		// (These entities are not client-authoritative: local values are only populated by
		// the server's relayed echo, so initial create-time values are never echoed to the creator.)
		c1Entity.Health.Set(50);
		yield return TickUntil(() => c1Entity.Health.Get() == 50 && c2Entity.Health.Get() == 50, 3f, AllConnections());

		// C1 wins the property with a cooldown lock
		Assert.IsTrue(c1Entity.Action.Set(5, TimeSpan.FromSeconds(1.5)));
		yield return TickUntil(() => c1Entity.Action.Get() == 5 && c2Entity.Action.Get() == 5, 3f, AllConnections());

		Assert.IsTrue(c2Entity.Action.IsCooldownLocked, "C2 should see the cooldown lock");
		Assert.Greater(c2Entity.Action.CooldownRemaining.TotalMilliseconds, 0.0);

		// Local pre-check refuses while locked
		Assert.IsFalse(c2Entity.Action.Set(99), "Set should fail locally during cooldown");

		// Watch the non-sender (C1) for any leaked changes
		bool c1ActionChanged = false, c1HealthChanged = false;
		c1Entity.Action.OnChanged += (o, n) => c1ActionChanged = true;
		c1Entity.Health.OnChanged += (o, n) => c1HealthChanged = true;

		// A forced attempt bypasses the local check, but the server drops the whole update —
		// including the non-temporal Health change batched into the same message
		Assert.IsTrue(c2Entity.Action.Set(99, force: true));
		c2Entity.Health.Set(1);

		float drain = 0f;
		while (drain < 0.7f)
		{
			foreach (var c in AllConnections()) c.Update();
			yield return null;
			drain += Time.deltaTime;
		}

		Assert.IsFalse(c1ActionChanged, "Locked temporal update leaked through to C1");
		Assert.IsFalse(c1HealthChanged, "Batched non-temporal update was not dropped with the locked temporal field");
		Assert.AreEqual(5, c1Entity.Action.Get(), "C1 action value changed despite the lock");
		Assert.AreEqual(50, c1Entity.Health.Get(), "C1 health value changed despite the dropped update");
	}

	[UnityTest, Category("CooldownLock")]
	public IEnumerator CooldownLock_ExpiryAllowsUpdates()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		IntegrationTestEntity c1Entity = null, c2Entity = null;
		yield return SetupTwoClientEntity("cooldown2", (e1, e2) => { c1Entity = e1; c2Entity = e2; });

		Assert.IsTrue(c1Entity.Action.Set(5, TimeSpan.FromSeconds(1)));
		yield return TickUntil(() => c2Entity.Action.Get() == 5, 3f, AllConnections());
		Assert.IsTrue(c2Entity.Action.IsCooldownLocked);

		// Wait out the cooldown
		yield return TickUntil(() => !c2Entity.Action.IsCooldownLocked, 3f, AllConnections());

		// A plain update (no lock requested) now succeeds and replicates everywhere
		Assert.IsTrue(c2Entity.Action.Set(42));
		yield return TickUntil(() => c1Entity.Action.Get() == 42 && c2Entity.Action.Get() == 42, 3f, AllConnections());
		Assert.IsFalse(c1Entity.Action.IsCooldownLocked, "Plain update must not start a new cooldown");
	}

	[UnityTest, Category("CooldownLock")]
	public IEnumerator CooldownLock_LateJoinerReceivesRemainingCooldown()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// C1 creates the channel + entity and locks the action before C2 subscribes
		var c1Channel = new IntegrationTestChannel();
		var subYield1 = LocalGame.EntityManager.SubscribeToChannelYield("cooldown3", c1Channel);
		yield return WaitForYield(subYield1, AllConnections());

		var c1Entity = new IntegrationTestEntity();
		c1Entity.Health.Set(100);
		var createYield = LocalGame.EntityManager.CreateObjectYield(c1Entity, subYield1.Value, false);
		yield return WaitForYield(createYield, AllConnections());

		Assert.IsTrue(c1Entity.Action.Set(5, TimeSpan.FromSeconds(2)));
		// The echo from the server confirms the update (and its lock) was accepted
		yield return TickUntil(() => c1Entity.Action.Get() == 5, 3f, AllConnections());

		// C2 joins while the lock is active and learns the remaining cooldown from initial state
		var subYield2 = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("cooldown3", null);
		yield return WaitForYield(subYield2, AllConnections());
		var c2Channel = subYield2.Value;
		yield return TickUntil(() => c2Channel.DistributedObjects.Count > 0, 3f, AllConnections());

		IntegrationTestEntity c2Entity = null;
		foreach (var obj in c2Channel.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(c2Entity);

		Assert.AreEqual(5, c2Entity.Action.Get(), "Late joiner did not replicate the temporal value");
		Assert.IsTrue(c2Entity.Action.IsCooldownLocked, "Late joiner should see the active cooldown");
		Assert.LessOrEqual(c2Entity.Action.CooldownRemaining.TotalSeconds, 2.0, "Remaining cooldown should not exceed the original lockout");
		Assert.IsFalse(c2Entity.Action.Set(99), "Late joiner Set should fail during cooldown");

		// And the lock still expires normally
		yield return TickUntil(() => !c2Entity.Action.IsCooldownLocked, 4f, AllConnections());
	}

	// ═══════════════════════════════════════════════════════════
	// 9. Delete-On-Disconnect
	// ═══════════════════════════════════════════════════════════

	/// <summary>Disposes the TCP remote connection and clears the field so TearDown won't double-dispose it
	/// and AllConnections() stops ticking it. Simulates client A dropping its connection.</summary>
	void DisconnectRemote()
	{
		RemoteGame.Dispose();
		RemoteGame = null;
	}

	[UnityTest, Category("DeleteOnDisconnect")]
	public IEnumerator DeleteOnDisconnect_Object_RemovedOnCreatorDisconnect_ControlSurvives()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// B (LocalGame) owns the channel so it survives A's disconnect.
		var bChannel = new IntegrationTestChannel();
		var subB = LocalGame.EntityManager.SubscribeToChannelYield("disco", bChannel);
		yield return WaitForYield(subB, AllConnections());

		// A (RemoteGame) subscribes, then creates one ephemeral object and one normal control object.
		var subA = RemoteGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("disco", null);
		yield return WaitForYield(subA, AllConnections());
		var aChannel = subA.Value;

		var ephemeral = new IntegrationTestEntity { DeleteOnDisconnect = true };
		ephemeral.Health.Set(11);
		var createEph = RemoteGame.EntityManager.CreateObjectYield(ephemeral, aChannel, false);
		yield return WaitForYield(createEph, AllConnections());

		var survivor = new IntegrationTestEntity(); // no flag
		survivor.Health.Set(22);
		var createSurv = RemoteGame.EntityManager.CreateObjectYield(survivor, aChannel, false);
		yield return WaitForYield(createSurv, AllConnections());

		// B replicates both objects.
		yield return TickUntil(() => subB.Value.DistributedObjects.Count == 2, 3f, AllConnections());

		IntegrationTestEntity bEphemeral = null, bSurvivor = null;
		foreach (var obj in subB.Value.DistributedObjects.Values)
		{
			var e = obj as IntegrationTestEntity;
			if (e != null && e.Health.Get() == 11) bEphemeral = e;
			else if (e != null && e.Health.Get() == 22) bSurvivor = e;
		}
		Assert.IsNotNull(bEphemeral, "B did not replicate the ephemeral object");
		Assert.IsNotNull(bSurvivor, "B did not replicate the control object");

		IDistributedObject removed = null;
		subB.Value.OnObjectRemovedEvent += o => removed = o;

		// A drops its connection — the server must delete only the DeleteOnDisconnect object.
		DisconnectRemote();

		yield return TickUntil(() => bEphemeral.WasDeleted, 5f, LocalGame);

		Assert.IsTrue(bEphemeral.WasDeleted, "Ephemeral object was not deleted when its creator disconnected");
		Assert.AreSame(bEphemeral, removed, "Channel OnObjectRemoved did not fire for the ephemeral object");
		Assert.IsFalse(subB.Value.DistributedObjects.ContainsKey(bEphemeral.DistributedEntityId),
			"Ephemeral object was not removed from the channel");

		Assert.IsFalse(bSurvivor.WasDeleted, "Control object was wrongly deleted on disconnect");
		Assert.IsTrue(subB.Value.DistributedObjects.ContainsKey(bSurvivor.DistributedEntityId),
			"Control object did not survive the creator's disconnect");
		Assert.AreEqual(1, subB.Value.DistributedObjects.Count, "Only the ephemeral object should have been removed");
	}

	[UnityTest, Category("DeleteOnDisconnect")]
	public IEnumerator DeleteOnDisconnect_Channel_RemovedAndNameFreedOnCreatorDisconnect()
	{
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		// A (RemoteGame) creates an ephemeral channel. CreateChannel does not subscribe the creator,
		// but A still owns it for the lifetime of its connection.
		var createGhost = RemoteGame.EntityManager.CreateChannelYield(
			"ghost", new IntegrationTestChannel { DeleteOnDisconnect = true }, false, null);
		yield return WaitForYield(createGhost, AllConnections());
		Assert.IsTrue(createGhost.Value, "Ephemeral channel was not created");

		// B (LocalGame) subscribes so it can observe the deletion.
		var subB = LocalGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("ghost", null);
		yield return WaitForYield(subB, AllConnections());
		var bGhost = subB.Value;
		Assert.IsNotNull(bGhost, "B failed to subscribe to the ephemeral channel");

		bool ghostDeleted = false;
		bGhost.OnDeletedEvent += _ => ghostDeleted = true;

		// A drops its connection — the server destroys the channel and frees its name.
		DisconnectRemote();

		yield return TickUntil(() => ghostDeleted, 5f, LocalGame);
		Assert.IsTrue(ghostDeleted, "Ephemeral channel was not deleted when its creator disconnected");
		Assert.AreEqual(1, bGhost.UndistributedCount, "Channel OnUndistributed not fired exactly once");

		// The name must be free again: re-creating a channel with the same name must succeed. Before the
		// disconnect-cleanup unregister fix the name lingered in NamedEntities and this threw ActionUniqueNameExists.
		var recreate = LocalGame.EntityManager.CreateChannelYield(
			"ghost", new IntegrationTestChannel(), false, null);
		yield return WaitForYield(recreate, LocalGame);
		Assert.IsTrue(recreate.Value, "Channel name was not freed after the creator disconnected");
	}
}
