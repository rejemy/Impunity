using System;
using System.Collections.Concurrent;
using System.Threading;

using UltraLiteDB;


namespace Impunity.GameState
{

	public class GameStateServer
	{
		GameStateLogic State;

		BlockingCollection<GameStateActionBase> ActionQueue;
		Thread WorkerThread;
		bool Running;

		private GameStateServer(GameStateLogic gameState)
		{
			State = gameState;

			ActionQueue = new BlockingCollection<GameStateActionBase>();

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

			ActionQueue.CompleteAdding();

			WorkerThread.Join();
		}

		private void Shutdown()
		{
			ActionQueue.Dispose();

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
				action.Run(State);

				try
                {
					action.OnCompleteHandler?.Invoke(action);
                }
				catch(Exception e)
                {
					ImpunityLogger.LogError(e, "Exception in game action onCompleteHandler");
                }
			}
		}


		public BsonDocument GetGameSummary()
		{
			return State.GetGameSummary();
		}

		public void AddListener(IGameStateListener listener)
        {
			State.AddListener(listener);
        }

		public void RemoveListener(IGameStateListener listener)
		{
			State.RemoveListener(listener);
		}

		public void QueueAction(GameStateActionBase action)
        {
			ActionQueue.Add(action);

		}

	}

}