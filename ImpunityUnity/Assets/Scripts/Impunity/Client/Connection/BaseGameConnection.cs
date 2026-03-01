using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	public enum LockWaitResult
	{
		Error,
		Locked,
		Unlocked,
		Timeout
	}
	public delegate void BroadcastMessageHandler(int messageType, BsonValue messageBody, string sender);


	public abstract class BaseGameConnection : IServerMessageHandler, IDisposable
	{
		public ClientEntityManager EntityManager { get; private set; }
		protected GameStateFormatData LocalFormat;
		protected long ServerMillisOffset;

		protected ConcurrentQueue<GameStateActionBase> CompletedActions;

		public abstract void Connect(ImpunityCallback onComplete);

		public string ConnectionId {get; protected set;}

		public string ConnectionKey { get; set; }
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

		public long GetServerTime()
		{
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerMillisOffset;
		}

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

		public abstract void DoAction(GameStateActionBase action);

		// ------- Server message handling

		protected void OnServerMessage(ServerActionBase action)
        {
			CompletedActions.Enqueue(action);
		}

		// Handler delegates
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

		public void CompoundDatabaseAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete)
		{
			DoAction(new CompoundDatabaseAction(actions, onComplete));
		}

		// -------- Game Setup

		public void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete)
		{
			DoAction(new SetGameSummaryAction(summary, onComplete));
		}

		public void GetGameSummary(ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new GetGameSummaryAction(onComplete));
		}

		// -------- DB actions

		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			DoAction(new InsertDocumentAction(collectionId, doc, onComplete));
		}

		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateDocumentAction(collectionId, doc, onComplete));
		}

		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpsertDocumentAction(collectionId, doc, onComplete));
		}

		public void MergeIntoDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new MergeIntoDocumentAction(collectionId, doc, onComplete));
		}

		public void MergeInsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new MergeInsertDocumentAction(collectionId, doc, onComplete));
		}

		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new FindDocumentByIdAction(collectionId, id, onComplete));
		}

		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteDocumentAction(collectionId, id, onComplete));
		}

		public void ListDocuments(int collectionId, ImpunityCallback<List<BsonDocument>> onComplete)
		{
			DoAction(new ListDocumentsAction(collectionId, onComplete));
		}

		// -------- Live game

		public void TryToLock(string lockName, ImpunityCallback<bool> onComplete)
        {
			DoAction(new LockNamedLockAction(lockName, false, onComplete));
        }

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

		public void Unlock(string lockName, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockNamedLockAction(lockName, onComplete));
		}

		public void CreateChannel(string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, IEnumerable<ObjectCreateData> channelObjects, ImpunityCallback<bool> onComplete)
		{
			var action = new CreateChannelAction(channelName, entityTypeId, instanceFlags, propBytes, replace, onComplete);
			if (channelObjects != null)
			{
				action.Objects = new List<ObjectCreateData>(channelObjects);
			}
			DoAction(action);
		}

		public void SubcribeToChannel(string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, IEnumerable<ObjectCreateData> channelObjects, ImpunityCallback<uint> onComplete)
		{
			var action = new SubscribeChannelAction(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, onComplete);
			if (channelObjects != null)
			{
				action.Objects = new List<ObjectCreateData>(channelObjects);
			}
			DoAction(action);
		}

		public void UnsubscribeFromChannel(uint channelId, ImpunityCallback onComplete)
		{
			DoAction(new UnsubscribeChannelAction(channelId, onComplete));
		}

		public void CreateObject(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, bool replace, ImpunityCallback<uint> onComplete)
		{
			DoAction(new CreateObjectAction(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, replace, onComplete));
		}

		public void UpdateEntity(uint entityId, ArraySegment<byte> updateData, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateEntityAction(entityId, updateData, onComplete));
		}

		public void DeleteEntity(uint entityId, BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteEntityAction(entityId, deleteData, onComplete));
		}

		public void TriggerEntityEvent(uint entityId, int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			DoAction(new EventEntityAction(entityId, eventType, eventData, onComplete));
		}

		public void TryToLockEntity(uint entityId, ImpunityCallback<bool> onComplete)
		{
			DoAction(new LockEntityAction(entityId, onComplete));
		}

		public void UnlockEntity(uint entityId, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockEntityAction(entityId, onComplete));
		}

		// -------- Broadcast

		public void SendBroadcastMessage(int messageType, BsonValue msgBody)
        {
			DoAction(new SendBroadcastMessageAction(messageType, msgBody));
        }

	}

}