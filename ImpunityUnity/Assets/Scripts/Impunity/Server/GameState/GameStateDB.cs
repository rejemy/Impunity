using System;
using System.IO;
using System.Collections.Generic;


using UltraLiteDB;


namespace Impunity.GameState
{

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
		
		UltraLiteDatabase GameDB;
		CollectionData[] Collections;

		

		private GameStateDB(string path)
		{
			RootDirectory = path;
			DBFilename = Path.Combine(RootDirectory, GameDBFile);

		}

		public static GameStateDB Open(string path, ImpunityOptions options = null)
		{
			GameStateDB game = new GameStateDB(path);

			if (!File.Exists(game.DBFilename))
			{
				ImpunityLogger.LogError("Tried to open savegame that doesn't exist at: " + path);
				return null;
			}

			ImpunityLogger.LogInformation("Opening game database at " + game.DBFilename);

			game.OpenDatabase(options);

			return game;
		}

		public static GameStateDB Create(string path, BsonDocument summary, ImpunityOptions options = null)
		{
			GameStateDB game = new GameStateDB(path);

			if (File.Exists(game.DBFilename))
			{
				ImpunityLogger.LogError("Tried to create savegame where one already exists: " + path);
				return null;
			}

			ImpunityLogger.LogInformation("Creating game database at " + game.DBFilename);

			Directory.CreateDirectory(path);

			game.OpenDatabase(options);

			game.SetGameSummary(summary);

			return game;
		}

		private void OpenDatabase(ImpunityOptions options)
		{
			if (GameDB != null)
				return;

			GameDB = new UltraLiteDatabase(
				new ConnectionString
				{
					Filename = DBFilename,
					Password = options?.DBPassword,
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


		public static BsonDocument LoadGameSummary(string path)
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

		public BsonDocument LoadGameSummary()
        {
			return LoadGameSummary(RootDirectory);
        }


		public GameMetadata LoadMetadata()
		{
			UltraLiteCollection<GameMetadata> metadataCollection = GameDB.GetCollection<GameMetadata>(MetadataCollection);
			GameMetadata metadata = metadataCollection.FindById(MetadataCollection);
			if (metadata == null)
			{
				metadata = new GameMetadata() { Id = MetadataCollection, Version = 0 };
			}

			return metadata;
		}

		public void SaveMetadata(GameMetadata metadata)
		{
			UltraLiteCollection<GameMetadata> metadataCollection = GameDB.GetCollection<GameMetadata>(MetadataCollection);
			metadataCollection.Upsert(metadata);
		}

		

		// ------------ API -----------------


		public void SetGameSummary(BsonDocument summary)
		{
			byte[] summaryBytes = BsonSerializer.Serialize(summary);
			string summaryFile = Path.Combine(RootDirectory, GameSummaryFile);
			File.WriteAllBytes(summaryFile, summaryBytes);

		}


		public void SetFormat(GameStateFormatData format)
		{
			if (format.Collections == null || format.Collections.Length < 1)
			{
				return;
			}

			int highestIndex = format.Collections[format.Collections.Length - 1].Index;

			Collections = new CollectionData[highestIndex + 1];
			for (int i = 0; i < format.Collections.Length; i++)
			{
				GameStateCollection collectionInfo = format.Collections[i];
				CollectionData collection = new CollectionData();
				collection.Name = collectionInfo.Name;
				collection.Collection = GameDB.GetCollection<BsonDocument>(collection.Name);
				Collections[collectionInfo.Index] = collection;
			}
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