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

		BsonMapper.Global.IncludeFields = true;

		ImpunityUnityLogger.Setup(ImpunityLogLevel.DEBUG);

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
				typeof(ZonePersistedObject),
				typeof(TestEmptyObj)
			}
		);

		StartCoroutine(ComboTest());
		//StartCoroutine(StandaloneServerTest());
	}

	IEnumerator StandaloneServerTest()
	{
		yield return StandaloneServerTCPConnectionTest(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 29654), "world1");
	}

	IEnumerator ComboTest()
	{
		
		yield return Setup();

		yield return LocalConnectionTest();

		yield return Setup();

		yield return TCPConnectionTest();
		
		yield return Setup();

		yield return TCPConnectionFailTest();

		yield return Setup();

		AsyncTests().ContinueWith((t)=>
		{
			TestsDone = true;
			ImpunityLogger.LogInformation("Tests complete");
		});
	}

	IEnumerator Setup()
    {
		Cleanup();

		yield return new WaitForSeconds(0.1f);

		ImpunityLogger.LogInformation("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Create("testgame", "yorb", GameStatePath, summary, Options);
	}

	async Task SetupAsync()
    {
		Cleanup();

		await Task.Delay(100);

		ImpunityLogger.LogInformation("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Create("testgame", "yorb", GameStatePath, summary, Options);
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

		GameServer = GameStateServer.Open("testgame", "yorb", GameStatePath, Options);
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

		TCPServer = new ImpunityServer(GameServer, Options);
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

		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(ServerEndpoint, FoundGameId, "yorb", CurrFormat, Options);
		RemoteGame.OnNetworkError = OnNetworkError;

		yield return GenericConnectionTest(RemoteGame);

		RemoteGame.Dispose();
		RemoteGame = null;

		TCPServer.Dispose();
		TCPServer = null;

		ImpunityLogger.LogInformation("Done with TCP connection test");
	}

	IEnumerator TCPConnectionFailTest()
	{
		ImpunityLogger.LogInformation("Running tcp connection fail test");

		ImpunityLogger.LogInformation("Creating TCP game server");

		TCPServer = new ImpunityServer(GameServer, Options);
		TCPServer.Start();

		yield return new WaitForSeconds(0.1f);

		// Make connection without required password
		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(new IPEndPoint(IPAddress.Loopback, Options.ServerPort), FoundGameId, null, CurrFormat, Options);
		RemoteGame.OnNetworkError = (ImpunityErrorResponse err) =>
		{
			ImpunityLogger.LogError("Got fail test network error: " + err.Message);
		};

		ImpunityYield connectAction = RemoteGame.ConnectYield();
		yield return connectAction;
		if (connectAction.Error != null)
		{
			ImpunityLogger.LogInformation("Got expected error: " + connectAction.Error.Message);
		}
		else
		{
			ImpunityLogger.LogError("Fail test connected succesfully, but it shouldn't");
		}

		RemoteGame.Dispose();
		RemoteGame = null;

		TCPServer.Dispose();
		TCPServer = null;

		ImpunityLogger.LogInformation("Done with TCP connection fail test");
	}

	IEnumerator StandaloneServerTCPConnectionTest(IPEndPoint serverhost, string gameId)
	{
		ImpunityLogger.LogInformation("Running standalone server tcp connection test to " + serverhost.ToString() + "/" + gameId);


		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(serverhost, gameId, "fuzzy-wuzzy", CurrFormat, Options);
		RemoteGame.OnNetworkError = OnNetworkError;

		yield return GenericConnectionTest(RemoteGame);

		RemoteGame.Dispose();
		RemoteGame = null;

		ImpunityLogger.LogInformation("Done with standalone server TCP connection test");
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

			TCPServer = new ImpunityServer(GameServer, Options);
			TCPServer.Start();

			RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(TCPServer.TCPEndpoint, null, "yorb", CurrFormat, Options);
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

		bool localLocked = await c1.TryToLockAsync("tempLock");
		if (localLocked != true)
		{
			ImpunityLogger.LogError("Unable to lock temp lock");
			return;
		}

		bool remoteLocked = await c2.TryToLockAsync("tempLock");
		if (remoteLocked != false)
		{
			ImpunityLogger.LogError("Was able to get lock that should be held");
			return;
		}

		bool localUnlocked = await c1.UnlockAsync("tempLock");
		if (localUnlocked != true)
		{
			ImpunityLogger.LogError("Unable to unlock temp lock");
			return;
		}

		var waitLockResult = await c1.WaitForLockAsync("waitLock");
		if (waitLockResult != LockWaitResult.Locked)
		{
			ImpunityLogger.LogError("Unable to lock waitLock");
			return;
		}

		TaskCompletionSource<bool> waitComplete = new TaskCompletionSource<bool>();
		
		await Task.Delay(20);

		c2.WaitForLock("waitLock", (err, lockResult) =>
		{
			if (lockResult != LockWaitResult.Unlocked)
			{
				ImpunityLogger.LogError("Got incorrect wait lock result " + lockResult.ToString());
			}
			else
			{
				ImpunityLogger.LogInformation("Got wait lock result " + lockResult.ToString());
			}
			
			waitComplete.SetResult(true);
		});
		
		await Task.Delay(20);

		bool waitUnlocked = await c1.UnlockAsync("waitLock");
		if (localUnlocked != true)
		{
			ImpunityLogger.LogError("Unable to unlock waitLock");
			return;
		}

		await waitComplete.Task;

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

		TestZone c1channelInitializer = new TestZone();
		c1channelInitializer.Chat.Init(10);
		c1channelInitializer.Chat.Add("hey dudes");
		c1channelInitializer.Grid.Replace(new int[] { 1,2 });

		TestZone c1channel = await c1.EntityManager.SubscribeToChannelAsync("testZone", c1channelInitializer);
		ImpunityLogger.LogInformation("C1 Made channel " + c1channel.DistributedEntityId);

		TestPlayer c1player1 = new TestPlayer();
		c1player1.Name = "player1";
		c1player1.MovementState.Set(new CustomMovementStateData
		{
			StartPos = new Vector3(1, 2, 3),
			Velocity = new Vector3(1,0,-1)
		});

		c1player1 = await c1.EntityManager.CreateObjectAsync(c1player1, c1channel, false);
		ImpunityLogger.LogInformation("C1 Made player: " + c1player1.DistributedEntityId);
		
		await Task.Delay(100);

		TestZone c2channel = await c2.EntityManager.SubscribeToChannelAsync<TestZone>("testZone", null);
		ImpunityLogger.LogInformation("C2 subscribed to channel " + c2channel.DistributedEntityId);

		TestPlayer c2player2 = new TestPlayer();
		c2player2.Name = "player2";
		c2player2 = await c2.EntityManager.CreateObjectAsync(c2player2, c2channel, false);
		ImpunityLogger.LogInformation("C2 Made player: " + c2player2.DistributedEntityId);

		TestEmptyObj c1empty = new TestEmptyObj();
		await c1.EntityManager.CreateObjectAsync(c1empty, c1channel, false);

		try
		{
			TestPlayer c2player1 = new TestPlayer();
			c2player1.Name = "player1";
			c2player1 = await c2.EntityManager.CreateObjectAsync(c2player1, c2channel, false);
			ImpunityLogger.LogError("C2 Made player1: " + c2player1.DistributedEntityId);
		}
		catch (ImpuntyErrorResponseException e)
		{
			ImpunityLogger.LogInformation("C2 prevented from making duplicate player1: " + e.Message);
		}

		WaitingForCount = 4;

		c1channel.Scalar.Set(2.0f);
		c1channel.Status.Set("New status");
		c1channel.Grid.Init(100);
		c1channel.Chat.Init(100);

		if (!await WaitForCount("Didn't get distributed status"))
		{
			return;
		}

		ImpunityLogger.LogInformation("c2channel got new status");

		WaitingForCount = 6;

		c1player1.Position.Set(new Vector3(5.0f, 5.0f, 5.0f));
		c1player1.BigData.Set(new BsonDataRecord {Thingy1 = "stuff", Thingy2 = 10, Thingy3 = 0.1f});
		c2player2.Direction.Set(new Vector3(1.0f, 1.0f, 1.0f));

		if (!await WaitForCount("Didn't get direction and/or position change callback"))
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

		
		await c2player2.TryLockAsync();

		bool deleted = await c1.DeleteEntityAsync(c2player2.DistributedEntityId, null);
		if (deleted)
		{
			ImpunityLogger.LogError("Was able to delete locked object");
			return;
		}

		await c2player2.UnlockAsync();
		ImpunityLogger.LogInformation("Completed locking");


		ImpunityLogger.LogInformation("Distributed array tests:");

		WaitingForCount = 2;
		c1channel.Grid.Set(10, 32);

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

		TestZone c1channelCreate = new TestZone();
		c1channelCreate.Status.Set("Stuff");

		List<IDistributedEntity> zoneObjects = new List<IDistributedEntity>();

		for(int i=1; i<=5; i++)
		{
			var zonePlayer = new TestPlayer();
			zonePlayer.Name = "player_"+i;
			zonePlayer.Direction.Set(new Vector3(1,1,i));
			zoneObjects.Add(zonePlayer);
		}

		ImpunityLogger.LogInformation("Creating a channel with objects");

		await c1.EntityManager.CreateChannelAsync("createChannelZoneTest", c1channelCreate, true, zoneObjects);

		TestZone c2channelFromCreate = await c2.EntityManager.SubscribeToChannelAsync<TestZone>("createChannelZoneTest", null);

		if (c2channelFromCreate.Status != "Stuff")
		{
			ImpunityLogger.LogError("Created channel has incorrect properties");
		}

		if (c2channelFromCreate.DistributedObjects.Count != 5)
		{
			ImpunityLogger.LogError("Created channel doesn't have all its objects");
		}

		await c2channelFromCreate.UnsubscribeAsync();

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

		int[] zoneGrid = new int[100];
		zoneGrid[0] = 25;
		zoneGrid[10] = 4;
		zoneGrid[90] = -2;

		PersistedTestZone zone1 = new PersistedTestZone();
		zone1.IsPersisted = true;
		zone1.Status.Set("ready");
		zone1.Scalar.Set(2.5f);
		zone1.Chat.Init(200);
		zone1.Grid.Replace(zoneGrid);

		zone1 = await c.EntityManager.SubscribeToChannelAsync("zone1", zone1);
		ImpunityLogger.LogInformation("C1 Made persisted zone " + zone1.DistributedEntityId);

		for (int i = 0; i < 10; i++)
		{
			ZonePersistedObject zobj = new ZonePersistedObject();
			zobj.IsPersisted = true;
			zobj.Position.Set(new Vector2Int(10, -2));
			zobj.Flags.Init();
			zobj.Flags.Add(34, "done");
			zobj.Quests.Init();
			zobj.Quests.Add("butt", "in progress");
			zobj.Direction.Set(new Vector3(0.0f, 1.0f, 2.0f));

			await c.EntityManager.CreateObjectAsync(zobj, zone1, false);
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

			TCPServer = new ImpunityServer(GameServer, Options);
			TCPServer.Start();

			RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(TCPServer.TCPEndpoint, null, "yorb", CurrFormat, Options);
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

		
		Task<PersistedTestZone> c1Zone1T = c1.EntityManager.SubscribeToChannelAsync<PersistedTestZone>("zone1", new PersistedTestZone { IsPersisted = true });
		Task<PersistedTestZone> c2Zone1T = c2.EntityManager.SubscribeToChannelAsync<PersistedTestZone>("zone1", new PersistedTestZone { IsPersisted = true });

		PersistedTestZone c1Zone1 = await c1Zone1T;
		PersistedTestZone c2Zone1 = await c2Zone1T;

		ImpunityLogger.LogInformation("Both clients subscribed to zone1");

		if (c1Zone1.Grid.Get(0) != 25 || c2Zone1.Grid.Get(0) != 25)
		{
			ImpunityLogger.LogError("Grid not initialized with loaded data");
		}

		Vector2Int expectedPos = new Vector2Int(10, -2);

		foreach(var ent in c1Zone1.DistributedObjects.Values)
		{
			ZonePersistedObject c1zobj = (ZonePersistedObject)ent;
			ZonePersistedObject c2zobj = (ZonePersistedObject)c2Zone1.DistributedObjects[c1zobj.DistributedEntityId];

			if (c1zobj.Position.Get() != expectedPos || c2zobj.Position.Get() != expectedPos)
			{
				ImpunityLogger.LogError("Zobj position not set");
			}

			if (c1zobj.Flags.Get(34) != "done" || c2zobj.Flags.Get(34) != "done")
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
