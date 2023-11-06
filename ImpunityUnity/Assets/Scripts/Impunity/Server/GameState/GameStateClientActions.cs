using System;
using System.Collections.Generic;

using UltraLiteDB;


namespace Impunity.GameState
{
	public enum ClientActionType
	{
		ESTABLISH_CONNECTION = 100,
		SET_SUMMARY = 101,
		GET_SUMMARY = 102,

		COMPOUND_DATABASE = 200,
		INSERT_DOCUMENT = 201,
		UPDATE_DOCUMENT = 202,
		UPSERT_DOCUMENT = 203,
		MERGE_INTO_DOCUMENT = 204,
		MERGE_INSERT_DOCUMENT = 205,
		FIND_DOCUMENT_BY_ID = 206,
		DELETE_DOCUMENT = 207,
		LIST_DOCUMENTS = 208,

		SUBSCRIBE_CHANNEL = 300,
		UNSUBSCRIBE_CHANNEL = 301,
		CREATE_OBJECT = 302,
		UPDATE_ENTITY = 303,
		DELETE_ENTITY = 304,
		EVENT_ENTITY = 305,
		LOCK_ENTITY = 306,
		UNLOCK_ENTITY = 307,
		LOCK_NAMED_LOCK = 308,
		UNLOCK_NAMED_LOCK = 309,

		BROADCAST_MESSAGE = 400,
	}

	public static class ClientActionFactory
	{
		public static Type GetActionClassType(int type)
		{
			ClientActionType typeEnum;

			try
			{
				typeEnum = (ClientActionType)type;
			}
			catch
			{
				throw new Exception("Unknown action type id: " + type);
			}

			return GetActionClassType(typeEnum);
		}

		public static Type GetActionClassType(ClientActionType type)
		{
			switch (type)
			{
				case ClientActionType.COMPOUND_DATABASE:
					return typeof(CompoundDatabaseAction);

				case ClientActionType.ESTABLISH_CONNECTION:
					return typeof(EstablishConnectionAction);
				case ClientActionType.SET_SUMMARY:
					return typeof(SetGameSummaryAction);
				case ClientActionType.GET_SUMMARY:
					return typeof(GetGameSummaryAction);

				case ClientActionType.INSERT_DOCUMENT:
					return typeof(InsertDocumentAction);
				case ClientActionType.UPDATE_DOCUMENT:
					return typeof(UpdateDocumentAction);
				case ClientActionType.UPSERT_DOCUMENT:
					return typeof(UpsertDocumentAction);
				case ClientActionType.MERGE_INTO_DOCUMENT:
					return typeof(MergeIntoDocumentAction);
				case ClientActionType.MERGE_INSERT_DOCUMENT:
					return typeof(MergeInsertDocumentAction);
				case ClientActionType.FIND_DOCUMENT_BY_ID:
					return typeof(FindDocumentByIdAction);
				case ClientActionType.DELETE_DOCUMENT:
					return typeof(DeleteDocumentAction);
				case ClientActionType.LIST_DOCUMENTS:
					return typeof(ListDocumentsAction);

				case ClientActionType.CREATE_OBJECT:
					return typeof(CreateObjectAction);
				case ClientActionType.UPDATE_ENTITY:
					return typeof(UpdateEntityAction);
				case ClientActionType.DELETE_ENTITY:
					return typeof(DeleteEntityAction);
				case ClientActionType.EVENT_ENTITY:
					return typeof(EventEntityAction);
				case ClientActionType.LOCK_ENTITY:
					return typeof(LockEntityAction);
				case ClientActionType.UNLOCK_ENTITY:
					return typeof(UnlockEntityAction);
				case ClientActionType.LOCK_NAMED_LOCK:
					return typeof(LockNamedLockAction);
				case ClientActionType.UNLOCK_NAMED_LOCK:
					return typeof(UnlockNamedLockAction);

				case ClientActionType.SUBSCRIBE_CHANNEL:
					return typeof(SubscribeChannelAction);
				case ClientActionType.UNSUBSCRIBE_CHANNEL:
					return typeof(UnsubscribeChannelAction);
				case ClientActionType.BROADCAST_MESSAGE:
					return typeof(SendBroadcastMessageAction);
			}

			throw new Exception("Action type id with no entry in factory: " + type);
		}
	}


	public class NoOpAction : ClientActionResultlessBase
	{
		public override bool IsDBOperation() { return false; }

		public NoOpAction() { }
		
		public NoOpAction(ImpunityCallback onComplete)
		{
			OnCompleteCallback = onComplete;
		}

		public override ushort GetActionType() { return 0; }
		public override bool HasCallback() { return true; }

		protected override void DoAction(GameStateServer game)
		{
			// Is actually a no-op
			throw new NotImplementedException();
		}
	}

