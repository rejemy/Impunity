using System;
using System.Collections.Concurrent;
using System.Threading;

using UltraLiteDB;


namespace Impunity.GameState
{

	public interface IServerSideConnectionProxy
    {
		string ConnectionId { get; }
		GameStateReplicant ConnectionReplicant { get; set; }

		void ReportActionResult(GameStateActionBase action);
		void SendMessageToClient(ServerActionBase message);

		bool SupportsUnguaranteed();
		
	}

	// Action that originates from neither the server nor client
	public abstract class GameStateAction : GameStateActionBase
	{
		public override void DeserializeResults(BsonDocument resultBody)
		{
			throw new NotImplementedException();
		}

		public override ushort GetActionType()
		{
			throw new NotImplementedException();
		}

		public override ActionResult GetResult()
		{
			throw new NotImplementedException();
		}

		public override Type GetResultType()
		{
			throw new NotImplementedException();
		}

		public override bool HasCallback()
		{
			return false;
		}

		public override void InvokeOnCompleteCallback()
		{
			throw new NotImplementedException();
		}
	}

    public class ConnectionOpenedAction : GameStateAction
	{
		protected override void DoAction(GameStateLive livestate, GameStateDB db)
		{
			GameStateReplicant replicant = new GameStateReplicant(Origin);
			Origin.ConnectionReplicant = replicant;

			livestate.AddGameStateReplicant(replicant);
		}
	}

	public class ConnectionClosedAction : GameStateAction
	{
		protected override void DoAction(GameStateLive livestate, GameStateDB db)
		{
			GameStateReplicant replicant = Origin.ConnectionReplicant;
			replicant.Cleanup();
			Origin.ConnectionReplicant = null;
		}
	}


	public class GameStateServer
	{
		GameStateDB GameDatabase;
		GameStateLive GameEntities;

		BlockingCollection<GameStateActionBase> ActionQueue;
		Thread WorkerThread;
		bool Running;

		private GameStateServer(GameStateDB gameDatabase)
		{
			GameDatabase = gameDatabase;
			GameEntities = new GameStateLive(GameDatabase);

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

		// Called by connection threads
		public BsonDocument GetGameSummary()
		{
			return GameDatabase.GetGameSummary();
		}

		// Called by connection threads
		internal void AddListener(IGameStateListener listener)
        {
			GameDatabase.AddListener(listener);
        }

		// Called by connection threads
		internal void RemoveListener(IGameStateListener listener)
		{
			GameDatabase.RemoveListener(listener);
		}

		public void QueueAction(GameStateActionBase action)
        {
			ActionQueue.Add(action);

		}

		// Called by connection threads
		public void ConnectionOpened(IServerSideConnectionProxy connectionProxy)
        {
			ConnectionOpenedAction action = new ConnectionOpenedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			ActionQueue.Add(action);
		}

		// Called by connection threads
		public void ConnectionClosed(IServerSideConnectionProxy connectionProxy)
		{
			ConnectionClosedAction action = new ConnectionClosedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			ActionQueue.Add(action);
		}


	}

}