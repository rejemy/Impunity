using UnityEngine;

using UltraLiteDB;


using Impunity.Connection;


namespace Impunity.Unity
{

	public class ImpunityYield : CustomYieldInstruction, ImpunityResult
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

	public class ImpunityYield<TReturn> : CustomYieldInstruction, ImpunityResult<TReturn>
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
		public static ImpunityYield Connect(this IGameStateConnection connection)
		{
			ImpunityYield action = new ImpunityYield();
			connection.Connect(action.OnComplete);
			return action;
		}

		public static ImpunityYield SetGameSummary(this IGameStateConnection connection, BsonDocument summary)
		{
			ImpunityYield action = new ImpunityYield();
			connection.SetGameSummary(summary, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> GetSummary(this IGameStateConnection connection)
		{
			ImpunityYield<BsonDocument> action = new ImpunityYield<BsonDocument>();
			connection.GetSummary(action.OnComplete);
			return action;
		}

		public static ImpunityYield EnsureFormat(this IGameStateConnection connection, GameStateFormat format)
		{
			ImpunityYield action = new ImpunityYield();
			connection.EnsureFormat(format, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonValue> InsertDocument(this IGameStateConnection connection, int collectionId, BsonDocument doc)
		{
			ImpunityYield<BsonValue> action = new ImpunityYield<BsonValue>();
			connection.InsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpdateDocument(this IGameStateConnection connection, int collectionId, BsonDocument doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.UpdateDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> UpsertDocument(this IGameStateConnection connection, int collectionId, BsonDocument doc)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.UpsertDocument(collectionId, doc, action.OnComplete);
			return action;
		}

		public static ImpunityYield<BsonDocument> FindDocumentById(this IGameStateConnection connection, int collectionId, BsonValue id)
		{
			ImpunityYield<BsonDocument> action = new ImpunityYield<BsonDocument>();
			connection.FindDocumentById(collectionId, id, action.OnComplete);
			return action;
		}

		public static ImpunityYield<bool> DeleteDocument(this IGameStateConnection connection, int collectionId, BsonValue id)
		{
			ImpunityYield<bool> action = new ImpunityYield<bool>();
			connection.DeleteDocument(collectionId, id);
			return action;
		}

	}

}