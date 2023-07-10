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
			ImpunityYield<List<ActionResult>> action = new ImpunityYield<List<ActionResult>>();
			connection.CompoundAction(actions, action.OnComplete);
			return action;
		}

		public static ImpunityYield Connect(this BaseGameConnection connection)
		{
			ImpunityYield action = new ImpunityYield();
			connection.Connect(action.OnComplete);
			return action;
		}

		// -------- DB actions

		public static ImpunityYield SetGameSummary(this BaseGameConnection connection, BsonDocument summary)
		{
			ImpunityYield action = new ImpunityYield();
			connection.SetGameSummary(summary, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> GetGameSummary(this BaseGameConnection connection)
		{
			ImpunityYield<BsonDocument> action = new ImpunityYield<BsonDocument>();
			connection.GetGameSummary(action.OnComplete);
			return action;
		}

		public static ImpunityYield EnsureFormat(this BaseGameConnection connection, GameStateFormat format)
		{
			ImpunityYield action = new ImpunityYield();
			connection.EnsureFormat(format, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonValue> InsertDocument(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			ImpunityYield<BsonValue> action = new ImpunityYield<BsonValue>();
			connection.InsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateDocument(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.UpdateDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpsertDocument(this BaseGameConnection connection, int collectionId, BsonDocument doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.UpsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> FindDocumentById(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			ImpunityYield<BsonDocument> action = new ImpunityYield<BsonDocument>();
			connection.FindDocumentById(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteDocument(this BaseGameConnection connection, int collectionId, BsonValue id)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.DeleteDocument(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<List<BsonDocument>> ListDocuments(this BaseGameConnection connection, int collectionId)
		{
			ImpunityYield<List<BsonDocument>> action = new ImpunityYield<List<BsonDocument>>();
			connection.ListDocuments(collectionId, action.OnComplete);
			return action;
		}

		// -------- Live game

		public static ImpunityYield<bool> TryToLock(this BaseGameConnection connection, string lockName, string key)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.TryToLock(lockName, key, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> Unlock(this BaseGameConnection connection, string lockName, string key)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.Unlock(lockName, key, action.OnComplete);
			return action;
		}

	}

}