	// Metadata operations

	public class EstablishConnectionAction : ClientActionResultlessBase
	{
		[BsonField("gid")]
		public string GameId;

		[BsonField("pw")]
		public string PasswordHash;

		[BsonField("f")]
		public GameStateFormatData Format;

		public override ushort GetActionType() { return (ushort)ClientActionType.ESTABLISH_CONNECTION; }
		public override bool IsDBOperation() { return false; }

		public EstablishConnectionAction() { }

		public EstablishConnectionAction(string gameId, string passwordHash, GameStateFormatData format, ImpunityCallback onComplete = null)
		{
			GameId = gameId;
			PasswordHash = passwordHash;
			Format = format;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.EstablishConnection(Origin, Format);
		}
	}

	public class SetGameSummaryAction : ClientActionResultlessBase
	{
		[BsonField("s")]
		public BsonDocument Summary;

		public override ushort GetActionType() { return (ushort)ClientActionType.SET_SUMMARY; }
		public override bool IsDBOperation() { return false; }

		public SetGameSummaryAction() { }

		public SetGameSummaryAction(BsonDocument summary, ImpunityCallback onComplete = null)
		{
			Summary = summary;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.SetGameSummary(Summary);
		}
	}

	public class GetGameSummaryAction : ClientActionResultBase<BsonDocument>
	{
		public override ushort GetActionType() { return (ushort)ClientActionType.GET_SUMMARY; }
		public override bool IsDBOperation() { return true; }

		public GetGameSummaryAction() { }

		public GetGameSummaryAction(ImpunityCallback<BsonDocument> onComplete = null)
		{
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.GetGameSummary();
		}
	}

	// Database operations

	public class CompoundDatabaseAction : ClientActionResultBase<List<ActionResult>>
	{
		[BsonField("as")]
		public List<GameStateActionBase> Actions;

		public override ushort GetActionType() { return (ushort)ClientActionType.COMPOUND_DATABASE; }
		public override bool IsDBOperation() { return true; }

		public CompoundDatabaseAction() { }

		public CompoundDatabaseAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete = null)
		{
			Actions = new List<GameStateActionBase>(actions);
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = new List<ActionResult>();

			int errors = 0;
			foreach (GameStateActionBase action in Actions)
			{
				if (!action.IsDBOperation())
				{
					throw new ImpunityServerException(ImpunityErrorCode.ActionBadRequest, "Non-database action in a compound database command");
				}

				action.Run(game);

				Result.Add(action.GetResult());

				if (action.Error != null)
				{
					errors += 1;
				}
			}

			if (errors > 0)
			{
				Error = new ImpunityErrorResponse(ImpunityErrorCode.ActionCompoundFailure, "Error(s) in compound action request: " + errors);
			}
		}


