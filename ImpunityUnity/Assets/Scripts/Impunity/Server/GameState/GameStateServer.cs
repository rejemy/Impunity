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
			// Idempotent: a local connection opens itself in Connect() and the server also opens it during the
			// handshake, so guard against creating a second (leaked) replicant for the same connection.
			if (Origin.ConnectionReplicant != null)
			{
				return;
			}

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
			// If the closing connection owns an offered/in-progress migration, release/roll it back.
			game.OnConnectionClosedForMigration(Origin);

			GameStateReplicant replicant = Origin.ConnectionReplicant;
			game.Live.RemoveGameStateReplicant(replicant);
			Origin.ConnectionReplicant = null!;
		}
	}

	/// <summary>The migration state of a game world. Only one connection may own a migration at a time.</summary>
	public enum MigrationPhase
	{
		/// <summary>No migration; the world is open to connections normally.</summary>
		None,
		/// <summary>A higher-version client has been offered a migration and the world is reserved for it. Nothing has been changed; the client must explicitly begin or decline.</summary>
		Offered,
		/// <summary>The owner has begun migrating; the database has been snapshotted and raw migration operations are allowed.</summary>
		Migrating
	}

	// Internal action queued by the migration idle timer to run a timeout check on the live thread.
	internal class MigrationTimeoutCheckAction : LocalGameStateAction
	{
		protected override void DoAction(GameStateServer game)
		{
			game.CheckMigrationTimeout();
		}
	}

	// Internal action queued by the idle-cleanup timer to reap idle channels on the live thread.
	internal class IdleChannelCleanupAction : LocalGameStateAction
	{
		protected override void DoAction(GameStateServer game)
		{
			game.CheckIdleChannels();
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

		// ----- Data migration state (see docs/guides/SchemaMigration.md). All fields guarded by MigrationLock.
		// Transitions happen on the live thread; the lock exists so the DB thread (owner checks / activity touches) and
		// the idle timer thread can read/update safely.
		private readonly object MigrationLock = new object();
		private MigrationPhase MigrationPhaseState = MigrationPhase.None;
		private string? MigrationOwnerId;
		private IServerSideConnectionProxy? MigrationOwnerProxy;
		private GameStateFormatData? MigrationTargetFormat;
		private int MigrationFromVersion;
		private int MigrationToVersion;
		private long MigrationLastActivityMillis;
		private bool MigrationAborting;
		private IServerSideConnectionProxy? MigrationAbortCloseProxy;
		private Timer? MigrationTimer;
		private Timer? IdleCleanupTimer;
		// Running total of channels reaped by the idle-cleanup pass. Written only on the live thread; int reads are atomic.
		private volatile int ChannelsReapedTotal;

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

			// If a migration was interrupted (server crash/kill mid-migration), recover before serving anyone.
			RecoverInterruptedMigration();

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

			StartIdleCleanupTimer();
		}

		/// <summary>Opens an existing game world from the database at the given path.</summary>
		public static GameStateServer Open(string gameId, string? gamePassword, string path, ImpunityOptions? options = null)
		{
			var db = GameStateDB.Open(path, options);
			if (db == null)
			{
				throw new Exception("Unable to open game " + gameId);
			}
			return new GameStateServer(gameId, gamePassword, db, options);
		}

		/// <summary>Creates a new game world with the given summary.</summary>
		public static GameStateServer Create(string gameId, string? gamePassword, string path, BsonDocument? summary, ImpunityOptions? options = null)
		{
			var db = GameStateDB.Create(path, summary, options);
			if (db == null)
			{
				throw new Exception("Unable to create game " + gameId);
			}
			return new GameStateServer(gameId, gamePassword, db, options);
		}

		/// <summary>Opens an existing game world or creates a new one.</summary>
		public static GameStateServer OpenOrCreate(string gameId, string? gamePassword, string path, BsonDocument? summary, ImpunityOptions? options = null)
		{
			var db = GameStateDB.OpenOrCreate(path, summary, options);
			if (db == null)
			{
				throw new Exception("Unable to open or create game " + gameId);
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

		/// <summary>Validates the client's format, upgrades or offers a migration if needed, and registers the connection. Called on the live thread. Throws on incompatibility.</summary>
		public EstablishConnectResult EstablishConnection(IServerSideConnectionProxy proxy, GameStateFormatData format)
		{
			if (NewConnectionsDisabled)
			{
				throw new ImpunityServerFatalException(ImpunityErrorCode.ServerUnavailable, "Server is busy, try again later");
			}

			// While a migration is being offered or run, the world is reserved for its owner; turn everyone else away.
			lock (MigrationLock)
			{
				if (MigrationPhaseState != MigrationPhase.None)
				{
					throw new ImpunityServerFatalException(ImpunityErrorCode.ServerMigrationInProgress, "A migration is in progress, try again later");
				}
			}

			EstablishConnectResult result = new EstablishConnectResult();
			result.ConnectionId = proxy.ConnectionId;

			if (!ValidateFormat(format))
			{
				if (Metadata.Version == 0)
				{
					// Brand-new world: there is nothing to migrate, just adopt the client's format.
					UpdateFormat(format, proxy.IsRemote);
				}
				else if (format.Version > Metadata.Version)
				{
					// A higher-version client. We do NOT migrate automatically: offer it and let the client decide.
					bool eligible = !proxy.IsRemote || Options.RemoteUpgradeAllowed;
					if (!eligible)
					{
						throw new ImpunityServerFatalException(ImpunityErrorCode.ServerVersionIncompatible, "Client version doesn't match server version");
					}

					if (Live.HasOtherConnections(proxy))
					{
						// Migration is only offered into a world with no other clients; never evict the clients already playing.
						throw new ImpunityServerFatalException(ImpunityErrorCode.ServerVersionIncompatible, "Client version doesn't match server version");
					}

					StartMigrationOffer(proxy, format);

					result.MigrationRequired = true;
					result.MigrationFromVersion = Metadata.Version;
					result.MigrationToVersion = format.Version;

					ConnectionOpened(proxy);
					return result;
				}
				else if (!Live.HasOtherConnections(proxy))
				{
					// Only connection, same-or-lower version with a different checksum: adopt as before (downgrade rejected inside).
					UpdateFormat(format, proxy.IsRemote);
				}
				else
				{
					throw new ImpunityServerFatalException(ImpunityErrorCode.ServerVersionIncompatible, "Client version doesn't match server version");
				}
			}

			ConnectionOpened(proxy);

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

			StopMigrationTimer();
			StopIdleCleanupTimer();

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
					catch (Exception e)
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
			// While a migration is offered/running, restrict the owner to migration operations and reject everything else
			// from client connections. Establish is exempt (handled inside EstablishConnection); clock sync is exempt
			// (part of the handshake and the periodic resync). Internal actions have no Origin and pass through.
			if (action.Origin != null && !(action is EstablishConnectionAction) && !(action is GetTimeAction))
			{
				CheckMigrationGate(action);
				if (action.Error != null)
				{
					SendActionResults(action);
					return;
				}
			}

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


		// ============================ Data migration ============================
		//
		// See docs/guides/SchemaMigration.md. The world moves None -> Offered -> Migrating -> None. The offer is
		// non-destructive (no backup, no marker); only an explicit begin snapshots the DB and unlocks raw migration ops.
		// Transitions run on the live thread; cross-thread reads/writes are guarded by MigrationLock.

		/// <summary>Reads the current migration phase.</summary>
		public MigrationPhase GetMigrationPhase()
		{
			lock (MigrationLock)
			{
				return MigrationPhaseState;
			}
		}

		/// <summary>True if the given connection currently owns an offered/in-progress migration.</summary>
		public bool IsMigrationOwner(string connectionId)
		{
			lock (MigrationLock)
			{
				return MigrationPhaseState != MigrationPhase.None && MigrationOwnerId == connectionId;
			}
		}

		/// <summary>Records migration activity, resetting the idle timeout. Called by migration operations.</summary>
		public void TouchMigrationActivity()
		{
			lock (MigrationLock)
			{
				MigrationLastActivityMillis = GetServerTime();
			}
		}

		/// <summary>Throws unless a migration is in progress and owned by the given connection. Used to gate raw migration DB operations.</summary>
		public void EnsureMigratingOwner(IServerSideConnectionProxy origin)
		{
			lock (MigrationLock)
			{
				if (MigrationPhaseState != MigrationPhase.Migrating || MigrationOwnerId != origin.ConnectionId)
				{
					throw new ImpunityServerException(ImpunityErrorCode.ServerMigrationInProgress, "No migration in progress for this connection");
				}
			}
		}

		// Enforces the per-phase whitelist of actions for the migration owner; sets action.Error if disallowed.
		private void CheckMigrationGate(GameStateActionBase action)
		{
			lock (MigrationLock)
			{
				if (MigrationPhaseState == MigrationPhase.None)
				{
					return;
				}

				if (MigrationOwnerId != action.Origin.ConnectionId)
				{
					// Shouldn't normally happen (others can't connect during a migration), but be safe.
					action.Error = new ImpunityErrorResponse(ImpunityErrorCode.ServerMigrationInProgress, "A migration is in progress");
					return;
				}

				bool allowed;
				if (MigrationPhaseState == MigrationPhase.Offered)
				{
					allowed = action is BeginMigrationAction || action is AbortMigrationAction;
				}
				else // Migrating
				{
					allowed = action is MigrationGetCollectionsAction
						|| action is MigrationScanAction
						|| action is MigrationWriteAction
						|| action is CommitMigrationAction
						|| action is AbortMigrationAction;
				}

				if (!allowed)
				{
					action.Error = new ImpunityErrorResponse(ImpunityErrorCode.ServerMigrationInProgress, "Only migration operations are allowed while a migration is in progress");
				}
			}
		}

		// Enters the (non-destructive) offered state, reserving the world for this connection.
		private void StartMigrationOffer(IServerSideConnectionProxy proxy, GameStateFormatData format)
		{
			lock (MigrationLock)
			{
				MigrationPhaseState = MigrationPhase.Offered;
				MigrationOwnerId = proxy.ConnectionId;
				MigrationOwnerProxy = proxy;
				MigrationTargetFormat = format;
				MigrationFromVersion = Metadata.Version;
				MigrationToVersion = format.Version;
				MigrationLastActivityMillis = GetServerTime();
				MigrationAborting = false;
				MigrationAbortCloseProxy = null;
			}

			StartMigrationTimer();

			ImpunityLogger.LogInformation("Migration offered to " + proxy.ConnectionId + " (v" + Metadata.Version + " -> v" + format.Version + ")");
		}

		/// <summary>Handles an explicit begin-migration request: transitions to Migrating and snapshots the database. The reply is deferred until the snapshot completes.</summary>
		public void HandleBeginMigration(GameStateActionBase beginAction)
		{
			MigrationMarker marker;
			lock (MigrationLock)
			{
				if (MigrationPhaseState != MigrationPhase.Offered || MigrationOwnerId != beginAction.Origin.ConnectionId)
				{
					beginAction.Error = new ImpunityErrorResponse(ImpunityErrorCode.ServerMigrationInProgress, "No migration offer for this connection");
					return;
				}

				// Flip to Migrating now so a duplicate begin can't start a second backup.
				MigrationPhaseState = MigrationPhase.Migrating;
				MigrationLastActivityMillis = GetServerTime();
				marker = new MigrationMarker(MigrationFromVersion, MigrationToVersion, MigrationOwnerId!, GetServerTime());
			}

			beginAction.AwaitingTask = true;
			QueueAction(new MigrationBackupDBAction(marker, beginAction));
		}

		/// <summary>Called on the live thread after the backup DB action completes, to finish the begin-migration handshake.</summary>
		public void FinishBeginMigration(GameStateActionBase beginAction, bool backupOk, ImpunityErrorResponse? backupError)
		{
			if (backupOk)
			{
				lock (MigrationLock)
				{
					MigrationLastActivityMillis = GetServerTime();
				}
				ImpunityLogger.LogInformation("Migration started for " + beginAction.Origin.ConnectionId);
			}
			else
			{
				ClearMigrationState();
				beginAction.Error = backupError ?? new ImpunityErrorResponse(ImpunityErrorCode.InternalServerError, "Migration backup failed");
			}

			SendActionResults(beginAction);
		}

		/// <summary>Handles a commit request: stamps the new schema version then cleans up the snapshot/marker.</summary>
		public void HandleCommitMigration(GameStateActionBase commitAction)
		{
			GameStateFormatData target;
			lock (MigrationLock)
			{
				if (MigrationPhaseState != MigrationPhase.Migrating || MigrationOwnerId != commitAction.Origin.ConnectionId || MigrationAborting)
				{
					commitAction.Error = new ImpunityErrorResponse(ImpunityErrorCode.ServerMigrationInProgress, "No migration in progress for this connection");
					return;
				}
				target = MigrationTargetFormat!;
				MigrationLastActivityMillis = GetServerTime();
			}

			commitAction.AwaitingTask = true;

			// Stamp the new version + collections (queues UpdateDBFormatAction on the DB thread)...
			UpdateFormat(target, commitAction.Origin.IsRemote);
			// ...then delete the snapshot + marker (FIFO after the metadata write) and finish.
			QueueAction(new MigrationFinalizeDBAction(commitAction));
		}

		/// <summary>Called on the live thread after the finalize DB action completes.</summary>
		public void FinishCommitMigration(GameStateActionBase commitAction)
		{
			ImpunityLogger.LogInformation("Migration committed (now v" + Metadata.Version + ")");
			ClearMigrationState();
			SendActionResults(commitAction);
		}

		/// <summary>Handles a client-initiated abort/decline.</summary>
		public void HandleAbortMigration(GameStateActionBase abortAction)
		{
			lock (MigrationLock)
			{
				if (MigrationPhaseState == MigrationPhase.None || MigrationOwnerId != abortAction.Origin.ConnectionId)
				{
					abortAction.Error = new ImpunityErrorResponse(ImpunityErrorCode.ServerMigrationInProgress, "No migration to abort for this connection");
					return;
				}
			}

			DoAbortMigration(abortAction, false);
		}

		// Called when a connection closes; if it owns a migration, roll it back / release the offer.
		internal void OnConnectionClosedForMigration(IServerSideConnectionProxy proxy)
		{
			lock (MigrationLock)
			{
				if (MigrationPhaseState == MigrationPhase.None || MigrationOwnerId != proxy.ConnectionId)
				{
					return;
				}
			}

			ImpunityLogger.LogInformation("Migration owner disconnected; aborting migration");
			DoAbortMigration(null, false);
		}

		// Live-thread timeout check, scheduled by the idle timer.
		internal void CheckMigrationTimeout()
		{
			long now = GetServerTime();
			lock (MigrationLock)
			{
				if (MigrationPhaseState == MigrationPhase.None || MigrationAborting)
				{
					return;
				}
				if (now - MigrationLastActivityMillis < Options.MigrationIdleTimeoutMillis)
				{
					return;
				}
			}

			ImpunityLogger.LogWarning("Migration idle timeout; aborting migration");
			DoAbortMigration(null, true);
		}

		// Common abort path. For an Offered migration this is purely in-memory; for a Migrating one it restores the
		// snapshot on the DB thread and finishes in FinishAbortMigration.
		private void DoAbortMigration(GameStateActionBase? abortAction, bool closeOwner)
		{
			MigrationPhase phase;
			IServerSideConnectionProxy? ownerProxy;
			GameStateCollection[] collections;
			lock (MigrationLock)
			{
				if (MigrationPhaseState == MigrationPhase.None || MigrationAborting)
				{
					return;
				}
				phase = MigrationPhaseState;
				ownerProxy = MigrationOwnerProxy;
				collections = Metadata.Collections;
				MigrationAborting = true;
				MigrationAbortCloseProxy = closeOwner ? ownerProxy : null;
			}

			if (phase == MigrationPhase.Offered)
			{
				// Nothing was changed on disk; just release the reservation.
				IServerSideConnectionProxy? toClose = MigrationAbortCloseProxy;
				ClearMigrationState();
				if (abortAction != null)
				{
					SendActionResults(abortAction);
				}
				if (toClose != null)
				{
					toClose.CloseConnectionRequest();
				}
			}
			else // Migrating
			{
				if (abortAction != null)
				{
					abortAction.AwaitingTask = true;
				}
				QueueAction(new MigrationRestoreDBAction(collections, abortAction));
			}
		}

		/// <summary>Called on the live thread after the restore DB action completes.</summary>
		public void FinishAbortMigration(GameStateActionBase? abortAction)
		{
			IServerSideConnectionProxy? toClose = MigrationAbortCloseProxy;
			ImpunityLogger.LogInformation("Migration aborted; world restored to v" + Metadata.Version);
			ClearMigrationState();

			if (abortAction != null)
			{
				SendActionResults(abortAction);
			}
			if (toClose != null)
			{
				toClose.CloseConnectionRequest();
			}
		}

		private void ClearMigrationState()
		{
			lock (MigrationLock)
			{
				MigrationPhaseState = MigrationPhase.None;
				MigrationOwnerId = null;
				MigrationOwnerProxy = null;
				MigrationTargetFormat = null;
				MigrationFromVersion = 0;
				MigrationToVersion = 0;
				MigrationAborting = false;
				MigrationAbortCloseProxy = null;
			}

			StopMigrationTimer();
		}

		private void StartMigrationTimer()
		{
			StopMigrationTimer();

			int period = Options.MigrationIdleTimeoutMillis;
			if (period <= 0)
			{
				return;
			}

			int interval = Math.Max(1000, period / 2);
			MigrationTimer = new Timer(MigrationTimerTick, null, interval, interval);
		}

		private void StopMigrationTimer()
		{
			Timer? timer = MigrationTimer;
			MigrationTimer = null;
			timer?.Dispose();
		}

		private void MigrationTimerTick(object? state)
		{
			try
			{
				QueueAction(new MigrationTimeoutCheckAction());
			}
			catch (Exception)
			{
				// Server is shutting down (queue closed); ignore.
			}
		}

		private void StartIdleCleanupTimer()
		{
			StopIdleCleanupTimer();

			int period = Options.IdleChannelTimeoutMillis;
			if (period <= 0)
			{
				return;
			}

			int interval = Math.Max(1000, period / 2);
			IdleCleanupTimer = new Timer(IdleCleanupTimerTick, null, interval, interval);
		}

		private void StopIdleCleanupTimer()
		{
			Timer? timer = IdleCleanupTimer;
			IdleCleanupTimer = null;
			timer?.Dispose();
		}

		private void IdleCleanupTimerTick(object? state)
		{
			try
			{
				QueueAction(new IdleChannelCleanupAction());
			}
			catch (Exception)
			{
				// Server is shutting down (queue closed); ignore.
			}
		}

		// Runs on the live thread (via IdleChannelCleanupAction) to reap channels idle past the timeout.
		internal void CheckIdleChannels()
		{
			ChannelsReapedTotal += Live.CleanupIdleChannels(GetServerTime(), Options.IdleChannelTimeoutMillis);
		}

		/// <summary>Total number of idle channels reaped from live memory since this server started. Useful for monitoring and tests.</summary>
		public int GetReapedChannelCount()
		{
			return ChannelsReapedTotal;
		}

		// Constructor-time recovery for a migration interrupted by a crash/kill.
		private void RecoverInterruptedMigration()
		{
			MigrationMarker? marker = DB.ReadMigrationMarker();
			if (marker == null)
			{
				return;
			}

			if (marker.ToVersion == Metadata.Version)
			{
				// The commit had already persisted the new version; only cleanup remains.
				ImpunityLogger.LogInformation("Found completed migration marker (v" + marker.ToVersion + "); cleaning up");
				DB.ClearMigrationFiles();
			}
			else
			{
				// Interrupted before commit: restore the pre-migration snapshot.
				ImpunityLogger.LogWarning("Found interrupted migration (v" + marker.FromVersion + " -> v" + marker.ToVersion + "); restoring backup");
				DB.RestoreFromMigrationBackup(Metadata.Collections);
				Metadata = DB.LoadMetadata();
			}
		}


	}

}
