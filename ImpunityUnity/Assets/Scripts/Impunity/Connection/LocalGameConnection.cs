using System;
using System.Collections.Concurrent;

using Impunity.GameState;

using UltraLiteDB;


namespace Impunity.Connection
{

	public class LocalGameConnection : IGameStateConnection, IGameStateResultHandler, IDisposable
	{
		GameStateServer State;
		ConcurrentQueue<IImpunityAction> PendingCallbacks;

		public LocalGameConnection(GameStateServer gameState, ImpunityOptions options = null)
		{
			State = gameState;
			PendingCallbacks = new ConcurrentQueue<IImpunityAction>();
		}

		public void Connect()
		{
			// No-op for local connections
		}

		public void Update()
		{
			while (PendingCallbacks.TryDequeue(out IImpunityAction action))
			{
				try
				{
					action.InvokeResultsCallback();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in gamestate callback");
				}
			}
		}

		public void Dispose()
		{

		}

		// Called on background thread
		public void OnActionComplete(IImpunityAction action)
		{
			PendingCallbacks.Enqueue(action);
		}

		// -------- API Calls

		public void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete)
		{
			State.SetGameSummary(this, summary, onComplete);
		}

		public void GetSummary(ImpunityCallback<BsonDocument> onComplete)
		{
			State.GetSummary(this, onComplete);
		}

		public void EnsureFormat(GameStateFormat format, ImpunityCallback onComplete)
		{
			State.EnsureFormat(this, format, onComplete);
		}

		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			State.InsertDocument(this, collectionId, doc, onComplete);
		}

		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			State.UpdateDocument(this, collectionId, doc, onComplete);
		}

		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			State.UpsertDocument(this, collectionId, doc, onComplete);
		}

		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			State.FindDocumentById(this, collectionId, id, onComplete);
		}

		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			State.DeleteDocument(this, collectionId, id, onComplete);
		}
	}

}