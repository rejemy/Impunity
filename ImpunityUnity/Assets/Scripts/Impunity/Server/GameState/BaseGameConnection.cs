using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Connection
{

	public delegate void BroadcastMessageHandler(int messageType, BsonValue messageBody, string sender);



	public abstract class BaseGameConnection : IDisposable
	{
		protected ConcurrentQueue<GameStateActionBase> CompletedActions;

		public abstract void Connect(ImpunityCallback onComplete);

		public void Update()
		{
			while (CompletedActions.TryDequeue(out GameStateActionBase action))
			{
				try
				{
					if (action is ServerActionBase)
                    {
						((ServerActionBase)action).DoAction(this);
                    }
					else
                    {
						action.InvokeOnCompleteCallback();
					}
					
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in action results callback");
				}
			}

		}

		public abstract void Dispose();

		public abstract void DoAction(GameStateActionBase action);

		// ------- Server message handling

		protected void OnServerMessage(ServerActionBase action)
        {
			CompletedActions.Enqueue(action);
		}

		// Handler delegates
		public BroadcastMessageHandler OnBroadcastMessage {get; set;}


		// -------- API Calls

		public void CompoundAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete)
		{
			DoAction(new CompoundAction(actions, onComplete));
		}

		// -------- Server Setup

		public void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete)
		{
			DoAction(new SetGameSummaryAction(summary, onComplete));
		}

		public void GetGameSummary(ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new GetGameSummaryAction(onComplete));
		}

		public void EnsureFormat(GameStateFormat format, ImpunityCallback onComplete)
		{
			DoAction(new EnsureFormatAction(format, onComplete));
		}

		// -------- DB actions

		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			DoAction(new InsertDocumentAction(collectionId, doc, onComplete));
		}

		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateDocumentAction(collectionId, doc, onComplete));
		}

		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpsertDocumentAction(collectionId, doc, onComplete));
		}

		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new FindDocumentByIdAction(collectionId, id, onComplete));
		}

		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteDocumentAction(collectionId, id, onComplete));
		}

		public void ListDocuments(int collectionId, ImpunityCallback<List<BsonDocument>> onComplete)
		{
			DoAction(new ListDocumentsAction(collectionId, onComplete));
		}

		// -------- Live game

		public void TryToLock(string lockName, string key, ImpunityCallback<bool> onComplete)
        {
			DoAction(new LockNamedLockAction(lockName, key, onComplete));
        }

		public void Unlock(string lockName, string key, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockNamedLockAction(lockName, key, onComplete));
		}


		// -------- Broadcast

		public void SendBroadcastMessage(int messageType, BsonValue msgBody)
        {
			DoAction(new SendBroadcastMessageAction(messageType, msgBody));
        }

	}

}