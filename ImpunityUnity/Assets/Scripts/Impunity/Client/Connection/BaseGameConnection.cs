using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;


namespace Impunity.Connection
{

	/// <summary>Outcome of a <see cref="BaseGameConnection.WaitForLock"/> request on a named lock.</summary>
	public enum LockWaitResult
	{
		/// <summary>The request failed (an error response was returned).</summary>
		Error,
		/// <summary>The lock was free and is now held by this connection.</summary>
		Locked,
		/// <summary>
		/// The lock was held by someone else and has since been released, so it is now <em>available</em>.
		/// NOTE (flagged for review): this signals availability only — it does <em>not</em> re-acquire the lock.
		/// Call <see cref="BaseGameConnection.TryToLock"/> again to claim it.
		/// </summary>
		Unlocked,
		/// <summary>Reserved for a wait that times out. NOTE (flagged for review): no timeout is currently applied, so this value is never produced.</summary>
		Timeout
	}
	/// <summary>Handler for broadcast messages relayed from other clients.</summary>
	/// <param name="messageType">Application-defined message category.</param>
	/// <param name="messageBody">The message payload as a BSON value.</param>
	/// <param name="sender">The connection id of the client that sent the broadcast.</param>
	public delegate void BroadcastMessageHandler(int messageType, BsonValue messageBody, string sender);


	/// <summary>
	/// Shared base for the two client connection flavours — <see cref="RemoteGameConnection"/> (over a network
	/// transport) and <see cref="LocalGameConnection"/> (an embedded in-process server). It exposes the whole
	/// client-facing API: the document database (insert/find/list/…), live state (channels, entities, entity and named
	/// locks, events), broadcast messaging, and server-time sync.
	/// <para>
	/// Almost every call is asynchronous and follows the same shape: it builds a <see cref="GameStateActionBase"/> and
	/// hands it to <see cref="DoAction"/>, which the subclass routes to its server. The reply (or a server push) is
	/// later dispatched to your callback on the main thread when you call <see cref="Update"/> — so callbacks always run
	/// where it is safe to touch game/UI state, never on a socket or worker thread. Nothing is delivered between
	/// <see cref="Update"/> calls.
	/// </para>
	/// <para>
	/// The methods here are the low-level, byte-oriented surface. For ergonomic, strongly-typed access prefer the higher
	/// layers built on top: <see cref="ClientEntityManager"/> and the distributed entity types for live state (see
	/// <c>docs/guides/DistributedEntities.md</c>), <see cref="GameStateDBCollection{DTYPE}"/> for typed documents, and the
	/// <c>…Async</c>/<c>…Yield</c> extensions for await/coroutine styles.
	/// </para>
	/// </summary>
	public abstract class BaseGameConnection : IServerMessageHandler, IDisposable
	{
		/// <summary>Manages client-side entity state, subscriptions, and dirty tracking. The typed live-state API lives here.</summary>
		public ClientEntityManager EntityManager { get; private set; }
		/// <summary>This client's format (schema + entity types + checksum) sent to the server during the handshake.</summary>
		protected GameStateFormatData LocalFormat;
		/// <summary>Estimated server-minus-client clock delta in milliseconds, refreshed by clock sync. See <see cref="GetServerTime"/>.</summary>
		protected long ServerMillisOffset;

		/// <summary>Thread-safe inbox of completed actions and server pushes awaiting main-thread dispatch in <see cref="Update"/>.</summary>
		protected ConcurrentQueue<GameStateActionBase> CompletedActions = new ConcurrentQueue<GameStateActionBase>();

		/// <summary>Opens the connection (transport, handshake, clock sync). Implemented per transport.</summary>
		/// <param name="onComplete">Invoked on the main thread with null on success, or an error response on failure.</param>
		public abstract void Connect(ImpunityCallback onComplete);

		/// <summary>Server-assigned connection identifier. Reads <c>"unconnected"</c> until the handshake completes.</summary>
		public string ConnectionId { get; protected set; }

		/// <summary>
		/// Client-generated key identifying this client across reconnects. Defaults to a random value; set it before
		/// <see cref="Connect"/> to reclaim per-connection server state (locks, ownership) after a reconnect.
		/// </summary>
		public string ConnectionKey { get; set; }
		/// <summary>True once the handshake and initial clock sync have completed successfully.</summary>
		public bool Connected { get; private set; }

