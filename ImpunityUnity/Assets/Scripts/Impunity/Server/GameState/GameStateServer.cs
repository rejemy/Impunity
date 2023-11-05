using System;
using System.Collections.Concurrent;
using System.Threading;
using UltraLiteDB;


namespace Impunity.GameState
{

	public interface IServerSideConnectionProxy
    {
		string ConnectionId { get; }
		bool IsRemote { get; }
		GameStateReplicant ConnectionReplicant { get; set; }
		bool SupportsUnguaranteed();

		void ReportActionResult(GameStateActionBase action);
		void SendMessageToClient(ServerActionBase message);

		void CloseConnectionRequest();
	}

	// Actions that don't pass between client and server
	public abstract class LocalGameStateAction : GameStateActionBase
	{
		public override bool IsDBOperation() { return false; }

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

	// Tells the live game a connection opened
    public class ConnectionOpenedAction : LocalGameStateAction
	{
		protected override void DoAction(GameStateServer game)
		{
			GameStateReplicant replicant = new GameStateReplicant(Origin);
			Origin.ConnectionReplicant = replicant;

			game.Live.AddGameStateReplicant(replicant);
		}
	}

	// Tells the live game a connection closed
	public class ConnectionClosedAction : LocalGameStateAction
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
		public string GameId { get; private set; }
		public string GamePasswordHash { get; private set; }
		public ImpunityOptions Options { get; private set; }

		public GameStateDB DB;
		internal GameStateLive Live;

		BsonDocument Summary;
		GameMetadata Metadata;

		public bool NewConnectionsDisabled { get; private set; } = false;

		BlockingCollection<GameStateActionBase> DBActionQueue;
		Thread DBWorkerThread;

		BlockingCollection<GameStateActionBase> LiveActionQueue;
		Thread LiveWorkerThread;

		bool Running;

		ConcurrentDictionary<int, IGameStateListener> Listeners;

		private GameStateServer(string gameId, string gamePassword, GameStateDB gameDatabase, ImpunityOptions options)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}

			GameId = gameId;
			GamePasswordHash = ImpunityUtil.HashPassword(gamePassword);
			Options = options;
			Listeners = new ConcurrentDictionary<int, IGameStateListener>();

			DB = gameDatabase;
			Live = new GameStateLive(this);

			Summary = DB.LoadGameSummary();
			Metadata = DB.LoadMetadata();

			DB.SetFormat(Metadata.Collections);
			Live.SetFormat(Metadata.EntityTypes);

			Running = true;

			DBActionQueue = new BlockingCollection<GameStateActionBase>();

			DBWorkerThread = new Thread(new ThreadStart(DBWorkerThead));
			DBWorkerThread.IsBackground = false;
			DBWorkerThread.Name = "Database worker";
			DBWorkerThread.Start();

			LiveActionQueue = new BlockingCollection<GameStateActionBase>();

