using System;
using System.Collections.Generic;

using UnityEngine;

using UltraLiteDB;

using Impunity.GameState;
using Impunity.Connection;
using UnityEditor.SearchService;

namespace Impunity.Unity
{

	public class ImpunityYield : CustomYieldInstruction
	{
		private bool Complete = false;

		public ImpunityErrorResponse Error { get; private set; }

		public override bool keepWaiting
		{
			get
			{
				return !Complete;
			}
		}

		public void OnComplete(ImpunityErrorResponse err)
		{
			Error = err;
			Complete = true;
		}
	}

	public class ImpunityYield<TReturn> : CustomYieldInstruction
	{
		private bool Complete = false;
		public TReturn Value { get; private set; }
		public ImpunityErrorResponse Error { get; private set; }

		public override bool keepWaiting
		{
			get
			{
				return !Complete;
			}
		}

		public void OnComplete(ImpunityErrorResponse err, TReturn returnValue)
		{
			Value = returnValue;
			Error = err;
			Complete = true;
		}
	}

	public static class ConnectionYieldExtensions
	{
		// ---------- API

		public static ImpunityYield ConnectYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield();
			connection.Connect(action.OnComplete);
			return action;
		}

		// -------- DB actions


		public static ImpunityYield<List<ActionResult>> CompoundDatabaseActionYield(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var action = new ImpunityYield<List<ActionResult>>();
			connection.CompoundDatabaseAction(actions, action.OnComplete);
			return action;
		}