		/// <summary>
		/// Set after <see cref="Connect"/> when this (higher-version) client has been offered a data migration: the world
		/// is at an older schema version and has been reserved for this connection to migrate. Null when no migration is
		/// needed. Nothing has been changed on the server yet — call <c>RunMigrationAsync</c> (e.g. after asking the
		/// user) to perform it, or <see cref="DeclineMigration"/> to release the reservation. See
		/// <c>docs/guides/SchemaMigration.md</c>.
		/// </summary>
		public MigrationRequest? PendingMigration { get; internal set; }

		// Pending WaitForLock callbacks keyed by lock name, fired when a NamedLockUnlocked push arrives. One waiter per
		// name per connection — a second WaitForLock for the same name replaces the first.
		private Dictionary<string, ImpunityCallback<LockWaitResult>> LockWaits = new();

		private long ClockSyncRateMs = 60 * 1000;
		private long LastClockSync = 0;

		/// <summary>
		/// Initializes the connection's entity manager and format. Wires the manager to this connection, registers the
		/// format's entity types (validating their ids), generates a random <see cref="ConnectionKey"/>, and builds the
		/// <see cref="LocalFormat"/> (with checksum) used during the handshake.
		/// </summary>
		/// <param name="format">The game state format describing this client's schema and entity types.</param>
		/// <param name="em">An existing entity manager to adopt, or null to create a fresh one.</param>
		public BaseGameConnection(GameStateFormat format, ClientEntityManager? em)
		{
			if (em == null)
			{
				em = new ClientEntityManager();
			}
			EntityManager = em;
			EntityManager.Connection = this;
			GameStateEntityTypeDef[]? entityTypes = EntityManager.RegisterEntityTypes(format.EntityTypes);

			ConnectionKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
			ConnectionId = "unconnected";

			LocalFormat = new GameStateFormatData(format, entityTypes);
		}

		/// <summary>
		/// Runs the connect handshake: sends the establish-connection action (game id, hashed password, format), and on
		/// success records the server-assigned <see cref="ConnectionId"/>, performs the initial clock sync, and marks the
		/// connection <see cref="Connected"/>. Called by subclasses from <see cref="Connect"/> once the transport is ready.
		/// </summary>
		/// <param name="gameId">The world to join, or null for a local connection.</param>
		/// <param name="password">Plaintext world password; hashed before sending. Null/empty when unprotected.</param>
		/// <param name="format">This client's format data, checked against the server for compatibility.</param>
		/// <param name="onComplete">Invoked with null once connected, or with the first error encountered (handshake or clock sync).</param>
		protected void EstablishConnection(string? gameId, string? password, GameStateFormatData format, ImpunityCallback onComplete)
		{
			void synced(ImpunityErrorResponse? err)
			{
				if (err != null)
				{
					onComplete?.Invoke(err);
					return;
				}

				Connected = true;
				onComplete?.Invoke(null);
			}

			void onEstablished(ImpunityErrorResponse? err, EstablishConnectResult result)
			{
				if (err != null)
				{
					onComplete?.Invoke(err);
					return;
				}


				this.ConnectionId = result.ConnectionId;
				if (result.MigrationRequired)
				{
					PendingMigration = new MigrationRequest(result.MigrationFromVersion, result.MigrationToVersion);
					ImpunityLogger.LogInformation("Server offered a data migration: v" + result.MigrationFromVersion + " -> v" + result.MigrationToVersion);
				}
				ImpunityLogger.LogInformation("Connected with connection id " + this.ConnectionId);
				SyncServerTime(synced);
			}

			string? hashedPassword = ImpunityUtil.HashPassword(password);
			DoAction(new EstablishConnectionAction(gameId, hashedPassword, format, ConnectionKey, onEstablished));
		}

		// Requests the server clock and stores the offset from local time. NOTE: the offset is computed purely as
		// (serverTime - localTimeAtReply) and does not account for network latency, so it can be off by up to roughly
		// the round-trip time. Re-run periodically from Update() to limit drift.
		private void SyncServerTime(ImpunityCallback? onComplete = null)
		{
			LastClockSync = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			void gotTime(ImpunityErrorResponse? err, long serverTimeMillis)
			{
				if (err == null)
				{
					long clientTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
					ServerMillisOffset = serverTimeMillis - clientTimeMillis;
				}

				onComplete?.Invoke(err);
			}

			DoAction(new GetTimeAction(gotTime));
		}

