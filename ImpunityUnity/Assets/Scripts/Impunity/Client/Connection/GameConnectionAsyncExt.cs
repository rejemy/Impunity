using System.Collections.Generic;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Connection
{
	internal class ImpunityTaskCompletionSource : TaskCompletionSource<bool>
	{
		public void CompleteTask(ImpunityError err)
		{
			if (err != null)
			{
				SetException(new ImpuntyErrorException(err));
			}
			else
			{
				SetResult(true);
			}
		}

	}

	internal class ImpunityTaskCompletionSource<TResult> : TaskCompletionSource<TResult>
	{

		public void CompleteTask(ImpunityError err, TResult result)
		{
			if (err != null)
			{
				SetException(new ImpuntyErrorException(err));
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
			connection.Connect(t.CompleteTask);
			return t.Task;
		}

		public static Task<List<ActionResult>> CompoundActionAsync(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var t = new ImpunityTaskCompletionSource<List<ActionResult>>();
			connection.CompoundAction(actions, t.CompleteTask);
			return t.Task;
		}

		// -------- DB actions

		public static Task SetGameSummaryAsync(this BaseGameConnection connection, BsonDocument summary)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.SetGameSummary(summary, t.CompleteTask);
			return t.Task;
		}

		public static Task<BsonDocument> GetGameSummaryAsync(this BaseGameConnection connection)
		{
			var t = new ImpunityTaskCompletionSource<BsonDocument>();
			connection.GetGameSummary(t.CompleteTask);
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
			connection.InsertDocument(collectionId, doc, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UpdateDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpdateDocument(collectionId, doc, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UpsertDocumentAsync(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpsertDocument(collectionId, doc, t.CompleteTask);
			return t.Task;
		}

		public static Task<BsonDocument> FindDocumentByIdAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<BsonDocument>();
			connection.FindDocumentById(collectionId, id, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> DeleteDocumentAsync(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.DeleteDocument(collectionId, id, t.CompleteTask);
			return t.Task;
		}

		public static Task<List<BsonDocument>> ListDocumentsAsync(this BaseGameConnection connection, int collectionId)
		{
			var t = new ImpunityTaskCompletionSource<List<BsonDocument>>();
			connection.ListDocuments(collectionId, t.CompleteTask);
			return t.Task;
		}

		// -------- Live game

		public static Task<bool> TryToLockAsync(this BaseGameConnection connection, string lockName, string key)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.TryToLock(lockName, key, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UnlockAsync(this BaseGameConnection connection, string lockName, string key)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.Unlock(lockName, key, t.CompleteTask);
			return t.Task;
		}

		public static Task<uint> CreateChannelAsync(this BaseGameConnection connection, int entityTypeId, string channelName, byte[] propBytes)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.CreateChannel(entityTypeId, channelName, propBytes, t.CompleteTask);
			return t.Task;
		}

		public static Task<uint> CreateObjectAsync(this BaseGameConnection connection, int entityTypeId, uint channelId, byte[] propBytes)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.CreateObject(entityTypeId, channelId, propBytes, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UpdateEntityAsync(this BaseGameConnection connection, uint entityId, string key, byte[] updateData)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UpdateEntity(entityId, key, updateData, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> DeleteEntityAsync(this BaseGameConnection connection, uint entityId, string key, BsonValue deleteData)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.DeleteEntity(entityId, key, deleteData, t.CompleteTask);
			return t.Task;
		}

		public static Task TriggerEntityEventAsync(this BaseGameConnection connection, uint entityId)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.TriggerEntityEvent(entityId, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> TryToLockEntityAsync(this BaseGameConnection connection, uint entityId, string key)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.TryToLockEntity(entityId, key, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UnlockEntityAsync(this BaseGameConnection connection, uint entityId, string key)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			connection.UnlockEntity(entityId, key, t.CompleteTask);
			return t.Task;
		}

		public static Task<uint> SubcribeToChannelAsync(this BaseGameConnection connection, string channelName)
		{
			var t = new ImpunityTaskCompletionSource<uint>();
			connection.SubcribeToChannel(channelName, t.CompleteTask);
			return t.Task;
		}

		public static Task UnsubscribeFromChannelAsync(this BaseGameConnection connection, uint channelId)
		{
			var t = new ImpunityTaskCompletionSource();
			connection.UnsubscribeFromChannel(channelId, t.CompleteTask);
			return t.Task;
		}

	}

	public static class GameStateDBCollectionAsyncExtensions
	{

		public static Task<BsonValue> InsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<BsonValue>();
			collection.InsertDocument(doc, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UpdateDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.UpdateDocument(doc, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> UpsertDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.UpsertDocument(doc, t.CompleteTask);
			return t.Task;
		}

		public static Task<DTYPE> FindDocumentByIdAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<DTYPE>();
			collection.FindDocumentById(id, t.CompleteTask);
			return t.Task;
		}

		public static Task<bool> DeleteDocumentAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			var t = new ImpunityTaskCompletionSource<bool>();
			collection.DeleteDocument(id, t.CompleteTask);
			return t.Task;
		}

		public static Task<List<DTYPE>> ListDocumentsAsync<DTYPE>(this GameStateDBCollection<DTYPE> collection)
		{
			var t = new ImpunityTaskCompletionSource<List<DTYPE>>();
			collection.ListDocuments(t.CompleteTask);
			return t.Task;
		}
	}

	public static class ClientEntityManagerAsyncExtensions
	{
		public static Task<T> CreateChannelAsync<T>(this ClientEntityManager manager, T channel, string name) where T : IDistributedChannel
        {
			var t = new ImpunityTaskCompletionSource<T>();
			manager.CreateChannel<T>(channel, name, t.CompleteTask);
			return t.Task;
        }

		public static Task<T> CreateObjectAsync<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel) where T : IDistributedEntity
		{
			var t = new ImpunityTaskCompletionSource<T>();
			manager.CreateObject<T>(obj, channel, t.CompleteTask);
			return t.Task;
		}
	}

}