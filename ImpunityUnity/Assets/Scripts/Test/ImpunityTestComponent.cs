using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System;
using System.Threading.Tasks;

using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

using Dreamwing.Cons;

using Impunity;
using Impunity.GameState;
using Impunity.Connection;
using Impunity.Unity;
using Impunity.Networking;

using UltraLiteDB;


public static class TestCollectionTypes
{
	// 0 - 9 are reserved
	public const int CHARACTERS = 10;
}

public static class TestEntityTypes
{
	// 0 is reserved
	public const int PLAYER = 1;
	public const int ZONE = 2;
	public const int PERSISTED_ZONE = 3;
	public const int PERSISTED_ZONE_OBJECT = 4;
}


[DistributedEntity(TestEntityTypes.PLAYER, FactoryMethod = "DistributedEntityFactory")]
public partial class TestPlayer : DistributedEntityBase
{
	enum DistributedPropIds
    {
		TESTBOOL = 1,
		DIRECTION = 2,
		FLAGS = 3,
		QUESTS = 4
	}

	public static IDistributedEntity DistributedEntityFactory() { return new TestPlayer(); }

	[Distributed((int)DistributedPropIds.TESTBOOL, OnChanged = "OnTestBoolChanged")]
	private DistributedValue<DBool> TestBool;
	
	private void OnTestBoolChanged(bool oldValue, bool newValue)
    {
		ImpunityLogger.LogInformation("Got testbool change on TestPlayer, from " + oldValue.ToString() + " to " + newValue.ToString());
	}

