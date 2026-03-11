using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	/// <summary>Result of a <see cref="BaseGameConnection.WaitForLock"/> operation.</summary>
	public enum LockWaitResult
	{
		Error,
		Locked,
		Unlocked,
		Timeout
	}
	/// <summary>Callback for receiving broadcast messages from other clients.</summary>
	public delegate void BroadcastMessageHandler(int messageType, BsonValue messageBody, string sender);


	/// <summary>
	/// Base class for client-side game connections (remote or local). Provides the public API for database operations,
	/// live state management (channels, entities, locks), and broadcast messaging. Call <see cref="Update"/> each frame
	/// to process completed actions and server push messages on the main thread.
	/// </summary>
	public abstract class BaseGameConnection : IServerMessageHandler, IDisposable
	{
		/// <summary>Manages client-side entity state, subscriptions, and dirty tracking.</summary>
		public ClientEntityManager EntityManager { get; private set; }
		protected GameStateFormatData LocalFormat;
		protected long ServerMillisOffset;

		protected ConcurrentQueue<GameStateActionBase> CompletedActions;

		/// <summary>Initiates the connection to the game server. Calls <paramref name="onComplete"/> with null on success.</summary>
		public abstract void Connect(ImpunityCallback onComplete);

		/// <summary>Server-assigned connection identifier, available after successful connection.</summary>
		public string ConnectionId {get; protected set;}

		/// <summary>Client-generated key used for reconnection identification.</summary>
		public string ConnectionKey { get; set; }
		/// <summary>True after the connection handshake and clock sync have completed successfully.</summary>
		public bool Connected { get; private set; }

		private Dictionary<string, ImpunityCallback<LockWaitResult>> LockWaits = new();

		private long ClockSyncRateMs = 60 * 1000;
		private long LastClockSync = 0;

		public BaseGameConnection(GameStateFormat format, ClientEntityManager em)
		{
			if (em == null)
			{
				em = new ClientEntityManager();
			}
			EntityManager = em;
			EntityManager.Connection = this;
			GameStateEntityTypeDef[] entityTypes = EntityManager.RegisterEntityTypes(format.EntityTypes);

			ConnectionKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
			ConnectionId = "unconnected";
			
			LocalFormat = new GameStateFormatData(format, entityTypes);

			CompletedActions = new ConcurrentQueue<GameStateActionBase>();
		}

		protected void EstablishConnection(string gameId, string password, GameStateFormatData format, ImpunityCallback onComplete)
		{
			void synced(ImpunityErrorResponse err)
			{
				if (err != null)
				{
					onComplete?.Invoke(err);
					return;
				}

				Connected = true;
				onComplete?.Invoke(null);
			}

			void onEstablished(ImpunityErrorResponse err, EstablishConnectResult result)
			{
				if (err != null)
				{
					onComplete?.Invoke(err);
					return;
				}
				
				this.ConnectionId = result.ConnectionId;
				ImpunityLogger.LogInformation("Connected with connection id " + this.ConnectionId);
				SyncServerTime(synced);
			}

			string hashedPassword = ImpunityUtil.HashPassword(password);
			DoAction(new EstablishConnectionAction(gameId, hashedPassword, format, ConnectionKey, onEstablished));
		}

		private void SyncServerTime(ImpunityCallback onComplete = null)
		{
			LastClockSync = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			void gotTime(ImpunityErrorResponse err, long serverTimeMillis)
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

		/// <summary>Returns the estimated current server time in Unix milliseconds, adjusted by the clock sync offset.</summary>
		public long GetServerTime()
		{
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerMillisOffset;
		}

		/// <summary>Processes completed actions and server messages on the main thread. Also sends dirty entity updates and periodic clock syncs. Must be called each frame.</summary>
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

		public abstract void Dispose();

		/// <summary>Sends an action to the server for execution. Subclasses route this to the appropriate transport.</summary>
		public abstract void DoAction(GameStateActionBase action);

		// ------- Server message handling

		protected void OnServerMessage(ServerActionBase action)
        {
			CompletedActions.Enqueue(action);
		}

		/// <summary>Callback invoked on the main thread when a broadcast message is received from another client.</summary>
		public BroadcastMessageHandler OnBroadcastMessage {get; set;}
		

		// Server message handlers

		public void HandleCreateChannel(uint channelId, string channelName, int channelType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData)
        {
			EntityManager.HandleCreateChannel(channelId, channelName, channelType, isLocked, instanceFlags, propData);
		}

		public void HandleCreateObject(uint objectId, uint channelId, int objectType, bool isLocked, byte instanceFlags, ArraySegment<byte> propData, string uniqueName, bool newlyCreated)
        {
			EntityManager.HandleCreateObject(objectId, channelId, objectType, isLocked, instanceFlags, propData, uniqueName, newlyCreated);
		}

		public void HandleEntityUpdate(uint entityId, ArraySegment<byte> updateData)
        {
			EntityManager.HandleEntityUpdate(entityId, updateData);
		}

		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
        {
			EntityManager.HandleEntityEvent(entityId, eventType, eventData);
		}

		public void HandleEntityLocked(uint entityId)
		{
			EntityManager.HandleEntityLocked(entityId);
		}

		public void HandleEntityUnlocked(uint entityId)
		{
			EntityManager.HandleEntityUnlocked(entityId);
		}

		public void HandleEntityDelete(uint entityId, BsonValue deleteData)
        {
			EntityManager.HandleEntityDelete(entityId, deleteData);
		}

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

		public void HandleNamedLockUnlocked(string lockName)
        {
			if(LockWaits.TryGetValue(lockName, out var onComplete))
			{
				LockWaits.Remove(lockName);
				try
				{
					onComplete.Invoke(null, LockWaitResult.Unlocked);
				}
				catch(Exception e)
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

		/// <summary>Executes multiple database actions atomically in a single batch.</summary>
		public void CompoundDatabaseAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete)
		{
			DoAction(new CompoundDatabaseAction(actions, onComplete));
		}

		// -------- Game Setup

		/// <summary>Updates the game world's summary document on the server.</summary>
		public void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete)
		{
			DoAction(new SetGameSummaryAction(summary, onComplete));
		}

		/// <summary>Retrieves the game world's summary document from the server.</summary>
		public void GetGameSummary(ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new GetGameSummaryAction(onComplete));
		}

		// -------- DB actions

		/// <summary>Inserts a document into a server DB collection. Returns the assigned ID.</summary>
		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			DoAction(new InsertDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Replaces an existing document in a server DB collection.</summary>
		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Inserts or replaces a document in a server DB collection.</summary>
		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpsertDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Merges fields into an existing document in a server DB collection.</summary>
		public void MergeIntoDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new MergeIntoDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Merges fields into an existing document, or inserts as new if not found.</summary>
		public void MergeInsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new MergeInsertDocumentAction(collectionId, doc, onComplete));
		}

		/// <summary>Retrieves a document from a server DB collection by ID.</summary>
		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new FindDocumentByIdAction(collectionId, id, onComplete));
		}

		/// <summary>Deletes a document from a server DB collection by ID.</summary>
		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteDocumentAction(collectionId, id, onComplete));
		}

		/// <summary>Lists all documents in a server DB collection.</summary>
		public void ListDocuments(int collectionId, ImpunityCallback<List<BsonDocument>> onComplete)
		{
			DoAction(new ListDocumentsAction(collectionId, onComplete));
		}

		// -------- Live game

		/// <summary>Attempts to acquire a named lock without waiting. Returns true if acquired.</summary>
		public void TryToLock(string lockName, ImpunityCallback<bool> onComplete)
        {
			DoAction(new LockNamedLockAction(lockName, false, onComplete));
        }

		/// <summary>Attempts to acquire a named lock, waiting for it to become available if currently held.</summary>
		public void WaitForLock(string lockName, ImpunityCallback<LockWaitResult> onComplete)
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
				else
				{
					LockWaits[lockName] = onComplete;
				}
				
			}));
        }

		/// <summary>Releases a named lock.</summary>
		public void Unlock(string lockName, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockNamedLockAction(lockName, onComplete));
		}

		/// <summary>Creates a live state channel with a root entity and optional child objects.</summary>
		public void CreateChannel(string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData> channelObjects, ImpunityCallback<bool> onComplete)
		{
			var action = new CreateChannelAction(channelName, entityTypeId, instanceFlags, propBytes, replace, onComplete);
			if (channelObjects != null)
			{
				action.Objects = new List<ObjectCreateData>(channelObjects);
			}
			DoAction(action);
		}

		/// <summary>Subscribes to a channel, loading it from DB if needed. Optionally creates the channel if it doesn't exist. Returns the channel entity ID.</summary>
		public void SubcribeToChannel(string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData> channelObjects, ImpunityCallback<uint> onComplete)
		{
			var action = new SubscribeChannelAction(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, onComplete);
			if (channelObjects != null)
			{
				action.Objects = new List<ObjectCreateData>(channelObjects);
			}
			DoAction(action);
		}

		/// <summary>Unsubscribes from a channel. The client will stop receiving updates for entities in this channel.</summary>
		public void UnsubscribeFromChannel(uint channelId, ImpunityCallback onComplete)
		{
			DoAction(new UnsubscribeChannelAction(channelId, onComplete));
		}

		/// <summary>Creates a new entity object within a channel. Returns the assigned entity ID.</summary>
		public void CreateObject(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, bool replace, ImpunityCallback<uint> onComplete)
		{
			DoAction(new CreateObjectAction(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, onComplete));
		}

		/// <summary>Sends a property update for a live entity.</summary>
		public void UpdateEntity(uint entityId, ArraySegment<byte> updateData, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateEntityAction(entityId, updateData, onComplete));
		}

		/// <summary>Deletes a live entity by ID.</summary>
		public void DeleteEntity(uint entityId, BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteEntityAction(entityId, deleteData, onComplete));
		}

		/// <summary>Fires a one-shot event on an entity, broadcast to all channel subscribers.</summary>
		public void TriggerEntityEvent(uint entityId, int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			DoAction(new EventEntityAction(entityId, eventType, eventData, onComplete));
		}

		/// <summary>Attempts to acquire an exclusive lock on an entity.</summary>
		public void TryToLockEntity(uint entityId, ImpunityCallback<bool> onComplete)
		{
			DoAction(new LockEntityAction(entityId, onComplete));
		}

		/// <summary>Releases an exclusive lock on an entity.</summary>
		public void UnlockEntity(uint entityId, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockEntityAction(entityId, onComplete));
		}

		// -------- Broadcast

		/// <summary>Sends a typed broadcast message to all connected clients.</summary>
		public void SendBroadcastMessage(int messageType, BsonValue msgBody)
        {
			DoAction(new SendBroadcastMessageAction(messageType, msgBody));
        }

	}

}