		/// <summary>Estimated current server time in Unix milliseconds (local UTC plus the synced clock offset).</summary>
		/// <returns>The approximate server wall-clock time. Accuracy is bounded by network latency (the offset ignores round-trip time) and clock drift between syncs.</returns>
		public long GetServerTime()
		{
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerMillisOffset;
		}

		/// <summary>
		/// Drives the connection for one frame; must be called regularly (typically once per frame). When connected it
		/// flushes pending entity updates and runs a periodic clock sync, then dispatches every queued server message
		/// and completed action — invoking your callbacks and entity push-handlers on the calling (main) thread.
		/// </summary>
		/// <remarks>
		/// Outbound dirty entity state is flushed (<see cref="ClientEntityManager.SendUpdates"/>) <em>before</em> inbound
		/// messages are processed. Exceptions thrown by your callbacks are caught and logged so one bad handler cannot
		/// stall the queue. Subclasses may override to add transport-specific work (timeouts, WebGL send pumping) and
		/// then call <c>base.Update()</c>.
		/// </remarks>
		public virtual void Update()
		{
			if (Connected)
			{
				EntityManager.SendUpdates();

				long currTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

				if (currTimeMillis - LastClockSync >= ClockSyncRateMs)
				{
					SyncServerTime();
				}
			}

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
					ImpunityLogger.LogError("Exception in action results callback", e);
				}
			}

		}

		/// <summary>Closes the connection and releases its resources. Implemented per transport.</summary>
		public abstract void Dispose();

		/// <summary>Submits an action to the server for execution. Subclasses route it to their transport (serialize-and-send, or queue in-process).</summary>
		/// <param name="action">The action to run; its result (if it has a callback) is delivered later via <see cref="Update"/>.</param>
		public abstract void DoAction(GameStateActionBase action);

		// ------- Server message handling

		// Enqueues a server-originated message for main-thread dispatch in Update(). Called by subclasses from their
		// receive path (socket thread for remote, server worker thread for local).
		protected void OnServerMessage(ServerActionBase action)
		{
			CompletedActions.Enqueue(action);
		}

		/// <summary>Invoked on the main thread (during <see cref="Update"/>) for each broadcast message received from another client. Set this to handle broadcasts.</summary>
		public BroadcastMessageHandler? OnBroadcastMessage { get; set; }


		// Server message handlers (IServerMessageHandler). These run on the main thread via Update() and forward live
		// state pushes to the entity manager, which applies them to the typed entities. See ClientEntityManager and
		// docs/guides/DistributedEntities.md for what each does to entity state.

		/// <inheritdoc/>
		public void HandleCreateChannel(uint channelId, string channelName, int channelType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, ushort seq)
		{
			EntityManager.HandleCreateChannel(channelId, channelName, channelType, isLocked, instanceFlags, propData, seq);
		}

		/// <inheritdoc/>
		public void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string? uniqueName, bool newlyCreated, ushort seq)
		{
			EntityManager.HandleCreateObject(objectId, channelId, objectType, isLocked, instanceFlags, propData, uniqueName, newlyCreated, seq);
		}

		/// <inheritdoc/>
		public void HandleEntityUpdate(uint entityId, ArraySegment<byte> updateData, ushort seq)
		{
			EntityManager.HandleEntityUpdate(entityId, updateData, seq);
		}

		/// <inheritdoc/>
		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
		{
			EntityManager.HandleEntityEvent(entityId, eventType, eventData);
		}

		/// <inheritdoc/>
		public void HandleEntityLocked(uint entityId)
		{
			EntityManager.HandleEntityLocked(entityId);
		}

		/// <inheritdoc/>
		public void HandleEntityUnlocked(uint entityId)
		{
			EntityManager.HandleEntityUnlocked(entityId);
		}

		/// <inheritdoc/>
		public void HandleEntityDelete(uint entityId, BsonValue? deleteData)
		{
			EntityManager.HandleEntityDelete(entityId, deleteData);
		}

		/// <inheritdoc/>
		public void HandleBroadcastMessage(int messageType, BsonValue messageBody, string sentBy)
		{
			try
			{
				OnBroadcastMessage?.Invoke(messageType, messageBody, sentBy);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnBroadcastMessage handler", e);
			}
		}

		/// <inheritdoc/>
		/// <remarks>Fires the pending <see cref="WaitForLock"/> callback for this lock name with <see cref="LockWaitResult.Unlocked"/> (availability only — it does not re-acquire). Warns if no waiter was registered.</remarks>
		public void HandleNamedLockUnlocked(string lockName)
		{
			if (LockWaits.TryGetValue(lockName, out var onComplete))
			{
				LockWaits.Remove(lockName);
				try
				{
					onComplete.Invoke(null, LockWaitResult.Unlocked);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in WaitForLock callback", e);
				}
			}
			else
			{
				ImpunityLogger.LogWarning("Got NamedLock unlock for lock we weren't waiting for: " + lockName);
			}
		}

		// -------- API Calls

		/// <summary>
		/// Sends several database actions to the server in one request, run sequentially on the server's DB thread.
		/// </summary>
		/// <param name="actions">
		/// The sub-actions to run, in order. Every one must be a database operation (<see cref="GameStateActionBase.IsDBOperation"/>);
		/// the server rejects the whole request with <see cref="ImpunityErrorCode.ActionBadRequest"/> otherwise.
		/// </param>
		/// <param name="onComplete">
		/// Invoked on the main thread with one <see cref="ActionResult"/> per sub-action, in order (inspect each for its own error/result).
		/// </param>
		/// <remarks>
		/// This is a single round-trip batch, <em>not</em> an atomic transaction: sub-actions are applied one by one with
		/// no rollback, so an early failure does not undo earlier successes. When any sub-action fails the top-level
		/// result also carries an <see cref="ImpunityErrorCode.ActionCompoundFailure"/> error, while the per-action
		/// results still report exactly which ones failed.
		/// </remarks>
		public void CompoundDatabaseAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>>? onComplete)
		{
			DoAction(new CompoundDatabaseAction(actions, onComplete));
		}

		// -------- Game Setup

		/// <summary>Replaces the game world's summary document on the server (the metadata surfaced in discovery — map, mode, player count, etc.).</summary>
		/// <param name="summary">The new summary document.</param>
		/// <param name="onComplete">Invoked on the main thread with null on success, or an error.</param>
		public void SetGameSummary(BsonDocument summary, ImpunityCallback? onComplete)
		{
			DoAction(new SetGameSummaryAction(summary, onComplete));
		}

		/// <summary>Retrieves the game world's summary document from the server.</summary>
		/// <param name="onComplete">Invoked on the main thread with the summary document (may be null if none has been set), or an error.</param>
		public void GetGameSummary(ImpunityCallback<BsonDocument>? onComplete)
		{
			DoAction(new GetGameSummaryAction(onComplete));
		}

		// -------- Data migration
		//
		// These are the low-level building blocks of the migration flow. Most applications use the higher-level
		// RunMigrationAsync / DeclineMigrationAsync / EnsureFormatAsync extensions and the MigrationContext helpers
		// instead of calling these directly. See docs/guides/SchemaMigration.md.

		/// <summary>Explicitly begins the migration the server offered (see <see cref="PendingMigration"/>): the server snapshots the world and unlocks raw migration operations.</summary>
		/// <param name="onComplete">Invoked on the main thread with null once the snapshot is taken and migration may proceed, or an error (e.g. no migration was offered to this connection).</param>
		public void BeginMigration(ImpunityCallback? onComplete)
		{
			DoAction(new BeginMigrationAction(onComplete));
		}

		/// <summary>Commits the migration: the server stamps the new schema version, discards the snapshot, and reopens the world. After this the connection is a normal client at the new version.</summary>
		/// <param name="onComplete">Invoked on the main thread with null once the new version is committed, or an error (e.g. no migration is in progress for this connection).</param>
		public void CommitMigration(ImpunityCallback? onComplete)
		{
			DoAction(new CommitMigrationAction(onComplete));
		}

		/// <summary>Aborts an in-progress migration, rolling the world back to its pre-migration snapshot.</summary>
		/// <param name="onComplete">Invoked on the main thread with null once the world has been rolled back and reopened, or an error.</param>
		public void AbortMigration(ImpunityCallback? onComplete)
		{
			DoAction(new AbortMigrationAction(onComplete));
		}

		/// <summary>Declines an offered migration, releasing the world's reservation. Equivalent to <see cref="AbortMigration"/> while only offered (nothing has been changed).</summary>
		/// <param name="onComplete">Invoked on the main thread with null once the reservation is released, or an error.</param>
		public void DeclineMigration(ImpunityCallback? onComplete)
		{
			DoAction(new AbortMigrationAction(onComplete));
		}

		/// <summary>Lists every collection name in the world, including old/renamed collections and the reserved live-entities collection. Valid only while this connection is migrating.</summary>
		/// <param name="onComplete">Invoked on the main thread with the list of collection names, or an error (e.g. if no migration is in progress for this connection).</param>
		public void MigrationGetCollections(ImpunityCallback<List<string>>? onComplete)
		{
			DoAction(new MigrationGetCollectionsAction(onComplete));
		}

		/// <summary>Reads a page of raw documents from a named collection. Valid only while this connection is migrating.</summary>
		/// <param name="collectionName">The name of the collection to read from.</param>
		/// <param name="skip">Number of documents to skip (for paging through large collections).</param>
		/// <param name="limit">Maximum number of documents to return in this page.</param>
		/// <param name="onComplete">Invoked on the main thread with the page of raw documents, or an error.</param>
		public void MigrationScan(string collectionName, int skip, int limit, ImpunityCallback<List<BsonDocument>>? onComplete)
		{
			DoAction(new MigrationScanAction(collectionName, skip, limit, onComplete));
		}

		/// <summary>Performs a single raw write against a named collection. Valid only while this connection is migrating.</summary>
		/// <param name="collectionName">The name of the collection to write to (created on demand for inserts/upserts).</param>
		/// <param name="op">Which write to perform: insert, upsert, update, or delete.</param>
		/// <param name="doc">The document to write (matched/keyed by its <c>_id</c>); required for insert/upsert/update, ignored for delete.</param>
		/// <param name="id">The <c>_id</c> of the document to delete; used only for <see cref="MigrationWriteOp.Delete"/>.</param>
		/// <param name="onComplete">Invoked on the main thread with the write's success flag, or an error.</param>
		public void MigrationWrite(string collectionName, MigrationWriteOp op, BsonDocument? doc, BsonValue? id, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new MigrationWriteAction(collectionName, op, doc, id, onComplete));
		}

		// -------- DB actions

		/// <summary>Inserts a new document into a server database collection.</summary>
		/// <param name="collectionId">Target collection id (a <see cref="GameStateCollection.Index"/>, ≥ 10).</param>
		/// <param name="doc">The document to insert. If it has no <c>_id</c>, the server assigns one.</param>
		/// <param name="onComplete">Invoked on the main thread with the assigned <c>_id</c>, or an error (e.g. duplicate id).</param>
		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue>? onComplete)
		{
			DoAction(new InsertDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Replaces an existing document (matched by <c>_id</c>) in a server database collection.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="doc">The replacement document; its <c>_id</c> selects the row.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if a matching document was found and replaced, <c>false</c> if none existed.</param>
		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new UpdateDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Inserts a document, or replaces the existing one with the same <c>_id</c>.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="doc">The document to insert or replace.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if the document was inserted as new, or <c>false</c> if it replaced an existing one.</param>
		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new UpsertDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Merges the given fields into an existing document (matched by <c>_id</c>), leaving its other fields intact.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="doc">A partial document whose fields are copied over the stored document. Must include the target <c>_id</c>.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if the target document existed and was merged, <c>false</c> if it was not found (nothing is inserted).</param>
		public void MergeIntoDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new MergeIntoDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Merges the given fields into an existing document (matched by <c>_id</c>), or inserts <paramref name="doc"/> as a new document if none exists.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="doc">A partial document to merge, or the full document to insert when absent. Must include the target <c>_id</c>.</param>
		/// <param name="onComplete">Invoked with a success flag (true on insert of a new document; the merge path likewise reports success).</param>
		public void MergeInsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new MergeInsertDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Retrieves a single document from a server database collection by its <c>_id</c>.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="id">The <c>_id</c> of the document to fetch.</param>
		/// <param name="onComplete">Invoked on the main thread with the document, or <c>null</c> if no document has that id.</param>
		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument>? onComplete)
		{
			DoAction(new FindDocumentByIdAction(collectionId, id, onComplete));
		}

		/// <summary>Deletes a document from a server database collection by its <c>_id</c>.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="id">The <c>_id</c> of the document to delete.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if a matching document was found and deleted, <c>false</c> otherwise.</param>
		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new DeleteDocumentAction(collectionId, id, onComplete));
		}

		/// <summary>Retrieves every document in a server database collection.</summary>
		/// <param name="collectionId">Target collection id.</param>
		/// <param name="onComplete">Invoked on the main thread with the list of documents (empty when the collection has none), or an error.</param>
		public void ListDocuments(int collectionId, ImpunityCallback<List<BsonDocument>>? onComplete)
		{
			DoAction(new ListDocumentsAction(collectionId, onComplete));
		}

		// -------- Live game
		//
		// "Named locks" are server-wide mutexes keyed by an arbitrary string, independent of any entity. A connection
		// holds at most one instance of each named lock; the server releases all of a connection's named locks when it
		// disconnects. (For locking a specific live entity instead, see TryToLockEntity / UnlockEntity below.)

		/// <summary>Tries to acquire a named lock without blocking.</summary>
		/// <param name="lockName">The lock's name. Created on first use.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if the lock is now held by this connection, <c>false</c> if another connection holds it.</param>
		public void TryToLock(string lockName, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new LockNamedLockAction(lockName, false, onComplete));
		}

		/// <summary>
		/// Tries to acquire a named lock, and if it is currently held by someone else, defers the callback until the lock
		/// is released rather than failing immediately.
		/// </summary>
		/// <param name="lockName">The lock's name.</param>
		/// <param name="onComplete">
		/// Invoked with <see cref="LockWaitResult.Locked"/> if acquired now; <see cref="LockWaitResult.Error"/> on failure;
		/// or, when the lock was busy, later with <see cref="LockWaitResult.Unlocked"/> once it frees.
		/// </param>
		/// <remarks>
		/// NOTE (flagged for review): the deferred <see cref="LockWaitResult.Unlocked"/> path signals availability only —
		/// it does <em>not</em> re-acquire the lock for you; call <see cref="TryToLock"/> again to claim it (another
		/// client may win the race in between). <see cref="LockWaitResult.Timeout"/> is never produced (no timeout is
		/// applied), so a lock that never frees leaves the callback pending indefinitely. Only one waiter is tracked per
		/// lock name per connection: a second <see cref="WaitForLock"/> for the same name before the first resolves
		/// silently replaces the earlier callback.
		/// </remarks>
		public void WaitForLock(string lockName, ImpunityCallback<LockWaitResult>? onComplete)
		{
			DoAction(new LockNamedLockAction(lockName, true, (err, locked) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, LockWaitResult.Error);
				}
				else if (locked)
				{
					onComplete?.Invoke(null, LockWaitResult.Locked);
				}
				else if (onComplete != null)
				{
					LockWaits[lockName] = onComplete;
				}

			}));
		}

		/// <summary>Releases a named lock held by this connection.</summary>
		/// <param name="lockName">The lock's name.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if this connection held the lock and it was released, <c>false</c> if it did not hold it.</param>
		public void Unlock(string lockName, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new UnlockNamedLockAction(lockName, onComplete));
		}

		// The methods below are the raw wire-level live-state API: ids and pre-serialized property blobs. Application
		// code normally uses the typed wrappers on ClientEntityManager (CreateChannel<T>, SubscribeToChannel<T>, …),
		// which build these arguments from your entity instances. See docs/guides/DistributedEntities.md.

		/// <summary>Creates a live state channel, with the channel entity's initial state and optional pre-populated child objects.</summary>
		/// <param name="channelName">Unique channel name to create.</param>
		/// <param name="entityTypeId">The channel entity's distributed type id.</param>
		/// <param name="instanceFlags">Instance flags (<see cref="ImpunityInstanceFlags"/>): persisted, client-authoritative, delete-on-disconnect.</param>
		/// <param name="propBytes">Serialized initial field state for the channel entity.</param>
		/// <param name="replace">If true, replace an existing channel of the same name; if false, fail when one exists.</param>
		/// <param name="channelObjects">Optional child objects to create in the channel up front.</param>
		/// <param name="onComplete">Invoked with <c>true</c> on success, or an error (e.g. name already exists when <paramref name="replace"/> is false). Creating a channel does not subscribe you to it.</param>
		public void CreateChannel(string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData>? channelObjects, ImpunityCallback<bool>? onComplete)
		{
			var action = new CreateChannelAction(channelName, entityTypeId, instanceFlags, propBytes, replace, onComplete);
			if (channelObjects != null)
			{
				action.Objects = new List<ObjectCreateData>(channelObjects);
			}
			DoAction(action);
		}

		/// <summary>
		/// Subscribes this connection to a channel — loading it from the database if needed — and optionally creates it
		/// when missing. After subscribing the server pushes the channel's current state and all subsequent updates.
		/// (Method name is misspelled "Subcribe"; kept for API compatibility.)
		/// </summary>
		/// <param name="channelName">The channel to subscribe to.</param>
		/// <param name="createIfMissing">If true, create the channel (using the entity type/flags/props below) when it does not already exist.</param>
		/// <param name="entityTypeId">Channel entity type id, used only when creating.</param>
		/// <param name="instanceFlags">Instance flags, used only when creating.</param>
		/// <param name="propBytes">Serialized initial channel field state, used only when creating.</param>
		/// <param name="channelObjects">Optional child objects to create alongside, used only when creating.</param>
		/// <param name="onComplete">Invoked with the channel's entity id on success, or an error.</param>
		public void SubcribeToChannel(string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData>? channelObjects, ImpunityCallback<uint>? onComplete)
		{
			var action = new SubscribeChannelAction(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, onComplete);
			if (channelObjects != null)
			{
				action.Objects = new List<ObjectCreateData>(channelObjects);
			}
			DoAction(action);
		}

		/// <summary>Unsubscribes from a channel; the client stops receiving updates for it and its objects.</summary>
		/// <param name="channelId">The channel's entity id (as returned by <see cref="SubcribeToChannel"/>).</param>
		/// <param name="onComplete">Invoked with null once the server has acknowledged the unsubscribe, or an error.</param>
		public void UnsubscribeFromChannel(uint channelId, ImpunityCallback onComplete)
		{
			DoAction(new UnsubscribeChannelAction(channelId, onComplete));
		}

		/// <summary>Creates a new entity object inside an existing channel.</summary>
		/// <param name="entityTypeId">The object's distributed type id.</param>
		/// <param name="instanceFlags">Instance flags (<see cref="ImpunityInstanceFlags"/>).</param>
		/// <param name="channelId">The id of the channel to create the object in.</param>
		/// <param name="propBytes">Serialized initial field state for the object.</param>
		/// <param name="uniqueName">Optional name unique within the channel (must not contain '/'); doubles as the database key for persisted objects.</param>
		/// <param name="replace">If true, replace an existing object with the same unique name.</param>
		/// <param name="onComplete">Invoked with the assigned entity id on success, or an error (e.g. <see cref="ImpunityErrorCode.ActionUniqueNameExists"/>).</param>
		public void CreateObject(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string? uniqueName, bool replace, ImpunityCallback<uint>? onComplete)
		{
			DoAction(new CreateObjectAction(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, onComplete));
		}

		/// <summary>Sends a serialized property delta for a live entity to the server, which validates and relays it to other subscribers.</summary>
		/// <param name="entityId">The entity to update.</param>
		/// <param name="updateData">The serialized changed-fields blob.</param>
		/// <param name="guaranteed">If true, send reliably (TCP); if false, allow best-effort delivery (UDP) for high-frequency updates.</param>
		/// <param name="seq">Per-entity sequence number used to discard stale/out-of-order updates.</param>
		/// <param name="onComplete">Optional completion callback; updates are typically fire-and-forget.</param>
		public void UpdateEntity(uint entityId, ArraySegment<byte> updateData, bool guaranteed, ushort seq, ImpunityCallback? onComplete)
		{
			var action = new UpdateEntityAction(entityId, updateData, seq, onComplete);
			action.SetGuaranteed(guaranteed);
			DoAction(action);
		}

		/// <summary>Sends an exclusive (optimistic-concurrency) property delta for a live entity. The server applies and
		/// relays it only if the client's known per-field seqs (<paramref name="knownFieldSeqs"/>) match the server's
		/// last-modified seqs for every field in the update; otherwise the whole update is rejected and
		/// <paramref name="onComplete"/> receives an <see cref="ImpunityErrorCode.ActionStaleData"/> error. Always sent
		/// reliably (TCP) regardless of the fields' guaranteed flags, because the reply is positionally correlated over
		/// the ordered stream.</summary>
		/// <param name="entityId">The entity to update.</param>
		/// <param name="updateData">The serialized changed-fields blob.</param>
		/// <param name="knownFieldSeqs">The client's known per-field seqs blob, format <c>[fieldId:byte][seq:ushort LE]...[0]</c>.</param>
		/// <param name="seq">Per-entity sequence number used to discard stale/out-of-order updates.</param>
		/// <param name="onComplete">Completion callback; receives an error on rejection (stale data or lock held by another).</param>
		public void UpdateEntityExclusive(uint entityId, ArraySegment<byte> updateData, ArraySegment<byte> knownFieldSeqs, ushort seq, ImpunityCallback? onComplete)
		{
			var action = new UpdateEntityAction(entityId, updateData, seq, knownFieldSeqs, onComplete);
			action.SetGuaranteed(true);
			DoAction(action);
		}

		/// <summary>Queues a completion callback to fire on the next <see cref="Update"/> without a server round-trip, used
		/// for locally-resolved outcomes (e.g. an exclusive update with nothing to send, or a client-side validation error).
		/// Delivered via the same <c>CompletedActions</c> queue as server replies, so it never runs reentrantly.</summary>
		internal void QueueLocalCallback(ImpunityCallback? onComplete, ImpunityErrorResponse? error)
		{
			if (onComplete == null)
			{
				return;
			}

			NoOpAction action = new NoOpAction(onComplete);
			action.Error = error;
			CompletedActions.Enqueue(action);
		}

		/// <summary>Deletes a live entity by id. Deleting a channel also deletes its member objects.</summary>
		/// <param name="entityId">The entity to delete.</param>
		/// <param name="deleteData">Optional payload relayed to subscribers with the delete notification (e.g. a reason).</param>
		/// <param name="onComplete">Invoked with <c>true</c> if the entity was deleted, or an error (e.g. blocked by another connection's lock).</param>
		public void DeleteEntity(uint entityId, BsonValue deleteData, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new DeleteEntityAction(entityId, deleteData, onComplete));
		}

		/// <summary>Fires a one-shot, fire-and-forget event on an entity. The server relays it to every subscriber of the entity's channel, including the sender.</summary>
		/// <param name="entityId">The entity to raise the event on.</param>
		/// <param name="eventType">Application-defined event category.</param>
		/// <param name="eventData">The event payload.</param>
		/// <param name="onComplete">Optional completion callback.</param>
		public void TriggerEntityEvent(uint entityId, int eventType, BsonValue eventData, ImpunityCallback? onComplete)
		{
			DoAction(new EventEntityAction(entityId, eventType, eventData, onComplete));
		}

		/// <summary>Tries to acquire an exclusive lock on a specific live entity for this connection. While held, the server rejects other connections' updates and deletes to that entity.</summary>
		/// <param name="entityId">The entity to lock.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if the lock is held by this connection (including if it already held it), <c>false</c> if another connection holds it.</param>
		public void TryToLockEntity(uint entityId, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new LockEntityAction(entityId, onComplete));
		}

		/// <summary>Releases this connection's exclusive lock on a live entity.</summary>
		/// <param name="entityId">The entity to unlock.</param>
		/// <param name="onComplete">Invoked with <c>true</c> if this connection held the lock and it was released, <c>false</c> otherwise.</param>
		public void UnlockEntity(uint entityId, ImpunityCallback<bool>? onComplete)
		{
			DoAction(new UnlockEntityAction(entityId, onComplete));
		}

		/// <summary>Lists all currently active channels. These are channels that have current subscribers, or have recently had subscribers.</summary>
		/// <param name="onComplete">Invoked with a list of channel names</param>
		public void ListActiveChannels(ImpunityCallback<List<string>>? onComplete)
		{
			DoAction(new ListActiveChannelsAction(onComplete));
		}

		/// <summary>Lists all channels that have been persisted to the database, whether they are currently active or not.</summary>
		/// <param name="onComplete">Invoked with a list of channel names</param>
		public void ListPersistedChannels(ImpunityCallback<List<string>>? onComplete)
		{
			DoAction(new ListPersistedChannelsAction(onComplete));
		}

		// -------- Broadcast

		/// <summary>Sends a typed broadcast message to all connected clients (delivered to their <see cref="OnBroadcastMessage"/>). Fire-and-forget — no completion callback or reply.</summary>
		/// <param name="messageType">Application-defined message category.</param>
		/// <param name="msgBody">The message payload.</param>
		public void SendBroadcastMessage(int messageType, BsonValue msgBody)
		{
			DoAction(new SendBroadcastMessageAction(messageType, msgBody));
		}

	}

}
