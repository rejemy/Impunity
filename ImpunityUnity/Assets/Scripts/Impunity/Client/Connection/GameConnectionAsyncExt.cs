using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Connection
{
	internal class ImpunityTaskCompletionSource : TaskCompletionSource<bool>
	{
		public void OnComplete(ImpunityErrorResponse err)
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

	internal class ImpunityTaskCompletionSource<TResult> : TaskCompletionSource<TResult>
	{

		public void OnComplete(ImpunityErrorResponse err, TResult result)
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


	public static class ConnectionAsyncExtensions
	{

		// ---------- API

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

		/*
		public static Task EnsureFormatAsync(this BaseGameConnection connection, GameStateFormat format)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.EnsureFormat(format, t.CompleteTask);
			return t.Task;
		}
		*/

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

		public static Task<bool> UpdateEntityAsync(this BaseGameConnection connection, uint entityId, ArraySegment<byte> updateData)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpdateEntity(entityId, updateData, t.OnComplete);
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

	}

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

		public static Task<List<DTYPE>> ListDocumentsAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection)
		{
			var t = new ImpunityTaskCompletionSource<List<DTYPE>>();
			collection.ListDocuments(t.OnComplete);
			return t.Task;
		}
	}

	public static class ClientEntityManagerAsyncExtensions
	{

		public static Task<T> CreateObjectAsync<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel, bool replace) where T : class, IDistributedEntity
		{
			var t = new ImpunityTaskCompletionSource<T>();
			manager.CreateObject<T>(obj, channel, replace, t.OnComplete);
			return t.Task;
		}

		public static Task<bool> CreateChannelAsync<T>(this ClientEntityManager manager, string channelName, T channel, bool replace, IEnumerable<IDistributedEntity> channelObjects) where T : class, IDistributedChannel
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

	public static class IDistributedChannelAsyncExtensions
	{
		public static Task UnsubscribeAsync(this IDistributedChannel channel)
		{
			var t = new ImpunityTaskCompletionSource();
			channel.Unsubscribe(t.OnComplete);
			return t.Task;
		}

	}

}