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
	public enum Props : byte { HEALTH = 1, NAME = 2 }

	[Distributed((byte)Props.HEALTH)]
	public DistributedValue<int, Int32Serializer> Health;

	[Distributed((byte)Props.NAME)]
	public DistributedValue<string, StringSerializer> DisplayName;

	public bool WasDeleted;

	public IntegrationTestEntity()
	{
		InitializeDistributedFields();
	}

	public override void OnDeleted(BsonValue deleteData)
	{
		WasDeleted = true;
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

	public IntegrationTestChannel()
	{
		InitializeDistributedFields();
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

		// C1 deletes entity
		entity.Delete("goodbye", null);

		// Wait for C2 to see deletion
		yield return TickUntil(() => c2Entity.WasDeleted, 3f, AllConnections());
		Assert.IsTrue(c2Entity.WasDeleted);
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
}
