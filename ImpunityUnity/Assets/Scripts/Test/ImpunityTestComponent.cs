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

using Impunity;
using Impunity.GameState;
using Impunity.Connection;
using Impunity.Unity;
using Impunity.Networking;

using UltraLiteDB;


public static class TestCollectionTypes
{
	// 0 is reserved
	public const int CHARACTERS = 1;
}

public static class TestEntityTypes
{
	// 0 is reserved
	public const int PLAYER = 1;
	public const int ZONE = 2;
}


[DistributedEntity(TestEntityTypes.PLAYER, FactoryMethod = "DistributedEntityFactory")]
public partial class TestPlayer : DistributedEntityBase
{
	enum DistributedPropIds
    {
		TESTBOOL = 1,
		DIRECTION = 3
	}

	public static IDistributedEntity DistributedEntityFactory() { return new TestPlayer(); }

	[Distributed((int)DistributedPropIds.TESTBOOL, OnChanged = "OnTestBoolChanged")]
	private DistributedValue<DBool> TestBool;
	
	private void OnTestBoolChanged(bool oldValue, bool newValue)
    {

    }

	[Distributed((int)DistributedPropIds.DIRECTION, OnChanged = "OnDirectionChanged")]
	private DistributedValue<DVector3> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{

	}

}

/*
public partial class TestPlayer
{
	public void SetTestBool(bool v)
	{
		if (TestBool.Set(v)) SetDirty((int)DistributedPropIds.TESTBOOL);
	}
	public bool GetTestBool()
	{
		return (DBool)TestBool;
	}
	private void imp_WriteTestBool(BinaryWriter w)
	{
		TestBool.WriteChangesTo(w);
	}
	private void imp_UpdateTestBool(BinaryReader r)
    {
		bool oldValue = (DBool)TestBool;
		TestBool.ReadChangesFrom(r);
		bool newValue = (DBool)TestBool;
		OnTestBoolChanged(oldValue, newValue);
	}

	public void SetDirection(Vector3 v)
	{
		if (Direction.Set(v)) SetDirty((int)DistributedPropIds.DIRECTION);
	}
	public Vector3 GetDirection()
	{
		return (DVector3)Direction;
	}
	private void imp_WriteDirection(BinaryWriter w)
	{
		Direction.WriteChangesTo(w);
	}
	private void imp_UpdateDirection(BinaryReader r)
	{
		Vector3 oldValue = (DVector3)Direction;
		Direction.ReadChangesFrom(r);
		Vector3 newValue = (DVector3)Direction;
		OnDirectionChanged(oldValue, newValue);
	}
}
*/

[DistributedEntity(TestEntityTypes.ZONE)]
public partial class TestZone : DistributedChannelBase
{
	enum DistributedPropIds
	{
		STATUS = 1,
		SCALAR = 3
	}

	[Distributed((int)DistributedPropIds.STATUS)]
	private DistributedValue<DString> Status;
	

	[Distributed((int)DistributedPropIds.SCALAR)]
	private DistributedValue<DFloat> Scalar;
	
}

