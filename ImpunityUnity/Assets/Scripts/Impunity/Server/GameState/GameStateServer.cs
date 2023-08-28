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
		protected override void DoAction(GameStateServer game)
		{
			GameStateReplicant replicant = new GameStateReplicant(Origin);
			Origin.ConnectionReplicant = replicant;

			game.Live.AddGameStateReplicant(replicant);
		}
	}

	public class ConnectionClosedAction : GameStateAction
	{
		protected override void DoAction(GameStateServer game)
		{
			GameStateReplicant replicant = Origin.ConnectionReplicant;
			game.Live.RemoveGameStateReplicant(replicant);
			Origin.ConnectionReplicant = null;
		}
	}


	public class GameStateServer
	{
		public GameStateDB DB;
		internal GameStateLive Live;

		BsonDocument Summary;
		GameMetadata Metadata;

		BlockingCollection<GameStateActionBase> ActionQueue;
		Thread WorkerThread;
		bool Running;

		ConcurrentDictionary<int, IGameStateListener> Listeners;

		private GameStateServer(GameStateDB gameDatabase)
		{
			Listeners = new ConcurrentDictionary<int, IGameStateListener>();

			DB = gameDatabase;
			Live = new GameStateLive(DB);

			Summary = DB.LoadGameSummary();
			Metadata = DB.LoadMetadata();

			ActionQueue = new BlockingCollection<GameStateActionBase>();

			Running = true;
			WorkerThread = new Thread(new ThreadStart(WorkThead));
			WorkerThread.IsBackground = false;
			WorkerThread.Name = "GameState work";
			WorkerThread.Start();
		}

		public static GameStateServer Open(string path, string password = null)
		{
			return new GameStateServer(GameStateDB.Open(path, password));
		}

		public static GameStateServer Create(string path, BsonDocument summary, string password = null)
		{
			return new GameStateServer(GameStateDB.Create(path, summary, password));
		}

		// Called by connection threads
		internal void AddListener(IGameStateListener listener)
		{
			Listeners[listener.GetHashCode()] = listener;
		}

		// Called by connection threads
		internal void RemoveListener(IGameStateListener listener)
		{
			Listeners.TryRemove(listener.GetHashCode(), out _);
		}

		public void EnsureFormat(GameStateFormatData format)
		{
			if (format.Version == Metadata.Version && format.DataChecksum == Metadata.DataFormatChecksum)
			{
				return;
			}

			if (format.Version < Metadata.Version)
			{
				throw new Exception("Can't set savegame to earlier version");
			}

			DB.EnsureFormat(format);
			Live.EnsureFormat(format);

			Metadata.Version = format.Version;
			Metadata.DataFormatChecksum = format.DataChecksum;
			DB.SaveMetadata(Metadata);

		}

		public BsonDocument GetGameSummary()
        {
			return Summary;
        }

		public void SetGameSummary(BsonDocument summary)
		{
			Summary = summary;
			DB.SetGameSummary(summary);

			foreach (IGameStateListener listener in Listeners.Values)
			{
				try
				{
					listener.OnGameSummaryChanged(summary);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in OnGameSummaryChanged handler");
				}
			}
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

			if (DB != null)
			{
				DB.Dispose();
				DB = null;
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
				action.Run(this);

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


		public void QueueAction(GameStateActionBase action)
        {
			ActionQueue.Add(action);

		}

		// Called by connection threads
		internal void ConnectionOpened(IServerSideConnectionProxy connectionProxy)
        {
			ConnectionOpenedAction action = new ConnectionOpenedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			ActionQueue.Add(action);
		}

		// Called by connection threads
		internal void ConnectionClosed(IServerSideConnectionProxy connectionProxy)
		{
			ConnectionClosedAction action = new ConnectionClosedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			ActionQueue.Add(action);
		}


	}

}