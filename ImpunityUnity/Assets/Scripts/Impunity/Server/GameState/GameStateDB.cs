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


		// Called from live thread
		public void SetGameSummary(BsonDocument summary)
		{
			byte[] summaryBytes = BsonSerializer.Serialize(summary);
			string summaryFile = Path.Combine(RootDirectory, GameSummaryFile);
			File.WriteAllBytes(summaryFile, summaryBytes);

		}


		public void SetFormat(GameStateCollection[] collections)
		{
			if (collections == null || collections.Length < 1)
			{
				return;
			}

			int highestIndex = collections[collections.Length - 1].Index;

			Collections = new CollectionData[highestIndex + 1];

			HashSet<string> collectionNames = new HashSet<string>();

			//CollectionData channelCollection = new CollectionData();
			//channelCollection.Name = "Channels";
			//channelCollection.Collection = GameDB.GetCollection<BsonDocument>(channelCollection.Name);
			//Collections[(int)ImpunityInternalCollectionIds.Channels] = channelCollection;
			//collectionNames.Add(channelCollection.Name);

			CollectionData entityCollection = new CollectionData();
			entityCollection.Name = "Entities";
			entityCollection.Collection = GameDB.GetCollection<BsonDocument>(entityCollection.Name);
			entityCollection.Collection.EnsureIndex("ch");
			Collections[(int)ImpunityInternalCollectionIds.Entities] = entityCollection;
			collectionNames.Add(entityCollection.Name);

			for (int i = 0; i < collections.Length; i++)
			{
				GameStateCollection collectionInfo = collections[i];
				if (collectionInfo.Index < 10)
				{
					throw new ImpunityServerException(ImpunityErrorCode.ActionInvalidParameter, "Collection can't have index less than 10");
				}

				if (collectionNames.Contains(collectionInfo.Name))
				{
					throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Duplicate collection name: " + collectionInfo.Name);
				}

				CollectionData collection = new CollectionData();
				collection.Name = collectionInfo.Name;
				collection.Collection = GameDB.GetCollection<BsonDocument>(collection.Name);
				Collections[collectionInfo.Index] = collection;
				collectionNames.Add(collection.Name);
				
			}
		}

		public BsonValue InsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Insert(doc);
		}

		public bool UpdateDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Update(doc);
		}

		public bool UpsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Upsert(doc);
		}

		public bool MergeIntoDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			var collection = Collections[collectionId].Collection;
			var existing = collection.FindById(doc["_id"]);
			if (existing == null)
			{
				return false;
			}

			foreach (var data in doc)
			{
				existing[data.Key] = data.Value;
			}

			return Collections[collectionId].Collection.Update(existing);
		}

		public bool MergeInsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			var collection = Collections[collectionId].Collection;
			var existing = collection.FindById(doc["_id"]);
			if (existing == null)
			{
				return Collections[collectionId].Collection.Upsert(doc);
			}

			foreach (var data in doc)
			{
				existing[data.Key] = data.Value;
			}

			return Collections[collectionId].Collection.Update(existing);
		}

		public BsonDocument FindDocumentById(int collectionId, BsonValue id)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.FindById(id);
		}

		public bool DeleteDocument(int collectionId, BsonValue id)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Delete(id);
		}

		public List<BsonDocument> ListDocuments(int collectionId)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return new List<BsonDocument>(Collections[collectionId].Collection.FindAll());
		}

		// Private API for use by the live entity system

		public void CreateLiveEntity(string entityId, string channelId, int entityType, byte instanceFlags, List<LiveEntityPersistedPropertyData> properties)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			BsonDocument entityDoc = new BsonDocument();
			entityDoc["_id"] = entityId;
			entityDoc["ch"] = channelId;
			entityDoc["t"] = entityType;
			entityDoc["f"] = (int)instanceFlags;

			collection.Collection.Upsert(entityDoc);
			if (properties != null)
			{
				foreach (LiveEntityPersistedPropertyData prop in properties)
				{
					BsonDocument propDoc = new BsonDocument();
					propDoc["_id"] = entityId + "/" + prop.PropertyName;
					propDoc["ch"] = channelId;
					propDoc["v"] = prop.PropertyValue;

					collection.Collection.Upsert(propDoc);
				}
			}
		}

		public void UpdateLiveEntityProperties(string entityId, string channelId, List<LiveEntityPersistedPropertyData> properties)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			foreach (LiveEntityPersistedPropertyData prop in properties)
			{
				BsonDocument propDoc = new BsonDocument();
				propDoc["_id"] = entityId + "/" + prop.PropertyName;
				propDoc["ch"] = channelId;
				propDoc["v"] = prop.PropertyValue;

				collection.Collection.Upsert(propDoc);
			}
		}

		public List<BsonDocument> ListChannelContents(string channelId)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			var data = collection.Collection.Find(Query.EQ("ch", channelId));

			return null;
		}
	}

}