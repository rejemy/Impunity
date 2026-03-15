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

	/// <summary>Manages the persistent database for a game world. Wraps UltraLiteDB with collection management, entity storage, and game summary persistence.</summary>
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
			DBFilename = GetDBFilename(RootDirectory);

		}

		private static string GetDBFilename(string path)
		{
			return Path.Combine(path, GameDBFile);
		}

		/// <summary>Opens an existing game database at the given directory path. Returns null if the database file doesn't exist.</summary>
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

		/// <summary>Creates a new game database at the given path with an initial summary. Returns null if a database already exists there.</summary>
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

		/// <summary>Opens an existing database or creates a new one if none exists at the given path.</summary>
		public static GameStateDB OpenOrCreate(string path, BsonDocument summary, ImpunityOptions options = null)
		{
			if (File.Exists(GetDBFilename(path)))
			{
				return Open(path, options);
			}
			else
			{
				return Create(path, summary, options);
			}
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

		/// <summary>Writes a game summary BSON document to disk at the given directory path.</summary>
		public static void WriteGameSummary(string path, BsonDocument summary)
		{
			Directory.CreateDirectory(path);
			string summaryFile = Path.Combine(path, GameSummaryFile);
			byte[] summaryBytes = BsonSerializer.Serialize(summary);

			File.WriteAllBytes(summaryFile, summaryBytes);
		}


		/// <summary>Loads a game summary from disk. Returns null if no summary file exists.</summary>
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


		/// <summary>Loads game metadata (schema version, collections, entity types) from the database. Creates a default if none exists.</summary>
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

		/// <summary>Persists game metadata to the database.</summary>
		public void SaveMetadata(GameMetadata metadata)
		{
			UltraLiteCollection<GameMetadata> metadataCollection = GameDB.GetCollection<GameMetadata>(MetadataCollection);
			metadataCollection.Upsert(metadata);
		}

		

		// ------------ API -----------------


		/// <summary>Persists the game summary to disk. Called from the live thread.</summary>
		public void SetGameSummary(BsonDocument summary)
		{
			if (summary == null)
			{
				return;
			}
			byte[] summaryBytes = BsonSerializer.Serialize(summary);
			string summaryFile = Path.Combine(RootDirectory, GameSummaryFile);
			File.WriteAllBytes(summaryFile, summaryBytes);

		}


		/// <summary>Initializes the database collections based on the game state format. Must be called before any document operations.</summary>
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

		/// <summary>Inserts a document into the specified collection. Returns the document's _id.</summary>
		public BsonValue InsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}
			try
			{
				return Collections[collectionId].Collection.Insert(doc);
			}
			catch(UltraLiteException e)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Insert error: " + e.Message);
			}
		}

		/// <summary>Updates an existing document in the specified collection. Returns true if the document was found and updated.</summary>
		public bool UpdateDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Update(doc);
		}

		/// <summary>Inserts or updates a document in the specified collection.</summary>
		public bool UpsertDocument(int collectionId, BsonDocument doc)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Upsert(doc);
		}

		/// <summary>Merges fields from <paramref name="doc"/> into an existing document (by _id). Returns false if the document doesn't exist.</summary>
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

		/// <summary>Merges fields into an existing document, or inserts a new one if it doesn't exist.</summary>
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

		/// <summary>Finds a document by its _id in the specified collection. Returns null if not found.</summary>
		public BsonDocument FindDocumentById(int collectionId, BsonValue id)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.FindById(id);
		}

		/// <summary>Deletes a document by its _id. Returns true if the document was found and deleted.</summary>
		public bool DeleteDocument(int collectionId, BsonValue id)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return Collections[collectionId].Collection.Delete(id);
		}

		/// <summary>Returns all documents in the specified collection.</summary>
		public List<BsonDocument> ListDocuments(int collectionId)
		{
			if (collectionId <= 0 || collectionId >= Collections.Length)
			{
				throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Invalid collection id: " + collectionId);
			}

			return new List<BsonDocument>(Collections[collectionId].Collection.FindAll());
		}

		// Private API for use by the live entity system

		public bool DoesNamedEntityExistInDB(string entityId)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			return collection.Collection.Exists(Query.EQ("_id", entityId));
		}

		public void CreateLiveEntity(string entityId, string channelName, int entityType, byte instanceFlags, List<LiveEntityPersistedPropertyData> properties)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			BsonDocument entityDoc = new BsonDocument();
			entityDoc["_id"] = entityId;
			entityDoc["ch"] = channelName;
			entityDoc["t"] = entityType;
			entityDoc["f"] = (int)instanceFlags;

			collection.Collection.Upsert(entityDoc);
			if (properties != null)
			{
				foreach (LiveEntityPersistedPropertyData prop in properties)
				{
					BsonDocument propDoc = new BsonDocument();
					propDoc["_id"] = entityId + "/" + prop.PropertyName;
					propDoc["ch"] = channelName;
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

		public void DeleteLiveChannel(string channelName)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			collection.Collection.Delete(Query.EQ("ch", channelName));
		}

		public void DeleteLiveObject(string entityId)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			collection.Collection.Delete(Query.StartsWith("_id", entityId));
		}

		public LiveChannelData LoadChannelData(string channelName)
		{
			var collection = Collections[(int)ImpunityInternalCollectionIds.Entities];

			var propertyRows = collection.Collection.Find(Query.EQ("ch", channelName));
			if(propertyRows == null)
			{
				return null;
			}

			LiveChannelData channelData = null;
			Dictionary<string, LiveEntityData> loadedEntities = null;

			foreach(BsonDocument entDoc in propertyRows)
			{
				if (channelData == null)
				{
					channelData = new LiveChannelData(channelName);
					loadedEntities = new Dictionary<string, LiveEntityData>();
				}

				string rowId = entDoc["_id"].AsString;
				int sepIndex = rowId.IndexOf("/");
				if (sepIndex < 0)
				{
					// Entity metdata record

					LiveEntityData entData = null;
					if (rowId == channelName)
					{
						entData = channelData;
					}
					else
					{
						entData = loadedEntities.GetValueOrDefault(rowId);
					}

					if (entData == null)
					{
						entData = new LiveEntityData(rowId);
						loadedEntities[rowId] = entData;
					}

					entData.EntityType = entDoc["t"].AsInt32;
					entData.InstanceFlags = (byte)entDoc["f"].AsInt32;
				}
				else
				{
					// property record
					string entityId = rowId.Substring(0, sepIndex);
					string propertyName = rowId.Substring(sepIndex+1);

					LiveEntityData entData;
					if (entityId == channelName)
					{
						entData = channelData;
					}
					else
					{
						entData = loadedEntities.GetValueOrDefault(entityId);
					}

					if (entData == null)
					{
						entData = new LiveEntityData(entityId);
						loadedEntities[entityId] = entData;
					}

					if(entData.Properties == null)
					{
						entData.Properties = new List<LiveEntityPersistedPropertyData>();
					}

					entData.Properties.Add(new LiveEntityPersistedPropertyData(propertyName, entDoc["v"]));
				}
			}

			if (loadedEntities != null && loadedEntities.Count > 0)
			{
				channelData.ChannelObjects = new List<LiveEntityData>(loadedEntities.Values);
			}

			return channelData;
		}
	}

}