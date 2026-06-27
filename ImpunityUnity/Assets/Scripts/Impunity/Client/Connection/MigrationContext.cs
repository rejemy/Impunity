using System.Collections.Generic;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Connection
{
	/// <summary>
	/// Describes a data migration the server has offered to a (higher-version) client: the world's current schema
	/// version and the version this client would migrate it to. Surfaced on <see cref="BaseGameConnection.PendingMigration"/>
	/// after connecting. See <c>docs/guides/SchemaMigration.md</c>.
	/// </summary>
	public class MigrationRequest
	{
		/// <summary>The world's current (pre-migration) schema version.</summary>
		public int FromVersion { get; }
		/// <summary>The schema version this client will migrate the world to.</summary>
		public int ToVersion { get; }

		public MigrationRequest(int fromVersion, int toVersion)
		{
			FromVersion = fromVersion;
			ToVersion = toVersion;
		}
	}

	/// <summary>
	/// A single persisted live entity, as stored in the reserved live-entities collection, surfaced for migration. An
	/// entity is one metadata row plus one row per persisted property; this groups them. Channels appear here too (a
	/// channel is an entity whose <see cref="EntityId"/> equals <see cref="Channel"/>). Property values are raw
	/// <see cref="BsonValue"/>s so migration code can reshape them even when the C# entity type has changed.
	/// </summary>
	public class MigrationEntityRow
	{
		/// <summary>The entity's unique id (its database key). For a channel this is the channel name.</summary>
		public string EntityId = null!;
		/// <summary>The channel this entity belongs to (its own name, for a channel).</summary>
		public string Channel = null!;
		/// <summary>The distributed entity type id (the persisted <c>t</c> value).</summary>
		public int TypeId;
		/// <summary>The instance flags (<see cref="ImpunityInstanceFlags"/>) the entity was created with.</summary>
		public byte Flags;
		/// <summary>The entity's persisted properties, keyed by persisted-property name, with raw BSON values.</summary>
		public Dictionary<string, BsonValue> Properties = new Dictionary<string, BsonValue>();

		public MigrationEntityRow() { }

		public MigrationEntityRow(string entityId)
		{
			EntityId = entityId;
		}
	}

	/// <summary>
	/// The toolkit handed to a migration delegate (see <see cref="ConnectionAsyncExtensions.RunMigrationAsync"/>). It
	/// exposes raw, name-addressed reads and writes against the world's collections — including the reserved
	/// live-entities collection via the <c>*Entity*</c> helpers — plus the version range being migrated. Because
	/// migration code runs against data written by an older build whose C# types may have changed, everything here works
	/// in raw <see cref="BsonDocument"/>/<see cref="BsonValue"/> terms rather than mapped objects.
	/// <para>
	/// Every method is async and completes when the connection's <see cref="BaseGameConnection.Update"/> next dispatches
	/// the reply, so keep pumping <c>Update()</c> while the migration runs (the same as any other connection call).
	/// </para>
	/// </summary>
	public class MigrationContext
	{
		private const int ScanPageSize = 200;

		private readonly BaseGameConnection Connection;

		/// <summary>The world's current (pre-migration) schema version.</summary>
		public int FromVersion { get; }
		/// <summary>The schema version being migrated to.</summary>
		public int ToVersion { get; }

		/// <summary>Creates a migration toolkit bound to a connection that is currently migrating. Normally constructed for you by <see cref="ConnectionAsyncExtensions.RunMigrationAsync"/>.</summary>
		/// <param name="connection">The connection performing the migration; all operations are routed through it.</param>
		/// <param name="fromVersion">The world's current (pre-migration) schema version.</param>
		/// <param name="toVersion">The schema version being migrated to.</param>
		public MigrationContext(BaseGameConnection connection, int fromVersion, int toVersion)
		{
			Connection = connection;
			FromVersion = fromVersion;
			ToVersion = toVersion;
		}

		/// <summary>The name of the reserved collection that stores persisted live entities.</summary>
		public static string EntitiesCollectionName { get { return GameStateDB.EntitiesCollectionName; } }

		// -------- Raw collection access (by name)

		/// <summary>Returns every collection name present in the world (including old/renamed collections and the live-entities collection).</summary>
		/// <returns>The list of collection names.</returns>
		public Task<List<string>> GetCollectionNamesAsync()
		{
			var t = new ImpunityTaskCompletionSource<List<string>>();
			Connection.MigrationGetCollections(t.OnComplete);
			return t.Task;
		}

		/// <summary>Reads one page of raw documents from a named collection.</summary>
		/// <param name="collectionName">The collection to read from.</param>
		/// <param name="skip">Number of documents to skip before this page.</param>
		/// <param name="limit">Maximum number of documents to return.</param>
		/// <returns>The page of raw documents (fewer than <paramref name="limit"/> when the end is reached).</returns>
		public Task<List<BsonDocument>> ScanPageAsync(string collectionName, int skip, int limit)
		{
			var t = new ImpunityTaskCompletionSource<List<BsonDocument>>();
			Connection.MigrationScan(collectionName, skip, limit, t.OnComplete);
			return t.Task;
		}

		/// <summary>Reads all raw documents from a named collection, paging internally to stay under the wire size.</summary>
		/// <param name="collectionName">The collection to read from.</param>
		/// <returns>Every document in the collection.</returns>
		public async Task<List<BsonDocument>> ListAsync(string collectionName)
		{
			List<BsonDocument> all = new List<BsonDocument>();
			int skip = 0;
			while (true)
			{
				List<BsonDocument> batch = await ScanPageAsync(collectionName, skip, ScanPageSize);
				if (batch == null || batch.Count == 0)
				{
					break;
				}
				all.AddRange(batch);
				if (batch.Count < ScanPageSize)
				{
					break;
				}
				skip += ScanPageSize;
			}
			return all;
		}

		/// <summary>Inserts a raw document into a named collection. The collection is created if it does not exist.</summary>
		/// <param name="collectionName">The collection to insert into.</param>
		/// <param name="doc">The document to insert. If it has no <c>_id</c>, one is assigned.</param>
		/// <returns>A task that completes when the insert has been applied.</returns>
		public Task InsertAsync(string collectionName, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			Connection.MigrationWrite(collectionName, MigrationWriteOp.Insert, doc, null, t.OnComplete);
			return t.Task;
		}

		/// <summary>Inserts or replaces a raw document (matched by <c>_id</c>) in a named collection.</summary>
		/// <param name="collectionName">The collection to write to.</param>
		/// <param name="doc">The document to insert or replace; its <c>_id</c> selects the row.</param>
		/// <returns><c>true</c> if the document was inserted as new, <c>false</c> if it replaced an existing one.</returns>
		public Task<bool> UpsertAsync(string collectionName, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			Connection.MigrationWrite(collectionName, MigrationWriteOp.Upsert, doc, null, t.OnComplete);
			return t.Task;
		}

		/// <summary>Replaces an existing raw document (matched by <c>_id</c>) in a named collection.</summary>
		/// <param name="collectionName">The collection to write to.</param>
		/// <param name="doc">The replacement document; its <c>_id</c> selects the row.</param>
		/// <returns><c>true</c> if a matching document existed and was replaced, <c>false</c> otherwise.</returns>
		public Task<bool> UpdateAsync(string collectionName, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			Connection.MigrationWrite(collectionName, MigrationWriteOp.Update, doc, null, t.OnComplete);
			return t.Task;
		}

		/// <summary>Deletes a raw document by <c>_id</c> from a named collection.</summary>
		/// <param name="collectionName">The collection to delete from.</param>
		/// <param name="id">The <c>_id</c> of the document to delete.</param>
		/// <returns><c>true</c> if a matching document was found and deleted, <c>false</c> otherwise.</returns>
		public Task<bool> DeleteAsync(string collectionName, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			Connection.MigrationWrite(collectionName, MigrationWriteOp.Delete, null, id, t.OnComplete);
			return t.Task;
		}

		// -------- Persisted live entities

		/// <summary>Reads every persisted live entity (and channel) in the world, grouped into <see cref="MigrationEntityRow"/>s.</summary>
		/// <returns>One <see cref="MigrationEntityRow"/> per persisted entity and channel, with their properties populated.</returns>
		public async Task<List<MigrationEntityRow>> ScanEntitiesAsync()
		{
			List<BsonDocument> rows = await ListAsync(EntitiesCollectionName);

			Dictionary<string, MigrationEntityRow> byId = new Dictionary<string, MigrationEntityRow>();

			foreach (BsonDocument doc in rows)
			{
				string rowId = doc["_id"].AsString;
				int sep = rowId.IndexOf('/');
				if (sep < 0)
				{
					// Entity metadata row
					MigrationEntityRow row = GetOrCreate(byId, rowId);
					row.Channel = doc["ch"].AsString;
					row.TypeId = doc["t"].AsInt32;
					row.Flags = (byte)doc["f"].AsInt32;
				}
				else
				{
					// Property row: _id == entityId/propertyName
					string entityId = rowId.Substring(0, sep);
					string propertyName = rowId.Substring(sep + 1);
					MigrationEntityRow row = GetOrCreate(byId, entityId);
					row.Properties[propertyName] = doc["v"];
				}
			}

			return new List<MigrationEntityRow>(byId.Values);
		}

		/// <summary>Writes (inserts or replaces) a persisted live entity: its metadata row and one row per property.</summary>
		/// <param name="row">The entity to write. Its <see cref="MigrationEntityRow.EntityId"/>, channel, type id, flags, and properties are all persisted.</param>
		/// <returns>A task that completes when the entity has been written.</returns>
		public async Task WriteEntityAsync(MigrationEntityRow row)
		{
			BsonDocument meta = new BsonDocument();
			meta["_id"] = row.EntityId;
			meta["ch"] = row.Channel;
			meta["t"] = row.TypeId;
			meta["f"] = (int)row.Flags;
			await UpsertAsync(EntitiesCollectionName, meta);

			foreach (KeyValuePair<string, BsonValue> prop in row.Properties)
			{
				BsonDocument propDoc = new BsonDocument();
				propDoc["_id"] = row.EntityId + "/" + prop.Key;
				propDoc["ch"] = row.Channel;
				propDoc["v"] = prop.Value;
				await UpsertAsync(EntitiesCollectionName, propDoc);
			}
		}

		/// <summary>Deletes a persisted live entity: its metadata row and all of its property rows (as listed on <paramref name="row"/>).</summary>
		/// <param name="row">The entity to delete; its property names determine which property rows are removed.</param>
		/// <returns>A task that completes when the entity has been deleted.</returns>
		public async Task DeleteEntityAsync(MigrationEntityRow row)
		{
			await DeleteAsync(EntitiesCollectionName, row.EntityId);
			foreach (KeyValuePair<string, BsonValue> prop in row.Properties)
			{
				await DeleteAsync(EntitiesCollectionName, row.EntityId + "/" + prop.Key);
			}
		}

		private static MigrationEntityRow GetOrCreate(Dictionary<string, MigrationEntityRow> map, string entityId)
		{
			if (!map.TryGetValue(entityId, out MigrationEntityRow row))
			{
				row = new MigrationEntityRow(entityId);
				map[entityId] = row;
			}
			return row;
		}
	}
}
