using System.Collections;
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

	GameStateServer GameServer;
	LocalGameConnection LocalGame;

	ImpunityTCPServer TCPServer;

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
		BsonDocument summary = new BsonDocument();
		summary["name"] = "Test Game";

		Options = new ImpunityOptions
		{
			GameTypeCode = "ImpTest",
			LANDiscoverable = true
		};

		string gamestatePath = Path.Join(Application.persistentDataPath, "ImpTest", "TestGame");
		DeleteFolder(gamestatePath);

		Debug.Log("Creating local game server");

		GameServer = GameStateServer.Create(gamestatePath, summary);

		Debug.Log("Creating TCP game server");

		TCPServer = new ImpunityTCPServer(GameServer, Options);
		TCPServer.Start();

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

		yield return LocalConnectionTest();

		yield return TCPConnectionTest();


		Cleanup();
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
	}

	IEnumerator LocalConnectionTest()
	{
		Debug.Log("Running local connection test");

		LocalGame = new LocalGameConnection(GameServer);

		Debug.Log("Calling EnsureFormat");
		ImpunityYield ensureFormat = LocalGame.EnsureFormat(CurrFormat);
		yield return ensureFormat;
		if (ensureFormat.Error != null)
		{
			Debug.LogError(ensureFormat.Error.Message);
			yield break;
		}

		BsonDocument char1 = new BsonDocument();
		char1["_id"] = "char1";
		char1["name"] = "Hogwind";
		char1["level"] = 1;

		Debug.Log("Calling InsertDocument");
		ImpunityYield<BsonValue> insertAction = LocalGame.InsertDocument(1, char1);
		yield return insertAction;
		if (insertAction.Error != null)
		{
			Debug.LogError(insertAction.Error.Message);
			yield break;
		}

		Debug.Log("Calling FindDocumentById");
		ImpunityYield<BsonDocument> findAction = LocalGame.FindDocumentById(1, "char1");
		yield return findAction;
		if (findAction.Error != null)
		{
			Debug.LogError(findAction.Error.Message);
			yield break;
		}

		Debug.Log("Found character " + (string)(findAction.Value["name"]));

		LocalGame.Dispose();
		LocalGame = null;
	}

	IEnumerator TCPConnectionTest()
	{
		Debug.Log("Running tcp connection test");

		Debug.Log("Looking for server");
		Finder = new ImpunityTCPServerFinder(Options, OnServerFound);
		Finder.Start();

		while (!FoundServer)
        {
			yield return null;
		}
		Debug.Log("Found TCP server at " + ServerEndpoint.ToString());

		RemoteGame = new RemoteGameConnection(ServerEndpoint, Options);
		RemoteGame.OnNetworkError = OnNetworkError;

		ImpunityYield connectAction = RemoteGame.Connect();
		yield return connectAction;
		if( connectAction.Error != null)
        {
			Debug.Log("Error connecting: " + connectAction.Error.Message);
			yield break;
		}

		Debug.Log("TCP Connected");

		Debug.Log("Calling EnsureFormat");
		ImpunityYield ensureFormat = RemoteGame.EnsureFormat(CurrFormat);
		yield return ensureFormat;
		if (ensureFormat.Error != null)
		{
			Debug.LogError(ensureFormat.Error.Message);
			yield break;
		}

		BsonDocument char2 = new BsonDocument();
		char2["_id"] = "char2";
		char2["name"] = "Hogstorm";
		char2["level"] = 1;

		Debug.Log("Calling InsertDocument");
		ImpunityYield<BsonValue> insertAction = RemoteGame.InsertDocument(1, char2);
		yield return insertAction;
		if (insertAction.Error != null)
		{
			Debug.LogError(insertAction.Error.Message);
			yield break;
		}

		Debug.Log("Calling FindDocumentById");
		ImpunityYield<BsonDocument> findAction = RemoteGame.FindDocumentById(1, "char2");
		yield return findAction;
		if (findAction.Error != null)
		{
			Debug.LogError(findAction.Error.Message);
			yield break;
		}

		Debug.Log("Found character " + (string)(findAction.Value["name"]));

		RemoteGame.Dispose();
		RemoteGame = null;
	}

	void OnNetworkError(ImpunityError err)
    {
		Debug.Log("Got network error: " + err.Message);
    }

	void OnServerFound(ServerInfo serverInfo)
	{
		string gameName = serverInfo.GameSummary["name"];
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
