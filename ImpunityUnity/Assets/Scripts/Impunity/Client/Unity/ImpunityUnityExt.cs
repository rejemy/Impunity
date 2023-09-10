using System;
using System.Collections.Generic;

using UnityEngine;

using UltraLiteDB;

using Impunity.GameState;
using Impunity.Connection;

namespace Impunity.Unity
{

	public class ImpunityYield : CustomYieldInstruction
	{
		private bool Complete = false;

		public ImpunityError Error { get; private set; }

		public override bool keepWaiting
		{
			get
			{
				return !Complete;
			}
		}

		public void OnComplete(ImpunityError err)
		{
			Error = err;
			Complete = true;
		}
	}

	public class ImpunityYield<TReturn> : CustomYieldInstruction
	{
		private bool Complete = false;
		public TReturn Value { get; private set; }
		public ImpunityError Error { get; private set; }

		public override bool keepWaiting
		{
			get
			{
				return !Complete;
			}
		}

		public void OnComplete(ImpunityError err, TReturn returnValue)
		{
			Value = returnValue;
			Error = err;
			Complete = true;
		}
	}

	public static class ConnectionYieldExtensions
	{
		// ---------- API

		public static ImpunityYield<List<ActionResult>> CompoundActionYield(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var action = new ImpunityYield<List<ActionResult>>();
			connection.CompoundAction(actions, action.OnComplete);
			return action;
		}

		public static ImpunityYield ConnectYield(this BaseGameConnection connection)
		{
			var action = new ImpunityYield();
			connection.Connect(action.OnComplete);
			return action;
		}

		// -------- DB actions

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

		public static ImpunityYield<bool> TryToLockYield(this BaseGameConnection connection, string lockName, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLock(lockName, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UnlockYield(this BaseGameConnection connection, string lockName, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.Unlock(lockName, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> CreateChannelYield(this BaseGameConnection connection, int entityTypeId, byte instanceFlags, string channelName, ArraySegment<byte> propBytes)
		{
			var action = new ImpunityYield<uint>();
			connection.CreateChannel(entityTypeId, instanceFlags, channelName, propBytes, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> CreateObjectYield(this BaseGameConnection connection, int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes)
		{
			var action = new ImpunityYield<uint>();
			connection.CreateObject(entityTypeId, instanceFlags, channelId, propBytes, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateEntityYield(this BaseGameConnection connection, uint entityId, string key, ArraySegment<byte> updateData)
		{
			var action = new ImpunityYield<bool>();
			connection.UpdateEntity(entityId, key, updateData, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteEntityYield(this BaseGameConnection connection, uint entityId, string key, BsonValue deleteData)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteEntity(entityId, key, deleteData, action.OnComplete);
			return action;
		}

		public static ImpunityYield TriggerEntityEventYield(this BaseGameConnection connection, uint entityId, int eventType, BsonValue eventData)
		{
			var action = new ImpunityYield();
			connection.TriggerEntityEvent(entityId, eventType, eventData, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> TryToLockEntityYield(this BaseGameConnection connection, uint entityId, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLockEntity(entityId, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UnlockEntityYield(this BaseGameConnection connection, uint entityId, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.UnlockEntity(entityId, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> SubcribeToChannelYield(this BaseGameConnection connection, string channelName)
		{
			var action = new ImpunityYield<uint>();
			connection.SubcribeToChannel(channelName, action.OnComplete);
			return action;
		}

		public static ImpunityYield UnsubscribeFromChannelYield(this BaseGameConnection connection, uint channelId)
		{
			var action = new ImpunityYield();
			connection.UnsubscribeFromChannel(channelId, action.OnComplete);
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
		public static ImpunityYield<T> CreateChannelYield<T>(this ClientEntityManager manager, T channel, string name) where T : class, IDistributedChannel
		{
			var t = new ImpunityYield<T>();
			manager.CreateChannel<T>(channel, name, t.OnComplete);
			return t;
		}

		public static ImpunityYield<T> CreateObjectYield<T>(this ClientEntityManager manager, T obj, IDistributedChannel channel) where T : class, IDistributedEntity
		{
			var t = new ImpunityYield<T>();
			manager.CreateObject<T>(obj, channel, t.OnComplete);
			return t;
		}

		public static ImpunityYield<T> SubscribeToChannelYield<T>(this ClientEntityManager manager, string channelName) where T : class, IDistributedChannel
        {
			var t = new ImpunityYield<T>();
			manager.SubscribeToChannel<T>(channelName, t.OnComplete);
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

		public static ImpunityYield<bool> LockYield(this IDistributedEntity entity, string key)
		{
			var t = new ImpunityYield<bool>();
			entity.Lock(key, t.OnComplete);
			return t;
		}

		public static ImpunityYield<bool> UnlockYield(this IDistributedEntity entity, string key)
		{
			var t = new ImpunityYield<bool>();
			entity.Unlock(key, t.OnComplete);
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