		// Custom deserializer so that we know what generic type to expect for each result nin the list
		public override void DeserializeResults(BsonDocument resultBody)
		{
			BsonMapper mapper = ImpunityUtil.GetBsonMapper();

			BsonValue errorVal = resultBody["e"];
			if (!errorVal.IsNull)
			{
				Error = mapper.ToObject<ImpunityErrorResponse>(errorVal.AsDocument);
			}

			BsonArray resultArray = (BsonArray)(resultBody["r"]);

			Result = new List<ActionResult>(resultArray.Count);

			for (int i = 0; i < Actions.Count; i++)
			{
				GameStateActionBase action = Actions[i];
				BsonDocument resultVal = resultArray[i].AsDocument;

				Type resultType = action.GetResultType();

				Result.Add((ActionResult)mapper.ToObject(resultType, resultVal));
			}
		}

	}

	public class InsertDocumentAction : ClientActionResultBase<BsonValue>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;

		public override ushort GetActionType() { return (ushort)ClientActionType.INSERT_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public InsertDocumentAction() { }

		public InsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete = null)
		{
			CollectionId = collectionId;
			Doc = doc;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.InsertDocument(CollectionId, Doc);
		}
	}

	public class UpdateDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;

		public override ushort GetActionType() { return (ushort)ClientActionType.UPDATE_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public UpdateDocumentAction() { }

		public UpdateDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete = null)
		{
			CollectionId = collectionId;
			Doc = doc;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.UpdateDocument(CollectionId, Doc);
		}
	}

	public class UpsertDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;

		public override ushort GetActionType() { return (ushort)ClientActionType.UPSERT_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public UpsertDocumentAction() { }

		public UpsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete = null)
		{
			CollectionId = collectionId;
			Doc = doc;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.UpsertDocument(CollectionId, Doc);
		}
	}

	public class MergeIntoDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;

		public override ushort GetActionType() { return (ushort)ClientActionType.MERGE_INTO_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public MergeIntoDocumentAction() { }

		public MergeIntoDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete = null)
		{
			CollectionId = collectionId;
			Doc = doc;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.MergeIntoDocument(CollectionId, Doc);
		}
	}

	public class MergeInsertDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;

		public override ushort GetActionType() { return (ushort)ClientActionType.MERGE_INTO_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public MergeInsertDocumentAction() { }

		public MergeInsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete = null)
		{
			CollectionId = collectionId;
			Doc = doc;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.MergeInsertDocument(CollectionId, Doc);
		}
	}

	public class FindDocumentByIdAction : ClientActionResultBase<BsonDocument>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("did")]
		public BsonValue Id;

		public override ushort GetActionType() { return (ushort)ClientActionType.FIND_DOCUMENT_BY_ID; }
		public override bool IsDBOperation() { return true; }

		public FindDocumentByIdAction() { }

		public FindDocumentByIdAction(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete = null)
		{
			CollectionId = collectionId;
			Id = id;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.FindDocumentById(CollectionId, Id);
		}
	}

	public class DeleteDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("did")]
		public BsonValue Id;

		public override ushort GetActionType() { return (ushort)ClientActionType.DELETE_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public DeleteDocumentAction() { }

		public DeleteDocumentAction(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete = null)
		{
			CollectionId = collectionId;
			Id = id;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.DeleteDocument(CollectionId, Id);
		}
	}

	public class ListDocumentsAction : ClientActionResultBase<List<BsonDocument>>
	{
		[BsonField("cid")]
		public int CollectionId;

		public override ushort GetActionType() { return (ushort)ClientActionType.LIST_DOCUMENTS; }
		public override bool IsDBOperation() { return true; }

		public ListDocumentsAction() { }

		public ListDocumentsAction(int collectionId, ImpunityCallback<List<BsonDocument>> onComplete = null)
		{
			CollectionId = collectionId;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.ListDocuments(CollectionId);
		}
	}


	// Entity actions

	public class SubscribeChannelAction : ClientActionResultBase<uint>, GameStateChannelLoadListener
	{
		[BsonField("cn")]
		public string Name;

		[BsonField("cim")]
		public bool CreateIfMissing;

		[BsonField("t")]
		public int EntityTypeId;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		public override ushort GetActionType() { return (ushort)ClientActionType.SUBSCRIBE_CHANNEL; }
		public override bool IsDBOperation() { return false; }

		public SubscribeChannelAction() { }

		public SubscribeChannelAction(string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, ImpunityCallback<uint> onComplete = null)
		{
			Name = channelName;
			CreateIfMissing = createIfMissing;
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			PropBytes = propBytes;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			GameStateEntity entity = game.Live.GetNamedEntity(Name);
			if (entity == null)
			{
				AwaitingTask = true;

				GameStateChannelLoadProxy loadProxy = game.Live.LoadChannel(Name);
				loadProxy.AddListener(this);
				if (CreateIfMissing)
				{
					loadProxy.SetCreateIfMissing(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, PropBytes);
				}

				return;
			}
			else if (entity is GameStateChannelLoadProxy loadProxy)
			{
				// Channel is currently loading, add ourselves as a listener

				AwaitingTask = true;
				loadProxy.AddListener(this);
				if (CreateIfMissing)
				{
					loadProxy.SetCreateIfMissing(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, PropBytes);
				}

				return;
			}

			SubscribeToChannelAction(game);
		}

		public void OnChannelLoaded(GameStateServer game, ImpunityErrorResponse error, GameStateChannel loadedChannel)
		{
			AwaitingTask = false;

			if (error != null)
			{
				// Error loading from db or creating it, report it
				Error = error;
				game.SendActionResults(this);
				return;
			}

			// Else the channel should now be loaded/created, try subscribing
			game.RunActionMethod(this, SubscribeToChannelAction);
		}

		private void SubscribeToChannelAction(GameStateServer game)
		{
			Result = game.Live.SubscribeToChannel(Origin.ConnectionReplicant, Name);
		}
	}

	public class UnsubscribeChannelAction : ClientActionResultlessBase
	{
		[BsonField("cid")]
		public uint ID;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNSUBSCRIBE_CHANNEL; }
		public override bool IsDBOperation() { return false; }

		public UnsubscribeChannelAction() { }

		public UnsubscribeChannelAction(uint channelId, ImpunityCallback onComplete = null)
		{
			ID = channelId;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.UnsubscribeFromChannel(Origin.ConnectionReplicant, ID);
		}
	}


	public class CreateObjectAction : ClientActionResultBase<uint>
	{
		[BsonField("t")]
		public int EntityTypeId;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("c")]
		public uint ChannelId;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("n")]
		public string UniqueName;

		public override ushort GetActionType() { return (ushort)ClientActionType.CREATE_OBJECT; }
		public override bool IsDBOperation() { return false; }

		public CreateObjectAction() { }

		public CreateObjectAction(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string uniqueName, ImpunityCallback<uint> onComplete = null)
		{
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			ChannelId = channelId;
			PropBytes = propBytes;
			UniqueName = uniqueName;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.CreateObject(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, ChannelId, PropBytes, UniqueName);
		}
	}

	public class UpdateEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("k")]
		public string Key;

		[BsonField("ub")]
		public ArraySegment<byte> UpdateBytes;

		public override ushort GetActionType() { return (ushort)ClientActionType.UPDATE_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public UpdateEntityAction() { }

		public UpdateEntityAction(uint entityId, string key, ArraySegment<byte> updateBytes, ImpunityCallback<bool> onComplete = null)
		{
			EntityId = entityId;
			Key = key;
			UpdateBytes = updateBytes;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.UpdateEntity(Origin.ConnectionReplicant, EntityId, Key, UpdateBytes);
		}
	}

	public class DeleteEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("k")]
		public string Key;

		[BsonField("dd")]
		public BsonValue DeleteData;

		public override ushort GetActionType() { return (ushort)ClientActionType.DELETE_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public DeleteEntityAction() { }

		public DeleteEntityAction(uint entityId, string key, BsonValue deleteData, ImpunityCallback<bool> onComplete = null)
		{
			EntityId = entityId;
			Key = key;
			DeleteData = deleteData;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.DeleteEntity(EntityId, Key, DeleteData);
		}
	}

	public class EventEntityAction : ClientActionResultlessBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("mt")]
		public int EventType;

		[BsonField("mb")]
		public BsonValue EventData;

		public override ushort GetActionType() { return (ushort)ClientActionType.EVENT_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public EventEntityAction() { }

		public EventEntityAction(uint entityId, int eventType, BsonValue eventData, ImpunityCallback onComplete = null)
		{
			EntityId = entityId;
			EventType = eventType;
			EventData = eventData;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.SendEntityEvent(EntityId, EventType, EventData);
		}
	}

	public class LockEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("k")]
		public string Key;

		public override ushort GetActionType() { return (ushort)ClientActionType.LOCK_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public LockEntityAction() { }

		public LockEntityAction(uint entityId, string key, ImpunityCallback<bool> onComplete = null)
		{
			EntityId = entityId;
			Key = key;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.LockEntity(Origin.ConnectionReplicant, EntityId, Key);
		}
	}

	public class UnlockEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("k")]
		public string Key;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNLOCK_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public UnlockEntityAction() { }

		public UnlockEntityAction(uint entityId, string key, ImpunityCallback<bool> onComplete = null)
		{
			EntityId = entityId;
			Key = key;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.UnlockEntity(Origin.ConnectionReplicant, EntityId, Key);
		}
	}

	public class LockNamedLockAction : ClientActionResultBase<bool>
	{
		[BsonField("ln")]
		public string Name;

		[BsonField("k")]
		public string Key;

		public override ushort GetActionType() { return (ushort)ClientActionType.LOCK_NAMED_LOCK; }
		public override bool IsDBOperation() { return false; }

		public LockNamedLockAction() { }

		public LockNamedLockAction(string lockName, string key, ImpunityCallback<bool> onComplete = null)
		{
			Name = lockName;
			Key = key;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.TryToLockNamedLock(Origin.ConnectionReplicant, Name, Key);
		}
	}

	public class UnlockNamedLockAction : ClientActionResultBase<bool>
	{
		[BsonField("ln")]
		public string Name;

		[BsonField("k")]
		public string Key;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNLOCK_NAMED_LOCK; }
		public override bool IsDBOperation() { return false; }

		public UnlockNamedLockAction() { }

		public UnlockNamedLockAction(string lockName, string key, ImpunityCallback<bool> onComplete = null)
		{
			Name = lockName;
			Key = key;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.UnlockNamedLock(Origin.ConnectionReplicant, Name, Key);
		}
	}

	public class SendBroadcastMessageAction : ClientActionResultlessBase
	{
		[BsonField("mt")]
		public int MessageType;

		[BsonField("mb")]
		public BsonValue MessageBody;

		public override ushort GetActionType() { return (ushort)ClientActionType.BROADCAST_MESSAGE; }
		public override bool IsDBOperation() { return false; }

		public SendBroadcastMessageAction() { }

		public SendBroadcastMessageAction(int messageType, BsonValue message, ImpunityCallback onComplete = null)
		{
			MessageType = messageType;
			MessageBody = message;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.SendBroadcastMessage(MessageType, MessageBody, Origin.ConnectionId);
		}
	}

}