	[Distributed((int)DistributedPropIds.DIRECTION, OnChanged = "OnDirectionChanged")]
	private DistributedValue<DVector3> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got direction change on TestPlayer, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.FLAGS, OnChanged = "OnFlagsChanged")]
	private DistributedIntDictionary<DString> Flags;

	private void OnFlagsChanged(int key, string oldFlag, string newFlag)
	{
		ImpunityLogger.LogInformation("Got flags change on TestPlayer, key " + key + " from " + oldFlag + " to " + newFlag);
	}

	[Distributed((int)DistributedPropIds.QUESTS, OnChanged = "OnQuestsChanged")]
	private DistributedStringDictionary<DString> Quests;

	private void OnQuestsChanged(string key, string oldQuest, string newQuest)
	{
		ImpunityLogger.LogInformation("Got quests change on TestPlayer, key " + key + " from " + oldQuest + " to " + newQuest);
	}

	public override void OnEventTriggered(int eventType, BsonValue eventData)
	{
		ImpunityLogger.LogInformation("Got event " + eventType + " on TestPlayer with data " + eventData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public override void OnDeleted(BsonValue deleteData)
	{
		ImpunityLogger.LogInformation("Player deleted: " + deleteData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}


[DistributedEntity(TestEntityTypes.ZONE)]
public partial class TestZone : DistributedChannelBase
{
	enum DistributedPropIds
	{
		STATUS = 1,
		SCALAR = 2,
		GRID = 3,
		CHAT = 4
	}

	[Distributed((int)DistributedPropIds.STATUS)]
	private DistributedValue<DString> Status;
	

	[Distributed((int)DistributedPropIds.SCALAR)]
	private DistributedValue<DFloat> Scalar;

	[Distributed((int)DistributedPropIds.GRID, OnChanged = "OnGridChanged", OnReplaced = "OnGridReplaced")]
	private DistributedArray<DInt32> Grid;

	private void OnGridChanged(int index, int oldValue, int newValue)
	{
		ImpunityLogger.LogInformation("Got grid change on TestZone, index " + index + " from " + oldValue + " to " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnGridReplaced(DInt32[] oldValue, DInt32[] newValue)
	{
		ImpunityLogger.LogInformation("Got grid replaced on TestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.CHAT, OnChanged = "OnChatChanged", OnReplaced = "OnChatReplaced")]
	private DistributedQueue<DString> Chat;

	private void OnChatChanged(string newValue)
	{
		ImpunityLogger.LogInformation("Got chat change on TestZone: " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnChatReplaced(Queue<DString> oldValue, Queue<DString> newValue)
	{
		ImpunityLogger.LogInformation("Got chat replaced on TestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}

[DistributedEntity(TestEntityTypes.PERSISTED_ZONE, PersistAs = "zone")]
public partial class PersistedTestZone : DistributedChannelBase
{
	enum DistributedPropIds
	{
		STATUS = 1,
		SCALAR = 2,
		GRID = 3,
		CHAT = 4
	}

	[Distributed((int)DistributedPropIds.STATUS)]
	private DistributedValue<DString> Status;

	[Distributed((int)DistributedPropIds.SCALAR)]
	private DistributedValue<DFloat> Scalar;

	[Distributed((int)DistributedPropIds.GRID, PersistAs = "grid", OnChanged = "OnGridChanged", OnReplaced = "OnGridReplaced")]
	private DistributedArray<DInt32> Grid;

	private void OnGridChanged(int index, int oldValue, int newValue)
	{
		ImpunityLogger.LogInformation("Got grid change on PersistedTestZone, index " + index + " from " + oldValue + " to " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnGridReplaced(DInt32[] oldValue, DInt32[] newValue)
	{
		ImpunityLogger.LogInformation("Got grid replaced on PersistedTestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.CHAT, OnChanged = "OnChatChanged", OnReplaced = "OnChatReplaced")]
	private DistributedQueue<DString> Chat;

	private void OnChatChanged(string newValue)
	{
		ImpunityLogger.LogInformation("Got chat change on PersistedTestZone: " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnChatReplaced(Queue<DString> oldValue, Queue<DString> newValue)
	{
		ImpunityLogger.LogInformation("Got chat replaced on PersistedTestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}

[DistributedEntity(TestEntityTypes.PERSISTED_ZONE_OBJECT, PersistAs = "zobj")]
public partial class ZonePersistedObject : DistributedEntityBase
{
	enum DistributedPropIds
	{
		POSITION = 1,
		DIRECTION = 2,
		FLAGS = 3,
		QUESTS = 4
	}

	[Distributed((int)DistributedPropIds.POSITION, PersistAs="pos", OnChanged = "OnPositionChanged")]
	private DistributedValue<DVector2Int> Position;

	private void OnPositionChanged(Vector2Int oldValue, Vector2Int newValue)
	{
		ImpunityLogger.LogInformation("Got position change on ZonePersistedObject, from " + oldValue.ToString() + " to " + newValue.ToString());
	}

	[Distributed((int)DistributedPropIds.DIRECTION, OnChanged = "OnDirectionChanged")]
	private DistributedValue<DVector3> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got direction change on ZonePersistedObject, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.FLAGS, PersistAs="flags", OnChanged = "OnFlagsChanged")]
	private DistributedIntDictionary<DString> Flags;

	private void OnFlagsChanged(int key, string oldFlag, string newFlag)
	{
		ImpunityLogger.LogInformation("Got flags change on ZonePersistedObject, key " + key + " from " + oldFlag + " to " + newFlag);
	}

	[Distributed((int)DistributedPropIds.QUESTS, OnChanged = "OnQuestsChanged")]
	private DistributedStringDictionary<DString> Quests;

	private void OnQuestsChanged(string key, string oldQuest, string newQuest)
	{
		ImpunityLogger.LogInformation("Got quests change on ZonePersistedObject, key " + key + " from " + oldQuest + " to " + newQuest);
	}

	public override void OnEventTriggered(int eventType, BsonValue eventData)
	{
		ImpunityLogger.LogInformation("Got event " + eventType + " on ZonePersistedObject with data " + eventData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public override void OnDeleted(BsonValue deleteData)
	{
		ImpunityLogger.LogInformation("ZonePersistedObject deleted: " + deleteData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}

public class ImpunityTestComponent : MonoBehaviour
{
	ImpunityOptions Options;
	GameStateFormat CurrFormat;

	string GameStatePath;

	GameStateServer GameServer;

	LocalGameConnection LocalGame;

	ImpunityServer TCPServer;
	ImpunityTCPServerFinder Finder;
	RemoteGameConnection RemoteGame;

	bool FoundServer;
	IPEndPoint ServerEndpoint;
	string FoundGameId;

	bool TestsDone = false;

	public static int WaitingForCount = 0;

	void Start()
	{
		Cons.Init();
		Cons.Open();

		ImpunityUnityLogger.Setup(ImpunityLogLevel.INFO);

		GameStatePath = Path.Join(Application.persistentDataPath, "ImpTest", "TestGame");

		Options = new ImpunityOptions
		{
			GameTypeCode = "ImpTest",
			LANDiscoverable = true,
			RemoteUpgradeAllowed = true
		};

		CurrFormat = new GameStateFormat
		(
			1,

			new GameStateCollection[]
			{
				new GameStateCollection
				{
					Index = TestCollectionTypes.CHARACTERS,
					Name = "Characters"
				}
			},

			new Type[]
			{
				typeof(TestPlayer),
				typeof(TestZone),
				typeof(PersistedTestZone),
				typeof(ZonePersistedObject)
			}
		);

		StartCoroutine(ComboTest());
	}


	IEnumerator ComboTest()
	{
		yield return Setup();

		yield return LocalConnectionTest();

		yield return Setup();

		yield return TCPConnectionTest();

		yield return Setup();

		AsyncTests().ContinueWith((t)=>
		{
			TestsDone = true;
		});
	}

	IEnumerator Setup()
    {
		Cleanup();

		yield return new WaitForSeconds(0.1f);

		ImpunityLogger.LogInformation("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Create("testgame", null, GameStatePath, summary, Options);
	}

	async Task SetupAsync()
    {
		Cleanup();

		await Task.Delay(100);

		ImpunityLogger.LogInformation("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Create("testgame", null, GameStatePath, summary, Options);
	}

	void Cleanup(bool deleteFolder = true)
    {
		LocalGame?.Dispose();
		LocalGame = null;

		RemoteGame?.Dispose();
		RemoteGame = null;

		TCPServer?.Dispose();
		TCPServer = null;

		GameServer?.Dispose();
		GameServer = null;

		if (deleteFolder)
		{
			DeleteFolder(GameStatePath);
		}
	}

	async Task ResetServerAsync()
	{
		Cleanup(false);

		await Task.Delay(100);

		ImpunityLogger.LogInformation("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Open("testgame", null, GameStatePath, Options);
	}

	IEnumerator LocalConnectionTest()
	{
		ImpunityLogger.LogInformation("Running local connection test");

		LocalGame = new LocalGameConnection(GameServer, CurrFormat);

		yield return GenericConnectionTest(LocalGame);

		LocalGame.Dispose();
		LocalGame = null;

		ImpunityLogger.LogInformation("Done with local connection test");
	}

	IEnumerator TCPConnectionTest()
	{
		ImpunityLogger.LogInformation("Running tcp connection test");

		ImpunityLogger.LogInformation("Creating TCP game server");

		TCPServer = ImpunityServer.MakeTCPServer(GameServer, Options);
		TCPServer.Start();

		yield return new WaitForSeconds(0.1f);

		ImpunityLogger.LogInformation("Looking for server");
		Finder = new ImpunityTCPServerFinder(Options, OnServerFound);
		Finder.Start();

		while (!FoundServer)
        {
			yield return null;
		}
		ImpunityLogger.LogInformation("Found TCP server at " + ServerEndpoint.ToString());

		Finder.Dispose();
		Finder = null;

		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(ServerEndpoint, FoundGameId, null, CurrFormat, Options);
		RemoteGame.OnNetworkError = OnNetworkError;

		yield return GenericConnectionTest(RemoteGame);

		RemoteGame.Dispose();
		RemoteGame = null;

		TCPServer.Dispose();
		TCPServer = null;

		ImpunityLogger.LogInformation("Done with TCP connection test");
	}

	IEnumerator GenericConnectionTest(BaseGameConnection connection)
    {
		connection.OnBroadcastMessage = (messageType, message, sentBy) =>
		{
			ImpunityLogger.LogInformation("Got broadcast message " + message.AsString + " from " + sentBy);
		};

		ImpunityYield connectAction = connection.ConnectYield();
		yield return connectAction;
		if (connectAction.Error != null)
		{
			ImpunityLogger.LogError("Error connecting: " + connectAction.Error.Message + "\n" + connectAction.Error.Stacktrace);
			yield break;
		}

		ImpunityLogger.LogInformation("TCP Connected");

		BsonDocument char1 = new BsonDocument();
		char1["_id"] = "char1";
		char1["name"] = "Hogstorm";
		char1["level"] = 1;

		ImpunityLogger.LogInformation("Calling InsertDocument");
		ImpunityYield<BsonValue> insertAction = connection.InsertDocumentYield(TestCollectionTypes.CHARACTERS, char1);
		yield return insertAction;
		if (insertAction.Error != null)
		{
			ImpunityLogger.LogError(insertAction.Error.Message);
			yield break;
		}

		char1["level"] = 2;

		ImpunityLogger.LogInformation("Calling UpsertDocument");
		ImpunityYield<bool> upsertAction = connection.UpsertDocumentYield(TestCollectionTypes.CHARACTERS, char1);
		yield return upsertAction;
		if (upsertAction.Error != null)
		{
			ImpunityLogger.LogError(upsertAction.Error.Message);
			yield break;
		}

		ImpunityLogger.LogInformation("Calling FindDocumentById");
		ImpunityYield<BsonDocument> findAction = connection.FindDocumentByIdYield(TestCollectionTypes.CHARACTERS, "char1");
		yield return findAction;
		if (findAction.Error != null)
		{
			ImpunityLogger.LogError(findAction.Error.Message);
			yield break;
		}

		ImpunityLogger.LogInformation("Found character " + (string)(findAction.Value["name"]));


		BsonDocument char2 = new BsonDocument();
		char2["_id"] = "char2";
		char2["name"] = "Hogwind";
		char2["level"] = 1;

		ImpunityLogger.LogInformation("Calling InsertDocument for char 2");
		ImpunityYield<BsonValue> insert2Action = connection.InsertDocumentYield(TestCollectionTypes.CHARACTERS, char2);
		yield return insert2Action;
		if (insert2Action.Error != null)
		{
			ImpunityLogger.LogError(insert2Action.Error.Message);
			yield break;
		}

		ImpunityLogger.LogInformation("Calling ListDocuments");
		ImpunityYield<List<BsonDocument>> listAction = connection.ListDocumentsYield(TestCollectionTypes.CHARACTERS);
		yield return listAction;
		if (listAction.Error != null)
		{
			ImpunityLogger.LogError(listAction.Error.Message);
			yield break;
		}

		ImpunityLogger.LogInformation("characters found " + listAction.Value.Count);

		char1["level"] = 10;
		char2["level"] = 10;

		ImpunityLogger.LogInformation("Compound upsert");
		ImpunityYield<List<ActionResult>> compoundAction = connection.CompoundDatabaseActionYield(new GameStateActionBase[] {
			new UpsertDocumentAction(TestCollectionTypes.CHARACTERS, char1),
			new UpsertDocumentAction(TestCollectionTypes.CHARACTERS, char2),
			new ListDocumentsAction(TestCollectionTypes.CHARACTERS),
		});
		yield return compoundAction;
		if (compoundAction.Error != null)
		{
			ImpunityLogger.LogError(compoundAction.Error.Message);
			yield break;
		}

		ImpunityLogger.LogInformation("Compound action results " + compoundAction.Value.Count);

		connection.SendBroadcastMessage(1, "Yo yo yo");

		yield return new WaitForSeconds(0.1f);

	}

	async Task AsyncTests()
    {
		ImpunityLogger.LogInformation("Starting async tests");

		await AsyncConnectionTest();

		await SetupAsync();

		await LiveDataTest();

		await SetupAsync();

		await SetupLivePersistedDataTest();

		// Tears down and reopens server, doesn't delete on-disk data
		await ResetServerAsync();

		await VerifyLivePersistedDataTest();

		ImpunityLogger.LogInformation("Completed async tests");
	}

	async Task AsyncConnectionTest()
    {
		ImpunityLogger.LogInformation("Running async local connection test");

		try
        {
			LocalGame = new LocalGameConnection(GameServer, CurrFormat);

			BsonDocument char1 = new BsonDocument();
			char1["_id"] = "char1";
			char1["name"] = "Hogstorm";
			char1["level"] = 1;

			await LocalGame.ConnectAsync();
			await LocalGame.InsertDocumentAsync(TestCollectionTypes.CHARACTERS, char1);
			List<BsonDocument> chars = await LocalGame.ListDocumentsAsync(TestCollectionTypes.CHARACTERS);

			ImpunityLogger.LogInformation("characters found " + chars.Count);
		}
		catch(Exception e)
        {
			ImpunityLogger.LogError("Got exception in async test: " + e.Message);
        }

		LocalGame.Dispose();
		LocalGame = null;

		ImpunityLogger.LogInformation("Done with async local connection test");
	}

	async Task LiveDataTest()
    {
		ImpunityLogger.LogInformation("Running live data test");

		try
		{
			LocalGame = new LocalGameConnection(GameServer, CurrFormat);

			await LocalGame.ConnectAsync();
			//await LocalGame.EnsureFormatAsync(CurrFormat);

			TCPServer = ImpunityServer.MakeTCPServer(GameServer, Options);
			TCPServer.Start();

			RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(TCPServer.TCPEndpoint, null, null, CurrFormat, Options);
			RemoteGame.OnNetworkError = OnNetworkError;

			await Task.Delay(20);

			await RemoteGame.ConnectAsync();

			await LiveBroadcastTests(LocalGame, RemoteGame);

			await LiveLockTests(LocalGame, RemoteGame);

			await LiveChannelTests(LocalGame, RemoteGame);

		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("Got exception in live data test: " + e.ToString());
		}

		RemoteGame.Dispose();
		RemoteGame = null;

		await Task.Delay(100);

		TCPServer.Dispose();
		TCPServer = null;

		LocalGame.Dispose();
		LocalGame = null;


		ImpunityLogger.LogInformation("Done with live data test");
	}

	async Task LiveBroadcastTests(BaseGameConnection c1, BaseGameConnection c2)
    {
		ImpunityLogger.LogInformation("Doing broadcast test");

		bool gotMessage = false;

		c1.OnBroadcastMessage = (int messageType, BsonValue messageBody, string sender) =>
		{
			ImpunityLogger.LogInformation("Got broadcase message type: " + messageType + " body: " + messageBody.ToString() + " from: " + sender);
			gotMessage = true;
		};

		c2.SendBroadcastMessage(22, "Hiya buddy!");

		while (!gotMessage)
		{
			await Task.Delay(20);
		}

		ImpunityLogger.LogInformation("Broadcase test complete");
	}

	async Task LiveLockTests(BaseGameConnection c1, BaseGameConnection c2)
	{
		ImpunityLogger.LogInformation("Doing lock tests");

		bool localLocked = await c1.TryToLockAsync("tempLock", "xyz");
		if (localLocked != true)
		{
			ImpunityLogger.LogError("Unable to lock temp lock");
			return;
		}

		bool remoteLocked = await c2.TryToLockAsync("tempLock", "snad");
		if (remoteLocked != false)
		{
			ImpunityLogger.LogError("Was able to get lock that should be held");
			return;
		}

		bool localUnlocked = await c1.UnlockAsync("tempLock", "xyz");
		if (localUnlocked != true)
		{
			ImpunityLogger.LogError("Unable to unlock temp lock");
			return;
		}

		ImpunityLogger.LogInformation("Lock tests complete");
	}

	void Connection1ObjectCreated(IDistributedEntity obj, IDistributedChannel channel, bool newlyCreated)
    {
		ImpunityLogger.LogInformation("Got new object on connection 1 in channel " + channel.Name + ": " + obj.DistributedEntityId + " newly created: " + newlyCreated);
    }

	void Connection2ObjectCreated(IDistributedEntity obj, IDistributedChannel channel, bool newlyCreated)
	{
		ImpunityLogger.LogInformation("Got new object on connection 2 in channel " + channel.Name + ": " + obj.DistributedEntityId + " newly created: " + newlyCreated);
	}

	async Task LiveChannelTests(BaseGameConnection c1, BaseGameConnection c2)
    {
		ImpunityLogger.LogInformation("Doing live channel tests");

		c1.EntityManager.OnDistributedObjectCreated = Connection1ObjectCreated;
		c2.EntityManager.OnDistributedObjectCreated = Connection2ObjectCreated;

		TestZone c1channel = new TestZone();
		c1channel = await c1.EntityManager.SubscribeToChannelAsync("testZone", c1channel);
		ImpunityLogger.LogInformation("C1 Made channel " + c1channel.DistributedEntityId);

		TestPlayer c1player1 = new TestPlayer();
		c1player1.Name = "player1";
		c1player1 = await c1.EntityManager.CreateObjectAsync(c1player1, c1channel);
		ImpunityLogger.LogInformation("C1 Made player: " + c1player1.DistributedEntityId);

		TestZone c2channel = await c2.EntityManager.SubscribeToChannelAsync<TestZone>("testZone", null);
		ImpunityLogger.LogInformation("C2 subscribed to channel " + c2channel.DistributedEntityId);

		TestPlayer c2player2 = new TestPlayer();
		c2player2.Name = "player2";
		c2player2 = await c2.EntityManager.CreateObjectAsync(c2player2, c1channel);
		ImpunityLogger.LogInformation("C2 Made player: " + c2player2.DistributedEntityId);

		try
		{
			TestPlayer c2player1 = new TestPlayer();
			c2player1.Name = "player1";
			c2player1 = await c2.EntityManager.CreateObjectAsync(c2player1, c2channel);
			ImpunityLogger.LogError("C2 Made player1: " + c2player1.DistributedEntityId);
		}
		catch (ImpuntyErrorResponseException e)
		{
			ImpunityLogger.LogInformation("C2 prevented from making duplicate player1: " + e.Message);
		}

		WaitingForCount = 4;

		c1channel.SetScalar(2.0f);
		c1channel.SetStatus("New status");
		c1channel.InitGrid(100);
		c1channel.InitChat(100);

		if (!await WaitForCount("Didn't get distributed status"))
		{
			return;
		}

		ImpunityLogger.LogInformation("c2channel got new status");

		WaitingForCount = 2;

		c2player2.SetDirection(new Vector3(1.0f, 1.0f, 1.0f));

		if (!await WaitForCount("Didn't get direction change callback"))
		{
			return;
		}

		ImpunityLogger.LogInformation("Direction change callback worked");

		WaitingForCount = 2;

		c2player2.TriggerEvent(1, "Wooow!", null);

		if (!await WaitForCount("Didn't event trigger"))
		{
			return;
		}

		ImpunityLogger.LogInformation("Event trigger worked worked");

		WaitingForCount = 2;
		c1player1.Delete("Deleted buddy", null);

		if (!await WaitForCount("Didn't get delete callbacks"))
		{
			return;
		}

		ImpunityLogger.LogInformation("Deletes happened");

		
		await c2player2.LockAsync("xyz");

		bool deleted = await c1.DeleteEntityAsync(c2player2.DistributedEntityId, null, null);
		if (deleted)
		{
			ImpunityLogger.LogError("Was able to delete locked object");
			return;
		}

		await c2player2.UnlockAsync("xyz");
		ImpunityLogger.LogInformation("Completed locking");


		ImpunityLogger.LogInformation("Distributed array tests:");

		WaitingForCount = 2;
		c1channel.SetGrid(10, 32);

		if (!await WaitForCount("Didn't get array callbacks"))
		{
			return;
		}

		ImpunityLogger.LogInformation("Distributed array done");

		ImpunityLogger.LogInformation("Unsubscribing both connections");

		await c1channel.UnsubscribeAsync();
		await c2channel.UnsubscribeAsync();

		ImpunityLogger.LogInformation("Unsubscribed");

		await Task.Delay(100);

		ImpunityLogger.LogInformation("Live channel tests complete");
    }

	async Task SetupLivePersistedDataTest()
	{
		ImpunityLogger.LogInformation("Running setup live persisted data test");

		try
		{
			LocalGame = new LocalGameConnection(GameServer, CurrFormat);

			await LocalGame.ConnectAsync();

			await SetupPersistedZoneAndObjects(LocalGame);

			await Task.Delay(100);

		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("Got exception in setup live persisted data test: " + e.ToString());
		}


		LocalGame.Dispose();
		LocalGame = null;

		ImpunityLogger.LogInformation("Completed setup live persisted data test");
	}

	async Task SetupPersistedZoneAndObjects(BaseGameConnection c)
	{
		ImpunityLogger.LogInformation("Setting up persisted zone and objects");

		DInt32[] zoneGrid = new DInt32[100];
		zoneGrid[0] = 25;
		zoneGrid[10] = 4;
		zoneGrid[90] = -2;

		PersistedTestZone zone1 = new PersistedTestZone();
		zone1.SetStatus("ready");
		zone1.SetScalar(2.5f);
		zone1.InitChat(200);
		zone1.ReplaceGrid(zoneGrid);

		zone1 = await c.EntityManager.SubscribeToChannelAsync("zone1", zone1);
		ImpunityLogger.LogInformation("C1 Made persisted zone " + zone1.DistributedEntityId);

		for (int i = 0; i < 10; i++)
		{
			ZonePersistedObject zobj = new ZonePersistedObject();
			zobj.SetPosition(new Vector2Int(10, -2));
			zobj.InitFlags();
			zobj.AddFlags(34, "done");
			zobj.InitQuests();
			zobj.AddQuests("butt", "in progress");
			zobj.SetDirection(new Vector3(0.0f, 1.0f, 2.0f));

			await c.EntityManager.CreateObjectAsync(zobj, zone1);
		}

		ImpunityLogger.LogInformation("Done setting up persisted zone and objects");
	}

	async Task VerifyLivePersistedDataTest()
	{
		ImpunityLogger.LogInformation("Running verify live persisted data test");

		try
		{
			LocalGame = new LocalGameConnection(GameServer, CurrFormat);

			await LocalGame.ConnectAsync();

			TCPServer = ImpunityServer.MakeTCPServer(GameServer, Options);
			TCPServer.Start();

			RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(TCPServer.TCPEndpoint, null, null, CurrFormat, Options);
			RemoteGame.OnNetworkError = OnNetworkError;

			await Task.Delay(20);

			await RemoteGame.ConnectAsync();

			await VerifyPersistedZoneAndObjects(LocalGame, RemoteGame);

		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("Got exception in verify live persisted data test: " + e.ToString());
		}

		RemoteGame.Dispose();
		RemoteGame = null;

		await Task.Delay(100);

		TCPServer.Dispose();
		TCPServer = null;

		LocalGame.Dispose();
		LocalGame = null;

		ImpunityLogger.LogInformation("Completed verify live persisted data test");
	}

	async Task VerifyPersistedZoneAndObjects(BaseGameConnection c1, BaseGameConnection c2)
	{
		ImpunityLogger.LogInformation("Starting verifying persisted zone and objects");

		Task<PersistedTestZone> c1Zone1T = c1.EntityManager.SubscribeToChannelAsync<PersistedTestZone>("zone1", null);
		Task<PersistedTestZone> c2Zone1T = c2.EntityManager.SubscribeToChannelAsync<PersistedTestZone>("zone1", null);

		PersistedTestZone c1Zone1 = await c1Zone1T;
		PersistedTestZone c2Zone1 = await c2Zone1T;

		ImpunityLogger.LogInformation("Both clients subscribed to zone1");

		if (c1Zone1.GetGrid(0) != 25 || c2Zone1.GetGrid(0) != 25)
		{
			ImpunityLogger.LogError("Grid not initialized with loaded data");
		}

		Vector2Int expectedPos = new Vector2Int(10, -2);

		foreach(var ent in c1Zone1.DistributedObjects.Values)
		{
			ZonePersistedObject c1zobj = (ZonePersistedObject)ent;
			ZonePersistedObject c2zobj = (ZonePersistedObject)c1Zone1.DistributedObjects[c1zobj.DistributedEntityId];

			if (c1zobj.GetPosition() != expectedPos || c2zobj.GetPosition() != expectedPos)
			{
				ImpunityLogger.LogError("Zobj position not set");
			}

			if (c1zobj.GetFlags(34) != "done" || c2zobj.GetFlags(34) != "done")
			{
				ImpunityLogger.LogError("Zobj flags not set");
			}

		}

		ImpunityLogger.LogInformation("Done verifying persisted zone and objects");
	}

	async Task<bool> WaitForCount(string error)
	{
		int tries = 10;
		while (WaitingForCount > 0)
		{
			await Task.Delay(100);
			tries--;
			if (tries == 0)
			{
				ImpunityLogger.LogError(error);
				return false;
			}
		}

		return true;
	}

	void OnNetworkError(ImpunityErrorResponse err)
    {
		ImpunityLogger.LogError("Got network error: " + err.Message);
    }

	void OnServerFound(ServerInfo serverInfo)
	{

		string gameName = "new game";
		if (serverInfo.GameSummary != null)
        {
			gameName = serverInfo.GameSummary["name"];
		}
		ImpunityLogger.LogInformation("Found a server: " + gameName);

		FoundServer = true;
		ServerEndpoint = serverInfo.Address;
		FoundGameId = serverInfo.GameId;
	}



	void Update()
	{
		LocalGame?.Update();
		Finder?.Update();
		RemoteGame?.Update();

		if(TestsDone)
        {
			
			ImpunityLogger.LogInformation("Done with tests");

			Cleanup();

			TestsDone = false;
			Quit();
		}
	}

	void OnApplicationQuit()
    {
		ImpunityLogger.LogInformation("Shutting down");

		Cleanup();

	}

	void Quit()
    {
#if UNITY_EDITOR
		EditorApplication.ExitPlaymode();
#else
			Application.Quit();
#endif
	}

	public static void DeleteFolder(string path)
	{
		RecursiveDelete(new DirectoryInfo(path));
	}

	public static void RecursiveDelete(DirectoryInfo baseDir)
	{
		if (!baseDir.Exists)
			return;

		foreach (var dir in baseDir.EnumerateDirectories())
		{
			RecursiveDelete(dir);
		}

		var files = baseDir.GetFiles();
		foreach (var file in files)
		{
			file.IsReadOnly = false;
			file.Delete();
		}
		baseDir.Delete();
	}
}
