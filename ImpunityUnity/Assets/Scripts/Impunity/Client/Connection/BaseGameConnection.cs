using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	public delegate void BroadcastMessageHandler(int messageType, BsonValue messageBody, string sender);


	public abstract class BaseGameConnection : IServerMessageHandler, IDisposable
	{
		public ClientEntityManager EntityManager { get; private set; }
		protected GameStateFormatData LocalFormat;

		protected ConcurrentQueue<GameStateActionBase> CompletedActions;

		public abstract void Connect(ImpunityCallback onComplete);

		public BaseGameConnection(GameStateFormat format, ClientEntityManager em)
        {
			if (em == null)
            {
				em = new ClientEntityManager();
			}
			EntityManager = em;
			EntityManager.Connection = this;
			GameStateEntityTypeDef[] entityTypes = EntityManager.RegisterEntityTypes(format.EntityTypes);

			LocalFormat = new GameStateFormatData(format, entityTypes);

			CompletedActions = new ConcurrentQueue<GameStateActionBase>();
		}

		protected void EstablishConnection(string gameId, string password, GameStateFormatData format, ImpunityCallback onComplete)
		{
			string hashedPassword = ImpunityUtil.HashPassword(password);
			DoAction(new EstablishConnectionAction(gameId, hashedPassword, format, onComplete));
		}


		public void Update()
		{
			EntityManager.SendUpdates();

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

		public void HandleCreateChannel(uint channelId, string channelName, int channelType, ArraySegment<byte> propData)
        {
			EntityManager.HandleCreateChannel(channelId, channelName, channelType, propData);
		}

		public void HandleCreateObject(uint objectId, uint channelId, int objectType, ArraySegment<byte> propData, string uniqueName, bool newlyCreated)
        {
			EntityManager.HandleCreateObject(objectId, channelId, objectType, propData, uniqueName, newlyCreated);
		}

		public void HandleEntityUpdate(uint entityId, ArraySegment<byte> updateData)
        {
			EntityManager.HandleEntityUpdate(entityId, updateData);
		}

		public void HandleEntityEvent(uint entityId, int eventType, BsonValue eventData)
        {
			EntityManager.HandleEntityEvent(entityId, eventType, eventData);
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

		public void TryToLock(string lockName, string key, ImpunityCallback<bool> onComplete)
        {
			DoAction(new LockNamedLockAction(lockName, key, onComplete));
        }

		public void Unlock(string lockName, string key, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockNamedLockAction(lockName, key, onComplete));
		}


		public void SubcribeToChannel(string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, ImpunityCallback<uint> onComplete)
		{
			DoAction(new SubscribeChannelAction(channelName, createIfMissing, entityTypeId, instanceFlags, propBytes, onComplete));
		}

		public void UnsubscribeFromChannel(uint channelId, ImpunityCallback onComplete)
		{
			DoAction(new UnsubscribeChannelAction(channelId, onComplete));
		}

		public void CreateObject(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, ImpunityCallback<uint> onComplete)
		{
			DoAction(new CreateObjectAction(entityTypeId, instanceFlags, channelId, propBytes, uniqueName, onComplete));
		}

		public void UpdateEntity(uint entityId, string key, ArraySegment<byte> updateData, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateEntityAction(entityId, key, updateData, onComplete));
		}

		public void DeleteEntity(uint entityId, string key, BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteEntityAction(entityId, key, deleteData, onComplete));
		}

		public void TriggerEntityEvent(uint entityId, int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			DoAction(new EventEntityAction(entityId, eventType, eventData, onComplete));
		}

		public void TryToLockEntity(uint entityId, string key, ImpunityCallback<bool> onComplete)
		{
			DoAction(new LockEntityAction(entityId, key, onComplete));
		}

		public void UnlockEntity(uint entityId, string key, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UnlockEntityAction(entityId, key, onComplete));
		}

		// -------- Broadcast

		public void SendBroadcastMessage(int messageType, BsonValue msgBody)
        {
			DoAction(new SendBroadcastMessageAction(messageType, msgBody));
        }

	}

}