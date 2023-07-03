using System;
using System.Collections.Concurrent;
using System.Threading;

using UltraLiteDB;


namespace Impunity.GameState
{

	public interface IServerSideConnectionProxy
    {
		string ConnectionId { get; }

		void ReportActionResult(GameStateActionBase action);

		bool SupportsUnguaranteed();
		void SendGuaranteedMessage();
		void SendUnguaranteedMessage();
	}

	public class GameStateServer
	{
		GameStateDB GameDatabase;
		GameStateEntities GameEntities;

		BlockingCollection<GameStateActionBase> ActionQueue;
		Thread WorkerThread;
		bool Running;

		private GameStateServer(GameStateDB gameDatabase)
		{
			GameDatabase = gameDatabase;
			GameEntities = new GameStateEntities(GameDatabase);

			ActionQueue = new BlockingCollection<GameStateActionBase>();

			Running = true;
			WorkerThread = new Thread(new ThreadStart(WorkThead));
			WorkerThread.IsBackground = false;
			WorkerThread.Name = "GameState work";
			WorkerThread.Start();
		}

		public static GameStateServer Open(string path, GameStateFormat format = null, string password = null)
		{
			return new GameStateServer(GameStateDB.Open(path, format, password));
		}

		public static GameStateServer Create(string path, BsonDocument summary, GameStateFormat format = null, string password = null)
		{
			return new GameStateServer(GameStateDB.Create(path, summary, format, password));
		}

		public void Dispose()
		{
			Running = false;

			ActionQueue.CompleteAdding();

			WorkerThread.Join();
		}

		private void Shutdown()
		{
			ActionQueue.Dispose();

			if (GameDatabase != null)
			{
				GameDatabase.Dispose();
				GameDatabase = null;
			}
		}

		private void WorkThead()
		{
			while (Running)
			{
				GameStateActionBase action = null;

				try
				{
					action = ActionQueue.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					Shutdown();
					return;
				}

				// Run catches exceptions in the action
				action.Run(GameEntities, GameDatabase);

				if (action.ResultsExpected)
				{
					try
					{
						action.Origin.ReportActionResult(action);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError(e, "Exception in game action onCompleteHandler");
					}
				}
			}
		}


		public BsonDocument GetGameSummary()
		{
			return GameDatabase.GetGameSummary();
		}

		public void AddListener(IGameStateListener listener)
        {
			GameDatabase.AddListener(listener);
        }

		public void RemoveListener(IGameStateListener listener)
		{
			GameDatabase.RemoveListener(listener);
		}

		public void QueueAction(GameStateActionBase action)
        {
			ActionQueue.Add(action);

		}

	}

}