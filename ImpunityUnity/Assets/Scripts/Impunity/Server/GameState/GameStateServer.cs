using System;
using System.Collections.Concurrent;
using System.Threading;
using UltraLiteDB;


namespace Impunity.GameState
{

	/// <summary>Server-side interface representing a connection to a client. Implemented by network proxy and local proxy.</summary>
	public interface IServerSideConnectionProxy
    {
		/// <summary>Unique identifier for this connection.</summary>
		string ConnectionId { get; }
		/// <summary>Client-provided key for reconnection.</summary>
		string ConnectionKey { get; }
		/// <summary>True for remote network connections, false for in-process local connections.</summary>
		bool IsRemote { get; }
		/// <summary>The replicant tracking state subscriptions for this connection.</summary>
		GameStateReplicant ConnectionReplicant { get; set; }
		/// <summary>Whether this connection supports UDP delivery.</summary>
		bool SupportsUnguaranteed { get; }

		/// <summary>Queues an action result to be sent back to the client.</summary>
		void ReportActionResult(GameStateActionBase action);
		/// <summary>Queues a server-originated message to be sent to the client.</summary>
		void SendMessageToClient(ServerActionBase message);

		/// <summary>Requests that this connection be closed.</summary>
		void CloseConnectionRequest();
	}

	/// <summary>Base class for actions that are only used internally on the server (not serialized over the network).</summary>
	public abstract class LocalGameStateAction : GameStateActionBase
	{
		public override bool IsDBOperation() { return false; }

		public override void DeserializeResults(ArraySegment<byte> messageBytes)
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

	/// <summary>Core server-side game state manager. Owns the database, live state, and two worker threads (DB and Live). Actions are queued and processed on the appropriate thread.</summary>
	public class GameStateServer
	{
		/// <summary>Unique identifier for this game world.</summary>
		public string GameId { get; private set; }
		/// <summary>SHA-256 hash of the game password, or null if no password.</summary>
		public string? GamePasswordHash { get; private set; }
		/// <summary>Server configuration options.</summary>
		public ImpunityOptions Options { get; private set; }

		/// <summary>The persistent database for this game world.</summary>
		public GameStateDB DB;
		internal GameStateLive Live;

		BsonDocument? Summary;
		GameMetadata Metadata;

		/// <summary>When true, new connections are rejected (e.g., during shutdown or maintenance).</summary>
		public bool NewConnectionsDisabled { get; private set; } = false;

		BlockingCollection<GameStateActionBase> DBActionQueue;
		Thread DBWorkerThread;

		BlockingCollection<GameStateActionBase> LiveActionQueue;
		Thread LiveWorkerThread;

		bool Running;

		ConcurrentDictionary<int, IGameStateListener> Listeners;

		private GameStateServer(string gameId, string? gamePassword, GameStateDB gameDatabase, ImpunityOptions? options)
		{
			Options = options ?? new ImpunityOptions();

			GameId = gameId;
			GamePasswordHash = gamePassword != null ? ImpunityUtil.HashPassword(gamePassword) : null;
			
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

		/// <summary>Opens an existing game world from the database at the given path.</summary>
		public static GameStateServer Open(string gameId, string gamePassword, string path, ImpunityOptions? options = null)
		{
			var db = GameStateDB.Open(path, options);
			if (db == null)
			{
				throw new Exception("Unable to open game "+ gameId);
			}
			return new GameStateServer(gameId, gamePassword, db, options);
		}

		/// <summary>Creates a new game world with the given summary.</summary>
		public static GameStateServer Create(string gameId, string gamePassword, string path, BsonDocument summary, ImpunityOptions? options = null)
		{
			var db = GameStateDB.Create(path, summary, options);
			if (db == null)
			{
				throw new Exception("Unable to create game "+ gameId);
			}
			return new GameStateServer(gameId, gamePassword, db, options);
		}

		/// <summary>Opens an existing game world or creates a new one.</summary>
		public static GameStateServer OpenOrCreate(string gameId, string gamePassword, string path, BsonDocument summary, ImpunityOptions? options = null)
		{
			var db = GameStateDB.OpenOrCreate(path, summary, options);
			if (db == null)
			{
				throw new Exception("Unable to open or create game "+ gameId);
			}
			return new GameStateServer(gameId, gamePassword, db, options);
		}

		public static long GetServerTime()
		{
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		public static void WriteGameSummary(string path, BsonDocument summary)
		{
			GameStateDB.WriteGameSummary(path, summary);
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

		/// <summary>Validates the client's format, upgrades if needed, and registers the connection. Called on the live thread. Throws on incompatibility.</summary>
		public EstablishConnectResult EstablishConnection(IServerSideConnectionProxy proxy, GameStateFormatData format)
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

			EstablishConnectResult result = new EstablishConnectResult();
			result.ConnectionId = proxy.ConnectionId;
			
			return result;
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
					ImpunityLogger.LogError("Exception in OnGameStateFormatChanged handler", e);
				}
			}
		}

		public GameMetadata GetGameMetadata()
		{
			return Metadata;
		}

		// Called from various threads
		public BsonDocument? GetGameSummary()
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
					ImpunityLogger.LogError("Exception in OnGameSummaryChanged handler", e);
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
			DB.Dispose();
		}

		private void ShutdownLive()
		{
			LiveActionQueue.Dispose();
		}

		private void DBWorkerThead()
		{
			while (Running)
			{
				GameStateActionBase action;

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


				action.Run(this);


				if (action.ResultsExpected)
				{
					try
					{
						action.Origin.ReportActionResult(action);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in game action onCompleteHandler", e);
					}
				}
				else
				{
					// Cleanup action
					action.Cleanup();

					if (action.CloseConnectionOnComplete)
					{
						action.Origin.CloseConnectionRequest();
					}
				}
			}
		}

		private void LiveWorkerThead()
		{
			while (Running)
			{
				GameStateActionBase action;

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
						ImpunityLogger.LogError("Exception in game action onCompleteHandler", e);
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
							ImpunityLogger.LogError("Exception in game action ReportActionResult", e);
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
						ImpunityLogger.LogError("Exception in game action ReportActionResult", e);
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
					ImpunityLogger.LogError("Exception in game action ReportActionResult", e);
				}
			}
			else
			{
				// Cleanup action
				action.Cleanup();
			}
		}


		/// <summary>Routes an action to the appropriate worker thread (DB or Live) based on <see cref="GameStateActionBase.IsDBOperation"/>. Immediate actions run inline.</summary>
		public void QueueAction(GameStateActionBase action)
        {
			if (action.IsImmediate())
			{
				
				action.Run(this);
				SendActionResults(action);
			}
			else if (action.IsDBOperation())
			{
				DBActionQueue.Add(action);
			}
			else
			{
				LiveActionQueue.Add(action);
			}
		}

		/// <summary>Queues a completed DB action back to the live thread for callback invocation.</summary>
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
			if (connectionProxy.ConnectionReplicant == null)
			{
				// IF we haven't even setup a replicant for this connection yet, don't send the action
				return;
			}
			ConnectionClosedAction action = new ConnectionClosedAction();
			action.Origin = connectionProxy;
			action.ResultsExpected = false;
			LiveActionQueue.Add(action);
		}


	}

}