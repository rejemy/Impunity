using System;
using System.Collections.Concurrent;
using System.Threading;

using UltraLiteDB;

using Impunity.Networking;

namespace Impunity.GameState
{
	public interface IGameStateResultHandler
	{
		void OnActionComplete(IImpunityAction action);
	}


	public interface IImpunityAction
	{
		void InvokeAction();

		void InvokeResultsCallback();

		bool HasResult();
		BsonDocument GetResult();
		ImpunityError GetError();
	}

	public class GameStateServer
	{
		protected class VoidActionRecord : IImpunityAction
		{
			IGameStateResultHandler ReportTo;

			Action Work;
			ImpunityError Err;

			ImpunityCallback ResultsCallback;

			public VoidActionRecord(IGameStateResultHandler reportTo, ImpunityCallback onComplete, Action work)
			{
				ReportTo = reportTo;
				Work = work;
				ResultsCallback = onComplete;
			}

			public void InvokeAction()
			{
				try
				{
					Work();
				}
				catch (Exception e)
				{
					Err = new ImpunityError(e);
				}

				if (ResultsCallback != null)
				{
					OnActionComplete();
				}
			}

			public void OnActionComplete()
			{
				ReportTo.OnActionComplete(this);
			}

			public void InvokeResultsCallback()
			{
				ResultsCallback.Invoke(Err);
			}

			public bool HasResult() { return false; }

			public BsonDocument GetResult()
			{
				return null;
			}

			public ImpunityError GetError()
			{
				return Err;
			}
		}

		protected class ActionRecord<TResult> : IImpunityAction
		{
			IGameStateResultHandler ReportTo;
			Func<TResult> Work;

			TResult Result;
			ImpunityError Err;

			ImpunityCallback<TResult> ResultsCallback;

			public ActionRecord(IGameStateResultHandler reportTo, ImpunityCallback<TResult> onComplete, Func<TResult> work)
			{
				ReportTo = reportTo;
				Work = work;
				ResultsCallback = onComplete;
			}


			public void InvokeAction()
			{
				try
				{
					Result = Work();
				}
				catch (Exception e)
				{
					Err = new ImpunityError(e);
				}

				if (ResultsCallback != null)
				{
					OnActionComplete();
				}
			}

			public void OnActionComplete()
			{
				ReportTo.OnActionComplete(this);
			}

			public void InvokeResultsCallback()
			{
				ResultsCallback.Invoke(Err, Result);
			}


			public bool HasResult() { return true; }

			public BsonDocument GetResult()
			{
				return null;
			}

			public ImpunityError GetError()
			{
				return Err;
			}
		}

		GameStateLogic State;

		BlockingCollection<IImpunityAction> PendingActions;
		Thread WorkerThread;
		bool Running;

		private GameStateServer(GameStateLogic gameState)
		{
			State = gameState;

			PendingActions = new BlockingCollection<IImpunityAction>();

			Running = true;
			WorkerThread = new Thread(new ThreadStart(WorkThead));
			WorkerThread.IsBackground = false;
			WorkerThread.Name = "GameState work";
			WorkerThread.Start();
		}

		public static GameStateServer Open(string path, GameStateFormat format = null, string password = null)
		{
			return new GameStateServer(GameStateLogic.Open(path, format, password));
		}

		public static GameStateServer Create(string path, BsonDocument summary, GameStateFormat format = null, string password = null)
		{
			return new GameStateServer(GameStateLogic.Create(path, summary, format, password));
		}

		public void Dispose()
		{
			Running = false;
			PendingActions.CompleteAdding();

			WorkerThread.Join();
		}

		private void Shutdown()
		{
			PendingActions.Dispose();

			if (State != null)
			{
				State.Dispose();
				State = null;
			}
		}

		private void WorkThead()
		{
			while (Running)
			{
				IImpunityAction action = null;

				try
				{
					action = PendingActions.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					Shutdown();
					return;
				}

				// Invoke catches exceptions in the action
				action.InvokeAction();
			}
		}

		protected void CallGameState(IGameStateResultHandler handler, ImpunityCallback onComplete, Action call)
		{
			PendingActions.Add(new VoidActionRecord(handler, onComplete, call));
		}


		protected void CallGameState<TReturn>(IGameStateResultHandler handler, ImpunityCallback<TReturn> onComplete, Func<TReturn> call)
		{
			PendingActions.Add(new ActionRecord<TReturn>(handler, onComplete, call));
		}

		public BsonDocument GetSummary()
		{
			return State.GetSummary();
		}

		// -----------------------------

		public void SetGameSummary(IGameStateResultHandler handler, BsonDocument summary, ImpunityCallback onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				State.SetGameSummary(summary);
			}
			);
		}

		public void GetSummary(IGameStateResultHandler handler, ImpunityCallback<BsonDocument> onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				return State.GetSummary();
			}
			);
		}


		public void EnsureFormat(IGameStateResultHandler handler, GameStateFormat format, ImpunityCallback onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				State.EnsureFormat(format);
			}
			);
		}

		public void InsertDocument(IGameStateResultHandler handler, int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				return State.InsertDocument(collectionId, doc);
			}
			);
		}

		public void UpdateDocument(IGameStateResultHandler handler, int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				return State.UpdateDocument(collectionId, doc);
			}
			);
		}

		public void UpsertDocument(IGameStateResultHandler handler, int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				return State.UpsertDocument(collectionId, doc);
			}
			);
		}

		public void FindDocumentById(IGameStateResultHandler handler, int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			CallGameState(handler, onComplete, () =>
			{
				return State.FindDocumentById(collectionId, id);
			}
			);
		}

		public void DeleteDocument(IGameStateResultHandler handler, int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			CallGameState(handler, onComplete, () =>
			 {
				 return State.DeleteDocument(collectionId, id);
			 }
			);
		}
	}

}