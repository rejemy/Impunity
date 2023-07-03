using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;


namespace Impunity.GameState
{
	class GameMetadata
	{
		[BsonId]
		public string Id;
		[BsonField("Version")]
		public int Version;
	}

	class CollectionData
	{
		public string Name;
		public UltraLiteCollection<BsonDocument> Collection;
	}

	public class GameStateDB : IDisposable
	{
		private const string GameDBFile = "Game.db";
		private const string GameSummaryFile = "Summary.dat";
		private const string MetadataCollection = "_metadata";

		string RootDirectory;
		string DBFilename;
		BsonDocument Summary;
		UltraLiteDatabase GameDB;
		GameMetadata Metadata;
		CollectionData[] Collections;

		ConcurrentDictionary<int,IGameStateListener> Listeners;

		private GameStateDB(string path)
		{
			RootDirectory = path;
			DBFilename = Path.Combine(RootDirectory, GameDBFile);
			Listeners = new ConcurrentDictionary<int, IGameStateListener>();

		}

		public static GameStateDB Open(string path, GameStateFormat format = null, string password = null)
		{
			GameStateDB game = new GameStateDB(path);

			if (!File.Exists(game.DBFilename))
			{
				ImpunityLogger.LogError("Tried to open savegame that doesn't exist at: " + path);
				return null;
			}

			ImpunityLogger.LogInformation("Opening game database at " + game.DBFilename);

			game.Init(format, password);
			game.Summary = LoadSummary(path);

			return game;
		}

		public static GameStateDB Create(string path, BsonDocument summary, GameStateFormat format = null, string password = null)
		{
			GameStateDB game = new GameStateDB(path);

			if (File.Exists(game.DBFilename))
			{
				ImpunityLogger.LogError("Tried to create savegame where one already exists: " + path);
				return null;
			}

			ImpunityLogger.LogInformation("Creating game database at " + game.DBFilename);

			Directory.CreateDirectory(path);

			game.Init(format, password);

			game.SetGameSummary(summary);

			return game;
		}

		private void Init(GameStateFormat format, string password)
		{
			OpenDatabase(password);
			LoadMetadata();

			if (format != null)
			{
				EnsureFormat(format);
			}
		}

		private void OpenDatabase(string password)
		{
			if (GameDB != null)
				return;

			GameDB = new UltraLiteDatabase(
				new ConnectionString
				{
					Filename = DBFilename,
					Password = password,
					Flush = true
				},
				new BsonMapper(),
				new Logger(UltraLiteDB.Logger.ERROR, DatabaseLogger)
			);

		}

		private static void DatabaseLogger(string msg)
		{
			ImpunityLogger.LogError(msg);
		}

		public void Dispose()
		{
			if (GameDB != null)
			{
				GameDB.Dispose();
				GameDB = null;
			}
		}


		public static BsonDocument LoadSummary(string path)
		{
			string summaryFile = Path.Combine(path, GameSummaryFile);
			if (!File.Exists(summaryFile))
			{
				return null;
			}

			try
			{
				byte[] summaryBytes = File.ReadAllBytes(summaryFile);
				return BsonSerializer.Deserialize(summaryBytes);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error loading save game summary: " + e.Message);
				return null;
			}
		}


		private void LoadMetadata()
		{
			UltraLiteCollection<GameMetadata> metadataCollection = GameDB.GetCollection<GameMetadata>(MetadataCollection);
			Metadata = metadataCollection.FindById(MetadataCollection);
			if (Metadata == null)
			{
				Metadata = new GameMetadata() { Id = MetadataCollection, Version = 0 };
			}

		}

		private void SaveMetadata()
		{
			UltraLiteCollection<GameMetadata> metadataCollection = GameDB.GetCollection<GameMetadata>(MetadataCollection);
			metadataCollection.Upsert(Metadata);
		}


		public void AddListener(IGameStateListener listener)
		{
			Listeners[listener.GetHashCode()] = listener;
		}

		public void RemoveListener(IGameStateListener listener)
		{
			Listeners.TryRemove(listener.GetHashCode(), out _);
		}

		// ------------ API -----------------

		// NOTE - sometimes called from external thread
		public BsonDocument GetGameSummary()
		{
			return Summary;
		}


		public void SetGameSummary(BsonDocument summary)
		{
			Summary = summary;

			byte[] summaryBytes = BsonSerializer.Serialize(summary);
			string summaryFile = Path.Combine(RootDirectory, GameSummaryFile);
			File.WriteAllBytes(summaryFile, summaryBytes);

			foreach (IGameStateListener listener in Listeners.Values)
            {
				try
                {
					listener.OnGameSummaryChanged(summary);
				}
				catch(Exception e)
                {
					ImpunityLogger.LogError(e, "Exception in OnGameSummaryChanged handler");
                }
            }
		}


		public void EnsureFormat(GameStateFormat format)
		{
			if (format.Version == Metadata.Version)
			{
				return;
			}

			if (format.Version < Metadata.Version)
			{
				throw new Exception("Can't set savegame to earlier version");
			}

			if (format.Collections == null || format.Collections.Length <= 1)
			{
				return;
			}

			if (format.Collections[0] != null)
			{
				throw new Exception("Collection element 0 must be null");
			}

			Collections = new CollectionData[format.Collections.Length];
			for (int i = 1; i < format.Collections.Length; i++)
			{
				GameStateCollection collectionInfo = format.Collections[i];
				CollectionData collection = new CollectionData();
				collection.Name = collectionInfo.Name;
				collection.Collection = GameDB.GetCollection<BsonDocument>(collection.Name);
				Collections[i] = collection;
			}

			Metadata.Version = format.Version;
			SaveMetadata();

		}

		public BsonValue InsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new Exception("Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Insert(doc);
		}

		public bool UpdateDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new Exception("Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Update(doc);
		}

		public bool UpsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new Exception("Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Upsert(doc);
		}

		public BsonDocument FindDocumentById(int collectionId, BsonValue id)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new Exception("Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.FindById(id);
		}

		public bool DeleteDocument(int collectionId, BsonValue id)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new Exception("Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Delete(id);
		}

		public List<BsonDocument> ListDocuments(int collectionId)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new Exception("Invalid collection id: " + collectionId);
			}

			return new List<BsonDocument>(Collections[collectionId].Collection.FindAll());
		}
	}

}