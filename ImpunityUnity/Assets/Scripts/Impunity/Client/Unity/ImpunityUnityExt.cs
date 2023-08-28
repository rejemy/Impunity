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

	public static class ConnectionCoroutines
	{
		// ---------- API

		public static ImpunityYield<List<ActionResult>> CompoundAction(this BaseGameConnection connection, IEnumerable<GameStateActionBase> actions)
		{
			var action = new ImpunityYield<List<ActionResult>>();
			connection.CompoundAction(actions, action.OnComplete);
			return action;
		}

		public static ImpunityYield Connect(this BaseGameConnection connection)
		{
			var action = new ImpunityYield();
			connection.Connect(action.OnComplete);
			return action;
		}

		// -------- DB actions

		public static ImpunityYield SetGameSummary(this BaseGameConnection connection, BsonDocument summary)
		{
			var action = new ImpunityYield();
			connection.SetGameSummary(summary, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> GetGameSummary(this BaseGameConnection connection)
		{
			var action = new ImpunityYield<BsonDocument>();
			connection.GetGameSummary(action.OnComplete);
			return action;
		}

		/*
		public static ImpunityYield EnsureFormat(this BaseGameConnection connection, GameStateFormat format)
		{
			var action = new ImpunityYield();
			connection.EnsureFormat(format, action.OnComplete);
			return action;
		}
		*/

		public static ImpunityYield<BsonValue> InsertDocument(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<BsonValue>();
			connection.InsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateDocument(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.UpdateDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpsertDocument(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			var action = new ImpunityYield<bool>();
			connection.UpsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> FindDocumentById(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var action = new ImpunityYield<BsonDocument>();
			connection.FindDocumentById(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteDocument(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteDocument(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<List<BsonDocument>> ListDocuments(this BaseGameConnection connection, int collectionId)
		{
			var action = new ImpunityYield<List<BsonDocument>>();
			connection.ListDocuments(collectionId, action.OnComplete);
			return action;
		}

		// -------- Live game

		public static ImpunityYield<bool> TryToLock(this BaseGameConnection connection, string lockName, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLock(lockName, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> Unlock(this BaseGameConnection connection, string lockName, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.Unlock(lockName, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> CreateChannel(this BaseGameConnection connection, int entityTypeId, string channelName)
		{
			var action = new ImpunityYield<uint>();
			connection.CreateChannel(entityTypeId, channelName, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> CreateObject(this BaseGameConnection connection, int entityTypeId, uint channelId)
		{
			var action = new ImpunityYield<uint>();
			connection.CreateObject(entityTypeId, channelId, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateEntity(this BaseGameConnection connection, uint entityId, string key, byte[] updateData)
		{
			var action = new ImpunityYield<bool>();
			connection.UpdateEntity(entityId, key, updateData, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteEntity(this BaseGameConnection connection, uint entityId, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.DeleteEntity(entityId, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield TriggerEntityEvent(this BaseGameConnection connection, uint entityId)
		{
			var action = new ImpunityYield();
			connection.TriggerEntityEvent(entityId, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> TryToLockEntity(this BaseGameConnection connection, uint entityId, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.TryToLockEntity(entityId, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UnlockEntity(this BaseGameConnection connection, uint entityId, string key)
		{
			var action = new ImpunityYield<bool>();
			connection.UnlockEntity(entityId, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<uint> SubcribeToChannel(this BaseGameConnection connection, string channelName)
		{
			var action = new ImpunityYield<uint>();
			connection.SubcribeToChannel(channelName, action.OnComplete);
			return action;
		}

		public static ImpunityYield UnsubscribeFromChannel(this BaseGameConnection connection, uint channelId)
		{
			var action = new ImpunityYield();
			connection.UnsubscribeFromChannel(channelId, action.OnComplete);
			return action;
		}
	}

	public static class GameStateDBCollectionCoroutines
	{

		public static ImpunityYield<BsonValue> InsertDocument<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
        {
			ImpunityYield<BsonValue> action = new ImpunityYield<BsonValue>();
			collection.InsertDocument(doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateDocument<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.UpdateDocument(doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpsertDocument<DTYPE>(this GameStateDBCollection<DTYPE> collection, DTYPE doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.UpsertDocument(doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<DTYPE> FindDocumentById<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			ImpunityYield<DTYPE> action = new ImpunityYield<DTYPE>();
			collection.FindDocumentById(id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteDocument<DTYPE>(this GameStateDBCollection<DTYPE> collection, BsonValue id)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			collection.DeleteDocument(id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<List<DTYPE>> ListDocuments<DTYPE>(this GameStateDBCollection<DTYPE> collection)
		{
			ImpunityYield<List<DTYPE>> action = new ImpunityYield<List<DTYPE>>();
			collection.ListDocuments(action.OnComplete);
			return action;
		}
	}
}