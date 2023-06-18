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
	GameStateServer GameServer;
	LocalGameConnection LocalGame;

	ImpunityTCPServer TCPServer;
	ImpunityTCPServerFinder Finder;
	IImpunityClient TCPClient;

	GameStateFormat CurrFormat;
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

		ImpunityOptions options = new ImpunityOptions
		{
			GameTypeCode = "ImpTest",
			LANDiscoverable = true
		};

		string gamestatePath = Path.Join(Application.persistentDataPath, "ImpTest", "TestGame");
		DeleteFolder(gamestatePath);

		Debug.Log("Creating local game server");

		GameServer = GameStateServer.Create(gamestatePath, summary);

		yield return LocalConnectionTest();

		/*
		TCPServer = new ImpunityTCPServer(GameServer, options);

		TCPServer.Start();

		yield return new WaitForSeconds(1.0f);

		Finder = new ImpunityTCPServerFinder(options, OnServerFound);

		FoundServer = false;
		Finder.Start();

		yield return new WaitForSeconds(0.2f);

		if (!FoundServer)
		{
			Debug.LogError("Didn't find server, quitting");
			yield break;
		}

		Finder.Dispose();
		Finder = null;

		Debug.Log("Connecting client");
		TCPClient = ImpunityTCPClient.MakeTCPClient(ServerEndpoint, options);
		TCPClient.Connect();

		yield return new WaitForSeconds(0.2f);

		TCPClient.SendGuaranteedMessage(new byte[10], 0, 10);

		yield return new WaitForSeconds(0.2f);

		TCPClient.SendGuaranteedMessage(new byte[10], 0, 10);

		yield return new WaitForSeconds(0.2f);

		Debug.Log("Shutting down client");

		TCPClient.Dispose();

		yield return new WaitForSeconds(0.2f);

		Debug.Log("Shutting down server");

		TCPServer.Dispose();
		TCPServer = null;
		*/


		GameServer.Dispose();
		GameServer = null;



	}

	IEnumerator LocalConnectionTest()
	{
		Debug.Log("Running local connection test");

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

		LocalGame = new LocalGameConnection(GameServer);

		Debug.Log("Calling EnsureFormat");
		ImpunityYield ensureFormat = LocalGame.EnsureFormat(CurrFormat);
		yield return ensureFormat;
		if (ensureFormat.Error != null)
		{
			Debug.LogError(ensureFormat.Error.Message);
			yield return null;
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
			yield return null;
		}

		Debug.Log("Calling FindDocumentById");
		ImpunityYield<BsonDocument> findAction = LocalGame.FindDocumentById(1, "char1");
		yield return findAction;
		if (findAction.Error != null)
		{
			Debug.LogError(findAction.Error.Message);
			yield return null;
		}

		Debug.Log("Found character " + (string)(findAction.Value["name"]));

		LocalGame.Dispose();
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
		Finder?.Update();
		LocalGame?.Update();
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