/*
public partial class TestZone
{
	public void SetStatus(string v)
	{
		if (Status.Set(v)) SetDirty((int)DistributedPropIds.STATUS);
	}
	public string GetStatus()
	{
		return (DString)Status;
	}
	private void imp_WriteStatus(BinaryWriter w)
	{
		Status.WriteChangesTo(w);
	}
	private void imp_UpdateStatus(BinaryReader r)
	{
		Status.ReadChangesFrom(r);
	}

	public void SetScalar(float v)
	{
		if (Scalar.Set(v)) SetDirty((int)DistributedPropIds.SCALAR);
	}
	public float GetScalar()
	{
		return (DFloat)Scalar;
	}
	private void imp_WriteScalar(BinaryWriter w)
	{
		Scalar.WriteChangesTo(w);
	}
	private void imp_UpdateScalar(BinaryReader r)
	{
		Scalar.ReadChangesFrom(r);
	}
}
*/

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

	bool TestsDone = false;


	void Start()
	{

		ImpunityUnityLogger.Setup(ImpunityLogLevel.INFO);

		GameStatePath = Path.Join(Application.persistentDataPath, "ImpTest", "TestGame");

		Options = new ImpunityOptions
		{
			GameTypeCode = "ImpTest",
			LANDiscoverable = true
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
				typeof(TestZone)
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

		Debug.Log("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Create(GameStatePath, summary);
	}

	async Task SetupAsync()
    {
		Cleanup();

		await Task.Delay(100);

		Debug.Log("Creating local game server");

		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		GameServer = GameStateServer.Create(GameStatePath, summary);
	}

	void Cleanup()
    {
		LocalGame?.Dispose();
		LocalGame = null;

		RemoteGame?.Dispose();
		RemoteGame = null;

		TCPServer?.Dispose();
		TCPServer = null;

		GameServer?.Dispose();
		GameServer = null;

		DeleteFolder(GameStatePath);
	}

	IEnumerator LocalConnectionTest()
	{
		Debug.Log("Running local connection test");

		LocalGame = new LocalGameConnection(GameServer, CurrFormat);

		yield return GenericConnectionTest(LocalGame);

		LocalGame.Dispose();
		LocalGame = null;

		Debug.Log("Done with local connection test");
	}

	IEnumerator TCPConnectionTest()
	{
		Debug.Log("Running tcp connection test");

		Debug.Log("Creating TCP game server");

		TCPServer = ImpunityServer.MakeTCPServer(GameServer, Options);
		TCPServer.Start();

		yield return new WaitForSeconds(0.1f);

		Debug.Log("Looking for server");
		Finder = new ImpunityTCPServerFinder(Options, OnServerFound);
		Finder.Start();

		while (!FoundServer)
        {
			yield return null;
		}
		Debug.Log("Found TCP server at " + ServerEndpoint.ToString());

		Finder.Dispose();
		Finder = null;

		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(ServerEndpoint, CurrFormat, Options);
		RemoteGame.OnNetworkError = OnNetworkError;

		yield return GenericConnectionTest(RemoteGame);

		RemoteGame.Dispose();
		RemoteGame = null;

		TCPServer.Dispose();
		TCPServer = null;

		Debug.Log("Done with TCP connection test");
	}

	IEnumerator GenericConnectionTest(BaseGameConnection connection)
    {
		connection.OnBroadcastMessage = (messageType, message, sentBy) =>
		{
			Debug.Log("Got broadcast message " + message.AsString + " from " + sentBy);
		};

		ImpunityYield connectAction = connection.Connect();
		yield return connectAction;
		if (connectAction.Error != null)
		{
			Debug.LogError("Error connecting: " + connectAction.Error.Message);
			yield break;
		}

		Debug.Log("TCP Connected");

		BsonDocument char1 = new BsonDocument();
		char1["_id"] = "char1";
		char1["name"] = "Hogstorm";
		char1["level"] = 1;

		Debug.Log("Calling InsertDocument");
		ImpunityYield<BsonValue> insertAction = connection.InsertDocument(TestCollectionTypes.CHARACTERS, char1);
		yield return insertAction;
		if (insertAction.Error != null)
		{
			Debug.LogError(insertAction.Error.Message);
			yield break;
		}

		char1["level"] = 2;

		Debug.Log("Calling UpsertDocument");
		ImpunityYield<bool> upsertAction = connection.UpsertDocument(TestCollectionTypes.CHARACTERS, char1);
		yield return upsertAction;
		if (upsertAction.Error != null)
		{
			Debug.LogError(upsertAction.Error.Message);
			yield break;
		}

		Debug.Log("Calling FindDocumentById");
		ImpunityYield<BsonDocument> findAction = connection.FindDocumentById(TestCollectionTypes.CHARACTERS, "char1");
		yield return findAction;
		if (findAction.Error != null)
		{
			Debug.LogError(findAction.Error.Message);
			yield break;
		}

		Debug.Log("Found character " + (string)(findAction.Value["name"]));


		BsonDocument char2 = new BsonDocument();
		char2["_id"] = "char2";
		char2["name"] = "Hogwind";
		char2["level"] = 1;

		Debug.Log("Calling InsertDocument for char 2");
		ImpunityYield<BsonValue> insert2Action = connection.InsertDocument(TestCollectionTypes.CHARACTERS, char2);
		yield return insert2Action;
		if (insert2Action.Error != null)
		{
			Debug.LogError(insert2Action.Error.Message);
			yield break;
		}

		Debug.Log("Calling ListDocuments");
		ImpunityYield<List<BsonDocument>> listAction = connection.ListDocuments(TestCollectionTypes.CHARACTERS);
		yield return listAction;
		if (listAction.Error != null)
		{
			Debug.LogError(listAction.Error.Message);
			yield break;
		}

		Debug.Log("characters found " + listAction.Value.Count);

		char1["level"] = 10;
		char2["level"] = 10;

		Debug.Log("Compound upsert");
		ImpunityYield<List<ActionResult>> compoundAction = connection.CompoundAction(new GameStateActionBase[] {
			new UpsertDocumentAction(TestCollectionTypes.CHARACTERS, char1),
			new UpsertDocumentAction(TestCollectionTypes.CHARACTERS, char2),
			new ListDocumentsAction(TestCollectionTypes.CHARACTERS),
		});
		yield return compoundAction;
		if (compoundAction.Error != null)
		{
			Debug.LogError(compoundAction.Error.Message);
			yield break;
		}

		Debug.Log("Compound action results " + compoundAction.Value.Count);

		connection.SendBroadcastMessage(1, "Yo yo yo");

		yield return new WaitForSeconds(0.1f);

	}

	async Task AsyncTests()
    {
		Debug.Log("Starting async tests");

		await AsyncConnectionTest();

		await SetupAsync();

		await LiveDataTest();

		Debug.Log("Completed async tests");
	}

	async Task AsyncConnectionTest()
    {
		Debug.Log("Running async local connection test");

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

			Debug.Log("characters found " + chars.Count);
		}
		catch(Exception e)
        {
			Debug.Log("Got exception in async test: " + e.Message);
        }

		LocalGame.Dispose();
		LocalGame = null;

		Debug.Log("Done with async local connection test");
	}

	async Task LiveDataTest()
    {
		Debug.Log("Running live data test");

		try
		{
			LocalGame = new LocalGameConnection(GameServer, CurrFormat);

			await LocalGame.ConnectAsync();
			//await LocalGame.EnsureFormatAsync(CurrFormat);

			TCPServer = ImpunityServer.MakeTCPServer(GameServer, Options);
			TCPServer.Start();

			RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(TCPServer.TCPEndpoint, CurrFormat, Options);
			RemoteGame.OnNetworkError = OnNetworkError;

			await Task.Delay(20);

			await RemoteGame.ConnectAsync();

			await LiveBroadcastTests(LocalGame, RemoteGame);

			await LiveLockTests(LocalGame, RemoteGame);

			await LiveChannelTests(LocalGame, RemoteGame);

		}
		catch (Exception e)
		{
			Debug.LogError("Got exception in live data test: " + e.ToString());

			
		}

		RemoteGame.Dispose();
		RemoteGame = null;

		await Task.Delay(100);

		TCPServer.Dispose();
		TCPServer = null;

		LocalGame.Dispose();
		LocalGame = null;


		Debug.Log("Done with live data test");
	}

	async Task LiveBroadcastTests(BaseGameConnection c1, BaseGameConnection c2)
    {
		Debug.Log("Doing broadcast test");

		bool gotMessage = false;

		c1.OnBroadcastMessage = (int messageType, BsonValue messageBody, string sender) =>
		{
			Debug.Log("Got broadcase message type: " + messageType + " body: " + messageBody.ToString() + " from: " + sender);
			gotMessage = true;
		};

		c2.SendBroadcastMessage(22, "Hiya buddy!");

		while (!gotMessage)
		{
			await Task.Delay(20);
		}

		Debug.Log("Broadcase test complete");
	}

	async Task LiveLockTests(BaseGameConnection c1, BaseGameConnection c2)
	{
		Debug.Log("Doing lock tests");

		bool localLocked = await c1.TryToLockAsync("tempLock", "xyz");
		if (localLocked != true)
		{
			Debug.LogError("Unable to lock temp lock");
			return;
		}


		bool remoteLocked = await c2.TryToLockAsync("tempLock", "snad");
		if (remoteLocked != false)
		{
			Debug.LogError("Was able to get lock that should be held");
			return;
		}

		bool localUnlocked = await c1.UnlockAsync("tempLock", "xyz");
		if (localUnlocked != true)
		{
			Debug.LogError("Unable to unlock temp lock");
			return;
		}

		Debug.Log("Lock tests complete");
	}

	void Connection1ObjectCreated(IDistributedEntity obj, IDistributedChannel channel, bool newlyCreated)
    {
		Debug.Log("Got new object on connection 1 in channel " + channel.ChannelName + ": " + obj.DistributedEntityId + " newly created: " + newlyCreated);
    }

	void Connection2ObjectCreated(IDistributedEntity obj, IDistributedChannel channel, bool newlyCreated)
	{
		Debug.Log("Got new object on connection 2 in channel " + channel.ChannelName + ": " + obj.DistributedEntityId + " newly created: " + newlyCreated);
	}

	async Task LiveChannelTests(BaseGameConnection c1, BaseGameConnection c2)
    {
		Debug.Log("Doing live channel tests");

		c1.EntityManager.OnDistributedObjectCreated = Connection1ObjectCreated;
		c2.EntityManager.OnDistributedObjectCreated = Connection2ObjectCreated;

		TestZone c1channel = new TestZone();
		c1channel = await c1.EntityManager.CreateChannelAsync(c1channel, "testZone");
		Debug.Log("C1 Made channel " + c1channel.DistributedEntityId);

		TestPlayer c1player1 = new TestPlayer();
		c1player1 = await c1.EntityManager.CreateObjectAsync(c1player1, c1channel);
		Debug.Log("C1 Made player: " + c1player1.DistributedEntityId);

		TestZone c2channel = await c2.EntityManager.SubscribeToChannelAsync<TestZone>("testZone");
		Debug.Log("C2 subscribed to channel " + c2channel.DistributedEntityId);

		TestPlayer c2player1 = new TestPlayer();
		c2player1 = await c2.EntityManager.CreateObjectAsync(c2player1, c1channel);
		Debug.Log("C2 Made player: " + c2player1.DistributedEntityId);

		c1channel.SetScalar(2.0f);
		c1channel.SetStatus("New status");

		int tries = 10;
		while(c2channel.GetScalar() != 2.0f)
        {
			await Task.Delay(100);
			tries--;
			if(tries == 0)
            {
				Debug.LogError("Didn't get distributed status");
				return;
            }
		}

		Debug.Log("c2channel got new status");

		/*
		await c1.TryToLockEntityAsync(player.DistributedEntityId, "xyz");

		bool updated = await c2.DeleteEntityAsync(player.DistributedEntityId, null, null);
		Debug.Log("Able to delete locked entity: " + updated);

		await c1.UnlockEntityAsync(player.DistributedEntityId, "xyz");

		await c1.UnsubscribeFromChannelAsync(channel.DistributedEntityId);
		await c2.UnsubscribeFromChannelAsync(channel.DistributedEntityId);
		Debug.Log("Unsubscribed from channel " + channel.DistributedEntityId);

		*/

		await Task.Delay(100);

		Debug.Log("Live channel tests complete");
    }

	void OnNetworkError(ImpunityError err)
    {
		Debug.Log("Got network error: " + err.Message);
    }

	void OnServerFound(ServerInfo serverInfo)
	{

		string gameName = "new game";
		if (serverInfo.GameSummary != null)
        {
			gameName = serverInfo.GameSummary["name"];
		}
		Debug.Log("Found a server: " + gameName);

		FoundServer = true;
		ServerEndpoint = serverInfo.Address;
	}



	void Update()
	{
		LocalGame?.Update();
		Finder?.Update();
		RemoteGame?.Update();

		if(TestsDone)
        {
			Debug.Log("Done with tests");

			Cleanup();

			Quit();
		}
	}

	void OnApplicationQuit()
    {
		Debug.Log("Shutting down");

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
