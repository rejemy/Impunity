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
				SetException(new ImpunityErrorResponseException(err));
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
				SetException(new ImpunityErrorResponseException(err));
			}
			else
			{
				SetResult(result);
			}
		}
	}


	/// <summary>
	/// Async/await extension methods for <see cref="BaseGameConnection"/>. Each method wraps
	/// the corresponding callback-based API, throwing <see cref="ImpunityErrorResponseException"/> on failure.
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

		/// <summary>Async/await wrapper for <c>BaseGameConnection.CompoundDatabaseAction</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<List<ActionResult>> CompoundDatabaseActionAsync(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var t = new ImpunityTaskCompletionSource<List<ActionResult>>();
			connection.CompoundDatabaseAction(actions, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.SetGameSummary</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task SetGameSummaryAsync(this BaseGameConnection connection, BsonDocument summary)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.SetGameSummary(summary, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.GetGameSummary</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
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

		/// <summary>Async/await wrapper for <c>BaseGameConnection.BeginMigration</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task BeginMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.BeginMigration(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.CommitMigration</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task CommitMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.CommitMigration(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.AbortMigration</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task AbortMigrationAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.AbortMigration(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.MigrationGetCollections</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<List<string>> MigrationGetCollectionsAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<List<string>>();
			connection.MigrationGetCollections(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.MigrationScan</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<List<BsonDocument>> MigrationScanAsync(this BaseGameConnection connection, string collectionName, int skip, int limit)
		{
			var t = new ImpunityTaskCompletionSource<List<BsonDocument>>();
			connection.MigrationScan(collectionName, skip, limit, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.MigrationWrite</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> MigrationWriteAsync(this BaseGameConnection connection, string collectionName, MigrationWriteOp op, BsonDocument? doc, BsonValue? id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.MigrationWrite(collectionName, op, doc, id, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.InsertDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<BsonValue> InsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<BsonValue>();
			connection.InsertDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.UpdateDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UpdateDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpdateDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.UpsertDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UpsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpsertDocument(collectionId, doc, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.MergeIntoDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> MergeIntoDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc, IEnumerable<string>? unsetKeys = null)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.MergeIntoDocument(collectionId, doc, t.OnComplete, unsetKeys);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.MergeInsertDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> MergeInsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc, IEnumerable<string>? unsetKeys = null)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.MergeInsertDocument(collectionId, doc, t.OnComplete, unsetKeys);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.FindDocumentById</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<BsonDocument?> FindDocumentByIdAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<BsonDocument?>();
			connection.FindDocumentById(collectionId, id, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.DeleteDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> DeleteDocumentAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.DeleteDocument(collectionId, id, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.ListDocuments</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<List<BsonDocument>> ListDocumentsAsync(this BaseGameConnection connection, int collectionId)
		{
			var t = new ImpunityTaskCompletionSource<List<BsonDocument>>();
			connection.ListDocuments(collectionId, t.OnComplete);
			return t.Task;
		}

		// -------- Live game

		/// <summary>Async/await wrapper for <c>BaseGameConnection.TryToLock</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> TryToLockAsync(this BaseGameConnection connection, string lockName)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.TryToLock(lockName, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.WaitForLock</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<LockWaitResult> WaitForLockAsync(this BaseGameConnection connection, string lockName)
		{
			var t = new ImpunityTaskCompletionSource<LockWaitResult>();
			connection.WaitForLock(lockName, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.Unlock</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UnlockAsync(this BaseGameConnection connection, string lockName)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.Unlock(lockName, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.CreateChannel</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> CreateChannelAsync(this BaseGameConnection connection, string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData> channelObjects)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.CreateChannel(channelName, entityTypeId, instanceFlags, propBytes, replace, channelObjects, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.SubcribeToChannel</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<uint> SubcribeToChannelAsync(this BaseGameConnection connection, string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData> channelObjects)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.SubcribeToChannel(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, channelObjects, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.UnsubscribeFromChannel</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task UnsubscribeFromChannelAsync(this BaseGameConnection connection, uint channelId)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.UnsubscribeFromChannel(channelId, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.CreateObject</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<uint> CreateObjectAsync(this BaseGameConnection connection, int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, bool replace, GameStateActionBase? onCreatedAction = null, uint replaceEntityId = 0, BsonValue? replaceData = null)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.CreateObject(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, t.OnComplete, onCreatedAction, replaceEntityId, replaceData);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.UpdateEntity</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task UpdateEntityAsync(this BaseGameConnection connection, uint entityId, ArraySegment<byte> updateData, bool guaranteed, uint seq = 0)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.UpdateEntity(entityId, updateData, guaranteed, seq, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.DeleteEntity</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> DeleteEntityAsync(this BaseGameConnection connection, uint entityId, BsonValue deleteData, GameStateActionBase? onDeletedAction = null)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.DeleteEntity(entityId, deleteData, t.OnComplete, onDeletedAction);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.TriggerEntityEvent</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task TriggerEntityEventAsync(this BaseGameConnection connection, uint entityId, int eventType, BsonValue eventData,
			EntityEventEcho echo = EntityEventEcho.IncludeSender)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.TriggerEntityEvent(entityId, eventType, eventData, echo, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.TryToLockEntity</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> TryToLockEntityAsync(this BaseGameConnection connection, uint entityId)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.TryToLockEntity(entityId, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>BaseGameConnection.UnlockEntity</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UnlockEntityAsync(this BaseGameConnection connection, uint entityId)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UnlockEntity(entityId, t.OnComplete);
			return t.Task;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.ListActiveChannels</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
		public static Task<List<string>> ListActiveChannels(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<List<string>>();
			connection.ListActiveChannels(t.OnComplete);
			return t.Task;
		}

		/// <summary>Coroutine wrapper for <c>BaseGameConnection.ListPersistedChannels</c>. Yield it, then check <c>Error</c> (null on success) and read the
		/// result from <c>Value</c>.</summary>
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

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.InsertDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<BsonValue> InsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<BsonValue>();
			collection.InsertDocument(doc, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.UpdateDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UpdateDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.UpdateDocument(doc, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.UpsertDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UpsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.UpsertDocument(doc, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.MergeIntoDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> MergeIntoDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonDocument patch, IEnumerable<string>? unsetKeys = null)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.MergeIntoDocument(patch, t.OnComplete, unsetKeys);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.MergeInsertDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> MergeInsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonDocument patch, IEnumerable<string>? unsetKeys = null)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.MergeInsertDocument(patch, t.OnComplete, unsetKeys);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.FindDocumentById</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<DTYPE?> FindDocumentByIdAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<DTYPE?>();
			collection.FindDocumentById(id, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.DeleteDocument</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> DeleteDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.DeleteDocument(id, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>GameStateDBCollection{DTYPE}.ListDocuments</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
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

		/// <summary>Async/await wrapper for <c>ClientEntityManager.CreateObject</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<T> CreateObjectAsync<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel, bool replace, GameStateActionBase? onCreatedAction = null, uint replaceEntityId = 0, FieldGuard replaceGuard = default, BsonValue? replaceData = null) where T : class, IDistributedObject
		{
			var t = new ImpunityTaskCompletionSource<T>();
			manager.CreateObject<T>(obj, channel, replace, t.OnComplete, onCreatedAction, replaceEntityId, replaceGuard, replaceData);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>ClientEntityManager.CreateChannel</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> CreateChannelAsync<T>(this ClientEntityManager manager, string channelName, T channel, bool replace, IEnumerable<IDistributedObject> channelObjects) where T : class, IDistributedChannel
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			manager.CreateChannel<T>(channelName, channel, replace, channelObjects, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>ClientEntityManager.SubscribeToChannel</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<T> SubscribeToChannelAsync<T>(this ClientEntityManager manager, string channelName, T createIfNeeded) where T : class, IDistributedChannel
		{
			var t = new ImpunityTaskCompletionSource<T>();
			manager.SubscribeToChannel<T>(channelName, createIfNeeded, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>ClientEntityManager.UnsubscribeFromChannel</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
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
		/// <summary>Async/await wrapper for <c>IDistributedEntity.TriggerEvent</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task TriggerEventAsync(this IDistributedEntity entity, int eventType, BsonValue eventData,
			EntityEventEcho echo = EntityEventEcho.IncludeSender)
		{
			var t = new ImpunityTaskCompletionSource();
			entity.TriggerEvent(eventType, eventData, echo, t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <see cref="IDistributedEntity.UpdateIfUnchanged"/>. Returns
		/// <see cref="GuardedResult.Applied"/> or <see cref="GuardedResult.Stale"/> (lost the race; nothing applied).
		/// The task faults with an <see cref="ImpunityErrorResponseException"/> only for
		/// <see cref="GuardedResult.Failed"/>: locked by another connection, entity gone, connection broken.</summary>
		public static Task<GuardedResult> UpdateIfUnchangedAsync(this IDistributedEntity entity, GameStateActionBase? onUpdatedAction = null)
		{
			var t = new ImpunityTaskCompletionSource<GuardedResult>();
			entity.UpdateIfUnchanged(t.OnComplete, onUpdatedAction);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>IDistributedEntity.Delete</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> DeleteAsync(this IDistributedEntity entity, BsonValue deleteData, GameStateActionBase? onDeletedAction = null)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			entity.Delete(deleteData, t.OnComplete, onDeletedAction);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <see cref="IDistributedEntity.DeleteIfUnchanged"/>. Returns
		/// <see cref="GuardedResult.Applied"/> (deleted) or <see cref="GuardedResult.Stale"/> (a guarded field changed
		/// first; nothing deleted). The task faults with an <see cref="ImpunityErrorResponseException"/> only for
		/// <see cref="GuardedResult.Failed"/>: locked by another connection, entity gone, connection broken.</summary>
		public static Task<GuardedResult> DeleteIfUnchangedAsync(this IDistributedEntity entity, FieldGuard guard, BsonValue? deleteData, GameStateActionBase? onDeletedAction = null)
		{
			var t = new ImpunityTaskCompletionSource<GuardedResult>();
			entity.DeleteIfUnchanged(guard, deleteData, t.OnComplete, onDeletedAction);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <see cref="IDistributedEntity.ReplaceIfUnchanged{T}"/>. Returns
		/// <see cref="GuardedResult.Applied"/> (replaced; <paramref name="newObject"/> is registered) or
		/// <see cref="GuardedResult.Stale"/> (a guarded field changed first; nothing replaced). The task faults with an
		/// <see cref="ImpunityErrorResponseException"/> only for <see cref="GuardedResult.Failed"/>.</summary>
		public static Task<GuardedResult> ReplaceIfUnchangedAsync<T>(this IDistributedEntity entity, FieldGuard guard, T newObject, BsonValue? replaceData = null, GameStateActionBase? onCreatedAction = null) where T : class, IDistributedObject
		{
			var t = new ImpunityTaskCompletionSource<GuardedResult>();
			entity.ReplaceIfUnchanged(guard, newObject, replaceData, t.OnComplete, onCreatedAction);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>IDistributedEntity.TryLock</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> TryLockAsync(this IDistributedEntity entity)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			entity.TryLock(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>IDistributedEntity.WaitForLock</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<LockWaitResult> WaitForLockAsync(this IDistributedEntity entity)
		{
			var t = new ImpunityTaskCompletionSource<LockWaitResult>();
			entity.WaitForLock(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <c>IDistributedEntity.Unlock</c>. The task completes with the request's result and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task<bool> UnlockAsync(this IDistributedEntity entity)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			entity.Unlock(t.OnComplete);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <see cref="IDistributedEntity.RunExclusive"/> with a synchronous body.</summary>
		/// <param name="entity">The entity to hold the lock on.</param>
		/// <param name="body">The logic to run under the lock.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock; negative uses the manager's default, zero does
		/// not wait. The body's own running time is never counted.</param>
		/// <returns>A task for the scope's outcome. It faults with <see cref="ImpunityErrorResponseException"/> only
		/// when the request itself failed; a body that throws completes the task with
		/// <see cref="RunExclusiveResult.Failed"/>.</returns>
		public static Task<RunExclusiveResult> RunExclusiveAsync(this IDistributedEntity entity, Action body, float timeoutSeconds = -1f)
		{
			var t = new ImpunityTaskCompletionSource<RunExclusiveResult>();
			entity.RunExclusive(body, t.OnComplete, timeoutSeconds);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <see cref="IDistributedEntity.Transact"/>. The body is synchronous and may run
		/// more than once (once per attempt), so it must not await anything or have side effects outside the entity.</summary>
		/// <param name="entity">The entity to change.</param>
		/// <param name="body">Reads and sets the entity's fields; returns true to commit, false to send nothing.</param>
		/// <param name="maxAttempts">Runs of the body before <see cref="TransactResult.Conflict"/>; negative uses the
		/// manager's default.</param>
		/// <returns>A task for the outcome: <see cref="TransactResult.Committed"/>, <see cref="TransactResult.Declined"/> or
		/// <see cref="TransactResult.Conflict"/>. It faults with <see cref="ImpunityErrorResponseException"/> when the
		/// outcome is <see cref="TransactResult.Failed"/> (the body threw, the entity went away, or the update failed).</returns>
		public static Task<TransactResult> TransactAsync(this IDistributedEntity entity, Func<TransactScope, bool> body, int maxAttempts = -1)
		{
			var t = new ImpunityTaskCompletionSource<TransactResult>();
			entity.Transact(body, t.OnComplete, maxAttempts);
			return t.Task;
		}

		/// <summary>Async/await wrapper for <see cref="IDistributedEntity.RunExclusive"/> with an awaitable body. The
		/// lock is held for as long as <paramref name="body"/> takes, so the scope may span many frames — keep it short
		/// or other clients will sit in the waiter queue.</summary>
		/// <param name="entity">The entity to hold the lock on.</param>
		/// <param name="body">The logic to run under the lock. Awaited to completion before the lock is released.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock; negative uses the manager's default, zero does
		/// not wait. The body's own running time is never counted.</param>
		public static Task<RunExclusiveResult> RunExclusiveAsync(this IDistributedEntity entity, Func<Task> body, float timeoutSeconds = -1f)
		{
			var t = new ImpunityTaskCompletionSource<RunExclusiveResult>();

			entity.Manager.RunExclusiveDeferred(entity, finish =>
			{
				Task running;
				try
				{
					running = body();
				}
				catch (Exception e)
				{
					// The body threw before returning a task.
					finish(e);
					return;
				}

				if (running == null)
				{
					finish(null);
					return;
				}

				// Run the continuation inline on whichever thread completed the task. Every Impunity async result is
				// completed from BaseGameConnection.Update(), so a body that only awaits Impunity operations (or
				// already-completed tasks) resumes here on the main thread — which is required, because this
				// releases the lock and touches entity state. Awaiting something that completes on a pool thread
				// would break that, exactly as ConfigureAwait(false) does elsewhere in this API.
				running.ContinueWith(completed =>
				{
					finish(completed.Exception?.GetBaseException());
				}, TaskContinuationOptions.ExecuteSynchronously);
			}, t.OnComplete, timeoutSeconds);

			return t.Task;
		}

	}

	/// <summary>Async/await extension methods for <see cref="IDistributedChannel"/>.</summary>
	public static class IDistributedChannelAsyncExtensions
	{
		/// <summary>Async/await wrapper for <c>IDistributedChannel.Unsubscribe</c>. The task completes when the request succeeds and faults with
		/// <see cref="ImpunityErrorResponseException"/> if it fails.</summary>
		public static Task UnsubscribeAsync(this IDistributedChannel channel, bool immediate = false)
		{
			var t = new ImpunityTaskCompletionSource();
			channel.Unsubscribe(t.OnComplete, immediate);
			return t.Task;
		}

	}

}