			LiveWorkerThread = new Thread(new ThreadStart(LiveWorkerThead));
			LiveWorkerThread.IsBackground = false;
			LiveWorkerThread.Name = "Live state worker";
			LiveWorkerThread.Start();
		}

		public static GameStateServer Open(string gameId, string gamePassword, string path, ImpunityOptions options = null)
		{
			return new GameStateServer(gameId, gamePassword, GameStateDB.Open(path, options), options);
		}

		public static GameStateServer Create(string gameId, string gamePassword, string path, BsonDocument summary, ImpunityOptions options = null)
		{
			return new GameStateServer(gameId, gamePassword, GameStateDB.Create(path, summary, options), options);
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

		// Called on live thread
		public void EstablishConnection(IServerSideConnectionProxy proxy, GameStateFormatData format)
		{
			if (NewConnectionsDisabled)
			{
				throw new ImpunityServerFatalException(ImpunityErrorCode.ServerUnavailable, "Server is busy, try again later");
			}

			if (!ValidateFormat(format))
			{
				if (Metadata.Version == 0)
				{
					UpdateFormat(format, proxy.IsRemote);
				}
				else if (Live.NumConnections == 0)
				{
					// We're the only connection, safe to update format
					UpdateFormat(format, proxy.IsRemote);
				}
				else
				{
					throw new ImpunityServerFatalException(ImpunityErrorCode.ServerVersionIncompatible, "Client version doesn't match server version");
				}
			}

			ConnectionOpened(proxy);
		}

		public bool ValidateFormat(GameStateFormatData format)
		{
			if (format.Version == Metadata.Version && format.DataChecksum == Metadata.DataFormatChecksum)
			{
				return true;
			}

			return false;
		}

		// Called on live thread
		public void UpdateFormat(GameStateFormatData format, bool isRemote)
		{
			if (format.Version == Metadata.Version && format.DataChecksum == Metadata.DataFormatChecksum)
			{
				return;
			}

			if (format.Version < Metadata.Version)
			{
				throw new ImpunityServerFatalException(ImpunityErrorCode.ActionBadRequest, "Can't revert savegame to earlier version");
			}

			if (format.Version > Metadata.Version || format.DataChecksum != Metadata.DataFormatChecksum)
			{
				if (isRemote && !Options.RemoteUpgradeAllowed)
				{
					throw new ImpunityServerFatalException(ImpunityErrorCode.ActionBadRequest, "Remote client cannot change game format version");
				}
			}

			Live.SetFormat(format.EntityTypes);

			Metadata.Version = format.Version;
			Metadata.DataFormatChecksum = format.DataChecksum;
			Metadata.Collections = format.Collections;
			Metadata.EntityTypes = format.EntityTypes;
			
			UpdateDBFormatAction dbUpdateAction = new UpdateDBFormatAction(format.Collections, Metadata);
			QueueAction(dbUpdateAction);

			foreach (IGameStateListener listener in Listeners.Values)
			{
				try
				{
					listener.OnGameMetadataChanged(this);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in OnGameStateFormatChanged handler");
				}
			}
		}

		public GameMetadata GetGameMetadata()
		{
			return Metadata;
		}

		// Called from various threads
		public BsonDocument GetGameSummary()
        {
			return Summary;
        }

		// Called on Live thread
		public void SetGameSummary(BsonDocument summary)
		{
			Summary = summary;
			DB.SetGameSummary(summary);

			foreach (IGameStateListener listener in Listeners.Values)
			{
				try
				{
					listener.OnGameSummaryChanged(this);
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

			DBActionQueue.CompleteAdding();
			LiveActionQueue.CompleteAdding();

			DBWorkerThread.Join();
			LiveWorkerThread.Join();
		}

		private void ShutdownDB()
		{
			DBActionQueue.Dispose();

			if (DB != null)
			{
				DB.Dispose();
				DB = null;
			}
		}

		private void ShutdownLive()
		{
			LiveActionQueue.Dispose();
		}

		private void DBWorkerThead()
		{
			while (Running)
			{
				GameStateActionBase action = null;

				try
				{
					action = DBActionQueue.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					ShutdownDB();
					return;
				}


				// Run catches non-fatal exceptions in the action
				bool gotFatalException = false;
				try
				{
					action.Run(this);
				}
				catch (ImpunityServerFatalException)
				{
					gotFatalException = true;
				}
				

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
				else
				{
					// Cleanup action
					action.Cleanup();
				}

				if (gotFatalException)
				{
					action.Origin.CloseConnectionRequest();
				}
			}
		}

		private void LiveWorkerThead()
		{
			while (Running)
			{
				GameStateActionBase action = null;

				try
				{
					action = LiveActionQueue.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					ShutdownLive();
					return;
				}

				if (action.IsDBOperation())
				{
					// If it's a DB operation in the live queue, it's actually a response.
					// TODO - make this simpler and not a weird special case

					try
					{
						action.InvokeOnCompleteCallback();
					}
					catch(Exception e)
					{
						ImpunityLogger.LogError(e, "Exception in game action onCompleteHandler");
					}
					continue;
				}

				// Run catches non-fatal exceptions in the action
				bool gotFatalException = false;
				try
				{
					action.Run(this);
				}
				catch (ImpunityServerFatalException)
				{
					gotFatalException = true;
				}

				if (action.ResultsExpected)
				{
					if (!action.AwaitingTask)
					{
						try
						{
							action.Origin.ReportActionResult(action);
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError(e, "Exception in game action ReportActionResult");
						}
					}
				}
				else
				{
					// Cleanup action
					action.Cleanup();
				}

				if (gotFatalException)
				{
					action.Origin.CloseConnectionRequest();
				}
			}
		}

		public void RunActionMethod(GameStateActionBase action, ServerActionMethod method)
		{
			// Run catches non-fatal exceptions in the action
			bool gotFatalException = false;
			try
			{
				action.RunWithMethod(this, method);
			}
			catch (ImpunityServerFatalException)
			{
				gotFatalException = true;
			}

			if (action.ResultsExpected)
			{
				if (!action.AwaitingTask)
				{
					try
					{
						action.Origin.ReportActionResult(action);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError(e, "Exception in game action ReportActionResult");
					}
				}
			}
			else
			{
				// Cleanup action
				action.Cleanup();
			}

			if (gotFatalException)
			{
				action.Origin.CloseConnectionRequest();
			}
		}

		public void SendActionResults(GameStateActionBase action)
		{
			if (action.ResultsExpected)
			{
				try
				{
					action.Origin.ReportActionResult(action);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in game action ReportActionResult");
				}
			}
			else
			{
				// Cleanup action
				action.Cleanup();
			}
		}


		public void QueueAction(GameStateActionBase action)
        {
			if (action.IsDBOperation())
			{
				DBActionQueue.Add(action);
			}
			else
			{
				LiveActionQueue.Add(action);
			}
		}

		public void QueueDBReply(GameStateActionBase action)
		{
			LiveActionQueue.Add(action);
		}

		// Called by connection threads
		internal void ConnectionOpened(IServerSideConnectionProxy connectionProxy)
        {
			ConnectionOpenedAction action = new ConnectionOpenedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			LiveActionQueue.Add(action);
		}

		// Called by connection threads
		internal void ConnectionClosed(IServerSideConnectionProxy connectionProxy)
		{
			if (connectionProxy == null)
			{
				throw new Exception("Null connection proxy in ConnectionClosed");
			}
			ConnectionClosedAction action = new ConnectionClosedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			LiveActionQueue.Add(action);
		}


	}

}