		public static ImpunityYield SetGameSummaryYield(this BaseGameConnection connection, BsonDocument summary)
		{
			var action = new ImpunityYield();
			connection.SetGameSummary(summary, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> GetGameSummaryYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield<BsonDocument>();
			connection.GetGameSummary(action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonValue> InsertDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<BsonValue>();
			connection.InsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.UpdateDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpsertDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.UpsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> MergeIntoDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.MergeIntoDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> MergeInsertDocumentYield(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.MergeInsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> FindDocumentByIdYield(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var action = new ImpunityYield<BsonDocument>();
			connection.FindDocumentById(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteDocumentYield(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteDocument(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<List<BsonDocument>> ListDocumentsYield(this BaseGameConnection connection, int collectionId)
		{
			var action = new ImpunityYield<List<BsonDocument>>();
			connection.ListDocuments(collectionId, action.OnComplete);
			return action;
		}

		// -------- Live game

		public static ImpunityYield<bool> TryToLockYield(this BaseGameConnection connection, string lockName)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLock(lockName, action.OnComplete);
			return action;
		}

		public static ImpunityYield<LockWaitResult> WaitForLockYield(this BaseGameConnection connection, string lockName)
		{
			var action = new ImpunityYield<LockWaitResult>();
			connection.WaitForLock(lockName, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UnlockYield(this BaseGameConnection connection, string lockName)
		{
			var action = new ImpunityYield<bool>();
			connection.Unlock(lockName, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> CreateChannelYield(this BaseGameConnection connection, string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData> channelObjects)
		{
			var action = new ImpunityYield<bool>();
			connection.CreateChannel(channelName, entityTypeId, instanceFlags, propBytes, replace, channelObjects, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> SubcribeToChannelYield(this BaseGameConnection connection, string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData> channelObjects)
		{
			var action = new ImpunityYield<uint>();
			connection.SubcribeToChannel(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, channelObjects, action.OnComplete);
			return action;
		}

		public static ImpunityYield UnsubscribeFromChannelYield(this BaseGameConnection connection, uint channelId)
		{
			var action = new ImpunityYield();
			connection.UnsubscribeFromChannel(channelId, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> CreateObjectYield(this BaseGameConnection connection, int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, bool replace)
		{
			var action = new ImpunityYield<uint>();
			connection.CreateObject(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateEntityYield(this BaseGameConnection connection, uint entityId, ArraySegment<byte> updateData)
		{
			var action = new ImpunityYield<bool>();
			connection.UpdateEntity(entityId, updateData, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteEntityYield(this BaseGameConnection connection, uint entityId, BsonValue deleteData)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteEntity(entityId, deleteData, action.OnComplete);
			return action;
		}

		public static ImpunityYield TriggerEntityEventYield(this BaseGameConnection connection, uint entityId, int eventType, BsonValue eventData)
		{
			var action = new ImpunityYield();
			connection.TriggerEntityEvent(entityId, eventType, eventData, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> TryToLockEntityYield(this BaseGameConnection connection, uint entityId)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLockEntity(entityId, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UnlockEntityYield(this BaseGameConnection connection, uint entityId)
		{
			var action = new ImpunityYield<bool>();
			connection.UnlockEntity(entityId, action.OnComplete);
			return action;
		}

	}

	public static class GameStateDBCollectionYieldExtensions
	{

		public static ImpunityYield<BsonValue> InsertDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
        {
			ImpunityYield<BsonValue> action = new ImpunityYield<BsonValue>();
			collection.InsertDocument(doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.UpdateDocument(doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpsertDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.UpsertDocument(doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<DTYPE> FindDocumentByIdYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			ImpunityYield<DTYPE> action = new ImpunityYield<DTYPE>();
			collection.FindDocumentById(id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteDocumentYield<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.DeleteDocument(id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<List<DTYPE>> ListDocumentsYield<DTYPE>(this GameStateDBCollection<DTYPE> collection)
		{
			ImpunityYield<List<DTYPE>> action = new ImpunityYield<List<DTYPE>>();
			collection.ListDocuments(action.OnComplete);
			return action;
		}
	}

	public static class ClientEntityManagerYieldExtensions
	{
		public static ImpunityYield<T> CreateObjectYield<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel, bool replace) where T : class, IDistributedEntity
		{
			var t = new ImpunityYield<T>();
			manager.CreateObject<T>(obj, channel, replace, t.OnComplete);
			return t;
		}

		public static ImpunityYield<bool> CreateChannelYield<T>(this ClientEntityManager manager, string channelName, T channel, bool replace, IEnumerable<IDistributedEntity> channelObjects) where T : class, IDistributedChannel
		{
			var t = new ImpunityYield<bool>();
			manager.CreateChannel<T>(channelName, channel, replace, channelObjects, t.OnComplete);
			return t;
		}

		public static ImpunityYield<T> SubscribeToChannelYield<T>(this ClientEntityManager manager, string channelName, T createIfNeeded) where T : class, IDistributedChannel
        {
			var t = new ImpunityYield<T>();
			manager.SubscribeToChannel<T>(channelName, createIfNeeded, t.OnComplete);
			return t;
		}

		public static ImpunityYield UnsubscribeFromChannelYield(this ClientEntityManager manager, IDistributedChannel channel)
		{
			var t = new ImpunityYield();
			manager.UnsubscribeFromChannel(channel, t.OnComplete);
			return t;
		}
	}

	public static class IDistributedEntityYieldExtensions
	{
		public static ImpunityYield TriggerEventYield(this IDistributedEntity entity, int eventType, BsonValue eventData)
		{
			var t = new ImpunityYield();
			entity.TriggerEvent(eventType, eventData, t.OnComplete);
			return t;
		}

		public static ImpunityYield<bool> DeleteYield(this IDistributedEntity entity, BsonValue deleteData)
		{
			var t = new ImpunityYield<bool>();
			entity.Delete(deleteData, t.OnComplete);
			return t;
		}

		public static ImpunityYield<bool> TryLockYield(this IDistributedEntity entity)
		{
			var t = new ImpunityYield<bool>();
			entity.TryLock(t.OnComplete);
			return t;
		}

		public static ImpunityYield<LockWaitResult> WaitForLockYield(this IDistributedEntity entity)
		{
			var t = new ImpunityYield<LockWaitResult>();
			entity.WaitForLock(t.OnComplete);
			return t;
		}

		public static ImpunityYield<bool> UnlockYield(this IDistributedEntity entity)
		{
			var t = new ImpunityYield<bool>();
			entity.Unlock(t.OnComplete);
			return t;
		}

	}

	public static class IDistributedChannelYieldExtensions
	{
		public static ImpunityYield UnsubscribeYield(this IDistributedChannel channel)
		{
			var t = new ImpunityYield();
			channel.Unsubscribe(t.OnComplete);
			return t;
		}

	}
}