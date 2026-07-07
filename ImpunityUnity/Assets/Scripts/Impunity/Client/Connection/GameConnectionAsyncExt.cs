using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Connection
{
	/// <summary>Bridges callback-based Impunity APIs to Task-based async/await by wrapping a <see cref="TaskCompletionSource{T}"/>.</summary>
	internal class ImpunityTaskCompletionSource : TaskCompletionSource<bool>
	{
		/// <summary>Callback handler that completes the task, setting an exception on error or a result on success.</summary>
		public void OnComplete(ImpunityErrorResponse? err)
		{
			if (err != null)
			{
				SetException(new ImpuntyErrorResponseException(err));
			}
			else
			{
				SetResult(true);
			}
		}

	}

	/// <summary>Generic version of <see cref="ImpunityTaskCompletionSource"/> for APIs that return a typed result.</summary>
	internal class ImpunityTaskCompletionSource<TResult> : TaskCompletionSource<TResult>
	{
		/// <summary>Callback handler that completes the task with the result, or sets an exception on error.</summary>
		public void OnComplete(ImpunityErrorResponse? err, TResult result)
		{
			if (err != null)
			{
				SetException(new ImpuntyErrorResponseException(err));
			}
			else
			{
				SetResult(result);
			}
		}
	}


	/// <summary>
	/// Async/await extension methods for <see cref="BaseGameConnection"/>. Each method wraps
	/// the corresponding callback-based API, throwing <see cref="ImpuntyErrorResponseException"/> on failure.
	/// </summary>
	public static class ConnectionAsyncExtensions
	{

		// ---------- API

		/// <summary>Connects to the game server asynchronously.</summary>
		public static Task ConnectAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.Connect(t.OnComplete);
			return t.Task;
		}


		// -------- DB actions

		public static Task<List<ActionResult>> CompoundDatabaseActionAsync(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var t = new ImpunityTaskCompletionSource<List<ActionResult>>();
			connection.CompoundDatabaseAction(actions, t.OnComplete);
			return t.Task;
		}

		public static Task SetGameSummaryAsync(this BaseGameConnection connection, BsonDocument summary)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.SetGameSummary(summary, t.OnComplete);
			return t.Task;
		}

		public static Task<BsonDocument> GetGameSummaryAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<BsonDocument>();
			connection.GetGameSummary(t.OnComplete);
			return t.Task;
		}

		// -------- Data migration (see docs/guides/SchemaMigration.md)

		/// <summary>
		/// Connects, and if the server offers a data migration, asks <paramref name="shouldMigrate"/> whether to proceed
		/// (e.g. show the user a dialog). On yes it runs <paramref name="migrate"/> and commits; on no it declines and
		/// leaves the world untouched. When no migration is needed this is just a connect. The decision always goes
		/// through your callback — a migration is never run automatically.
		/// </summary>
		public static async Task EnsureFormatAsync(this BaseGameConnection connection, Func<MigrationRequest, Task<bool>> shouldMigrate, Func<MigrationContext, Task> migrate)
		{
			await connection.ConnectAsync();

			MigrationRequest? request = connection.PendingMigration;
			if (request == null)
			{
				return;
			}

			bool proceed = await shouldMigrate(request);
			if (proceed)
			{
				await connection.RunMigrationAsync(migrate);
			}
			else
			{
				await connection.DeclineMigrationAsync();
			}
		}

		/// <summary>
		/// Runs an offered migration: begins it (snapshotting the world), invokes <paramref name="migrate"/> with a
		/// <see cref="MigrationContext"/>, then commits. If the delegate throws, the migration is aborted (rolling the
		/// world back) and the exception is rethrown. Requires <see cref="BaseGameConnection.PendingMigration"/> to be set.
		/// </summary>
		public static async Task RunMigrationAsync(this BaseGameConnection connection, Func<MigrationContext, Task> migrate)
		{
			MigrationRequest? request = connection.PendingMigration;
			if (request == null)
			{
				throw new InvalidOperationException("No migration was offered for this connection (check PendingMigration after connecting)");
			}

			await connection.BeginMigrationAsync();

			MigrationContext context = new MigrationContext(connection, request.FromVersion, request.ToVersion);
			try
			{
				await migrate(context);
			}
			catch
			{
				try { await connection.AbortMigrationAsync(); }
				catch { /* best effort; the server also rolls back if we disconnect */ }
				throw;
			}

			await connection.CommitMigrationAsync();
			connection.PendingMigration = null;
		}

		/// <summary>Declines an offered migration, releasing the world's reservation.</summary>
		public static async Task DeclineMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.DeclineMigration(t.OnComplete);
			await t.Task;
			connection.PendingMigration = null;
		}

		public static Task BeginMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.BeginMigration(t.OnComplete);
			return t.Task;
		}

		public static Task CommitMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.CommitMigration(t.OnComplete);
			return t.Task;
		}

		public static Task AbortMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.AbortMigration(t.OnComplete);
			return t.Task;
		}

		public static Task<List<string>> MigrationGetCollectionsAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<List<string>>();
			connection.MigrationGetCollections(t.OnComplete);
			return t.Task;
		}

		public static Task<List<BsonDocument>> MigrationScanAsync(this BaseGameConnection connection, string collectionName, int skip, int limit)
		{
			var t = new ImpunityTaskCompletionSource<List<BsonDocument>>();
			connection.MigrationScan(collectionName, skip, limit, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> MigrationWriteAsync(this BaseGameConnection connection, string collectionName, MigrationWriteOp op, BsonDocument? doc, BsonValue? id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.MigrationWrite(collectionName, op, doc, id, t.OnComplete);
			return t.Task;
		}

		public static Task<BsonValue> InsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<BsonValue>();
			connection.InsertDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UpdateDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpdateDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UpsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpsertDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> MergeIntoDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.MergeIntoDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> MergeInsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.MergeInsertDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		public static Task<BsonDocument> FindDocumentByIdAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<BsonDocument>();
			connection.FindDocumentById(collectionId, id, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> DeleteDocumentAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.DeleteDocument(collectionId, id, t.OnComplete);
			return t.Task;
		}

		public static Task<List<BsonDocument>> ListDocumentsAsync(this BaseGameConnection connection, int collectionId)
		{
			var t = new ImpunityTaskCompletionSource<List<BsonDocument>>();
			connection.ListDocuments(collectionId, t.OnComplete);
			return t.Task;
		}

		// -------- Live game

		public static Task<bool> TryToLockAsync(this BaseGameConnection connection, string lockName)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.TryToLock(lockName, t.OnComplete);
			return t.Task;
		}

		public static Task<LockWaitResult> WaitForLockAsync(this BaseGameConnection connection, string lockName)
		{
			var t = new ImpunityTaskCompletionSource<LockWaitResult>();
			connection.WaitForLock(lockName, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UnlockAsync(this BaseGameConnection connection, string lockName)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.Unlock(lockName, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> CreateChannelAsync(this BaseGameConnection connection, string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData> channelObjects)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.CreateChannel(channelName, entityTypeId, instanceFlags, propBytes, replace, channelObjects, t.OnComplete);
			return t.Task;
		}

		public static Task<uint> SubcribeToChannelAsync(this BaseGameConnection connection, string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData> channelObjects)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.SubcribeToChannel(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, channelObjects, t.OnComplete);
			return t.Task;
		}

		public static Task UnsubscribeFromChannelAsync(this BaseGameConnection connection, uint channelId)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.UnsubscribeFromChannel(channelId, t.OnComplete);
			return t.Task;
		}

		public static Task<uint> CreateObjectAsync(this BaseGameConnection connection, int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, bool replace)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.CreateObject(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, t.OnComplete);
			return t.Task;
		}

		public static Task UpdateEntityAsync(this BaseGameConnection connection, uint entityId, ArraySegment<byte> updateData, bool guaranteed, ushort seq = 0)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.UpdateEntity(entityId, updateData, guaranteed, seq, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> DeleteEntityAsync(this BaseGameConnection connection, uint entityId, BsonValue deleteData)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.DeleteEntity(entityId, deleteData, t.OnComplete);
			return t.Task;
		}

		public static Task TriggerEntityEventAsync(this BaseGameConnection connection, uint entityId, int eventType, BsonValue eventData)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.TriggerEntityEvent(entityId, eventType, eventData, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> TryToLockEntityAsync(this BaseGameConnection connection, uint entityId)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.TryToLockEntity(entityId, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UnlockEntityAsync(this BaseGameConnection connection, uint entityId)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UnlockEntity(entityId, t.OnComplete);
			return t.Task;
		}

		public static Task<List<string>> ListActiveChannels(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<List<string>>();
			connection.ListActiveChannels(t.OnComplete);
			return t.Task;
		}

		public static Task<List<string>> ListPersistedChannels(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<List<string>>();
			connection.ListPersistedChannels(t.OnComplete);
			return t.Task;
		}

	}

	/// <summary>Async/await extension methods for <see cref="GameStateDBCollection{DTYPE}"/>.</summary>
	public static class GameStateDBCollectionAsyncExtensions
	{

		public static Task<BsonValue> InsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<BsonValue>();
			collection.InsertDocument(doc, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UpdateDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.UpdateDocument(doc, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UpsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.UpsertDocument(doc, t.OnComplete);
			return t.Task;
		}

		public static Task<DTYPE> FindDocumentByIdAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<DTYPE>();
			collection.FindDocumentById(id, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> DeleteDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.DeleteDocument(id, t.OnComplete);
			return t.Task;
		}

		public static Task<List<DTYPE>?> ListDocumentsAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection)
		{
			var t = new ImpunityTaskCompletionSource<List<DTYPE>?>();
			collection.ListDocuments(t.OnComplete);
			return t.Task;
		}
	}

	/// <summary>Async/await extension methods for <see cref="ClientEntityManager"/>.</summary>
	public static class ClientEntityManagerAsyncExtensions
	{

		public static Task<T> CreateObjectAsync<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel, bool replace) where T : class, IDistributedObject
		{
			var t = new ImpunityTaskCompletionSource<T>();
			manager.CreateObject<T>(obj, channel, replace, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> CreateChannelAsync<T>(this ClientEntityManager manager, string channelName, T channel, bool replace, IEnumerable<IDistributedObject> channelObjects) where T : class, IDistributedChannel
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			manager.CreateChannel<T>(channelName, channel, replace, channelObjects, t.OnComplete);
			return t.Task;
		}

		public static Task<T> SubscribeToChannelAsync<T>(this ClientEntityManager manager, string channelName, T createIfNeeded) where T : class, IDistributedChannel
		{
			var t = new ImpunityTaskCompletionSource<T>();
			manager.SubscribeToChannel<T>(channelName, createIfNeeded, t.OnComplete);
			return t.Task;
		}

		public static Task UnsubscribeFromChannelAsync(this ClientEntityManager manager, IDistributedChannel channel)
		{
			var t = new ImpunityTaskCompletionSource();
			manager.UnsubscribeFromChannel(channel, t.OnComplete);
			return t.Task;
		}
	}

	/// <summary>Async/await extension methods for <see cref="IDistributedEntity"/>.</summary>
	public static class IDistributedEntityAsyncExtensions
	{
		public static Task TriggerEventAsync(this IDistributedEntity entity, int eventType, BsonValue eventData)
		{
			var t = new ImpunityTaskCompletionSource();
			entity.TriggerEvent(eventType, eventData, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> DeleteAsync(this IDistributedEntity entity, BsonValue deleteData)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			entity.Delete(deleteData, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> TryLockAsync(this IDistributedEntity entity)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			entity.TryLock(t.OnComplete);
			return t.Task;
		}

		public static Task<LockWaitResult> WaitForLockAsync(this IDistributedEntity entity)
		{
			var t = new ImpunityTaskCompletionSource<LockWaitResult>();
			entity.WaitForLock(t.OnComplete);
			return t.Task;
		}

		public static Task<bool> UnlockAsync(this IDistributedEntity entity)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			entity.Unlock(t.OnComplete);
			return t.Task;
		}

	}

	/// <summary>Async/await extension methods for <see cref="IDistributedChannel"/>.</summary>
	public static class IDistributedChannelAsyncExtensions
	{
		public static Task UnsubscribeAsync(this IDistributedChannel channel, bool immediate = false)
		{
			var t = new ImpunityTaskCompletionSource();
			channel.Unsubscribe(t.OnComplete, immediate);
			return t.Task;
		}

	}

}
