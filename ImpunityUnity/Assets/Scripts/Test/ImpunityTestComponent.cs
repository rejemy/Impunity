using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.IO;

using UnityEngine;

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

	void Start()
	{
		ImpunityUnityLogger.Setup(ImpunityLogLevel.INFO);


		StartCoroutine(ComboTest());

	}

	IEnumerator ComboTest()
	{
		GameStatePath = Path.Join(Application.persistentDataPath, "ImpTest", "TestGame");

		Options = new ImpunityOptions
		{
			GameTypeCode = "ImpTest",
			LANDiscoverable = true
		};

		CurrFormat = new GameStateFormat
		{
			Version = 1,
			Collections = new GameStateCollection[]
			{
				null, //First entry is always null
				new GameStateCollection
				{
					Name = "Characters"
				}
			}
		};

		yield return Setup();

		yield return LocalConnectionTest();

		yield return Setup();

		yield return TCPConnectionTest();

		Cleanup();
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

		LocalGame = new LocalGameConnection(GameServer);

		yield return GenericConnectionTest(LocalGame);

		LocalGame.Dispose();
		LocalGame = null;
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

		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(ServerEndpoint, Options);
		RemoteGame.OnNetworkError = OnNetworkError;

		yield return GenericConnectionTest(RemoteGame);

		RemoteGame.Dispose();
		RemoteGame = null;
	}

	IEnumerator GenericConnectionTest(BaseGameConnection connection)
    {
		ImpunityYield connectAction = connection.Connect();
		yield return connectAction;
		if (connectAction.Error != null)
		{
			Debug.Log("Error connecting: " + connectAction.Error.Message);
			yield break;
		}

		Debug.Log("TCP Connected");

		Debug.Log("Calling EnsureFormat");
		ImpunityYield ensureFormat = connection.EnsureFormat(CurrFormat);
		yield return ensureFormat;
		if (ensureFormat.Error != null)
		{
			Debug.LogError(ensureFormat.Error.Message);
			yield break;
		}

		BsonDocument char1 = new BsonDocument();
		char1["_id"] = "char1";
		char1["name"] = "Hogstorm";
		char1["level"] = 1;

		Debug.Log("Calling InsertDocument");
		ImpunityYield<BsonValue> insertAction = connection.InsertDocument(1, char1);
		yield return insertAction;
		if (insertAction.Error != null)
		{
			Debug.LogError(insertAction.Error.Message);
			yield break;
		}

		char1["level"] = 2;

		Debug.Log("Calling UpsertDocument");
		ImpunityYield<bool> upsertAction = connection.UpsertDocument(1, char1);
		yield return upsertAction;
		if (upsertAction.Error != null)
		{
			Debug.LogError(upsertAction.Error.Message);
			yield break;
		}

		Debug.Log("Calling FindDocumentById");
		ImpunityYield<BsonDocument> findAction = connection.FindDocumentById(1, "char1");
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
		ImpunityYield<BsonValue> insert2Action = connection.InsertDocument(1, char2);
		yield return insert2Action;
		if (insert2Action.Error != null)
		{
			Debug.LogError(insert2Action.Error.Message);
			yield break;
		}

		Debug.Log("Calling ListDocuments");
		ImpunityYield<List<BsonDocument>> listAction = connection.ListDocuments(1);
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
			new UpsertDocumentAction(1, char1),
			new UpsertDocumentAction(1, char2),
			new ListDocumentActions(1),
		});
		yield return compoundAction;
		if (compoundAction.Error != null)
		{
			Debug.LogError(compoundAction.Error.Message);
			yield break;
		}

		Debug.Log("Compound action results " + compoundAction.Value.Count);
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
	}

	void OnApplicationQuit()
    {
		Debug.Log("Shutting down");

		Cleanup();

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
