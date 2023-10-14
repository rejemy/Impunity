using System;
using System.Collections.Generic;

using UltraLiteDB;

using Impunity.Networking;

namespace Impunity.GameState
{
	public enum ClientActionType
	{
		COMPOUND = 1,

		ESTABLISH_CONNECTION = 100,
		SET_SUMMARY = 101,
		GET_SUMMARY = 102,

		INSERT_DOCUMENT = 200,
		UPDATE_DOCUMENT = 201,
		UPSERT_DOCUMENT = 202,
		FIND_DOCUMENT_BY_ID = 203,
		DELETE_DOCUMENT = 204,
		LIST_DOCUMENTS = 205,

		CREATE_CHANNEL = 300,
		CREATE_OBJECT = 301,
		UPDATE_ENTITY = 302,
		DELETE_ENTITY = 303,
		EVENT_ENTITY = 304,
		LOCK_ENTITY = 305,
		UNLOCK_ENTITY = 306,
		LOCK_NAMED_LOCK = 307,
		UNLOCK_NAMED_LOCK = 308,

		SUBSCRIBE_CHANNEL = 400,
		UNSUBSCRIBE_CHANNEL = 401,
		BROADCAST_MESSAGE = 402,
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
				case ClientActionType.COMPOUND:
					return typeof(CompoundAction);

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
				case ClientActionType.FIND_DOCUMENT_BY_ID:
					return typeof(FindDocumentByIdAction);
				case ClientActionType.DELETE_DOCUMENT:
					return typeof(DeleteDocumentAction);
				case ClientActionType.LIST_DOCUMENTS:
					return typeof(ListDocumentsAction);

				case ClientActionType.CREATE_CHANNEL:
					return typeof(CreateChannelAction);
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


	public class EstablishConnectionAction : ClientActionResultlessBase
	{
		[BsonField("gid")]
		public string GameId;

		[BsonField("pw")]
		public string PasswordHash;

		[BsonField("f")]
		public GameStateFormatData Format;

		public override ushort GetActionType() { return (ushort)ClientActionType.ESTABLISH_CONNECTION; }

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


	public class InsertDocumentAction : ClientActionResultBase<BsonValue>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc;

		public override ushort GetActionType() { return (ushort)ClientActionType.INSERT_DOCUMENT; }

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

	public class FindDocumentByIdAction : ClientActionResultBase<BsonDocument>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("did")]
		public BsonValue Id;

		public override ushort GetActionType() { return (ushort)ClientActionType.FIND_DOCUMENT_BY_ID; }

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

	public class CompoundAction : ClientActionResultBase<List<ActionResult>>
	{
		[BsonField("as")]
		public List<GameStateActionBase> Actions;

		public override ushort GetActionType() { return (ushort)ClientActionType.COMPOUND; }

		public CompoundAction() { }

		public CompoundAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete = null)
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
			BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();

			BsonValue errorVal = resultBody["e"];
			if (!errorVal.IsNull)
			{
				Error = mapper.ToObject<ImpunityErrorResponse>(errorVal.AsDocument);
			}

			BsonArray resultArray = (BsonArray)(resultBody["r"]);

			Result = new List<ActionResult>(resultArray.Count);

			for(int i=0; i < Actions.Count; i++)
			{
				GameStateActionBase action = Actions[i];
				BsonDocument resultVal = resultArray[i].AsDocument;

				Type resultType = action.GetResultType();

				Result.Add((ActionResult)mapper.ToObject(resultType, resultVal));
			}
		}

	}

	// Entity actions

	public class CreateChannelAction : ClientActionResultBase<uint>
	{
		[BsonField("t")]
		public int EntityTypeId;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("n")]
		public string Name;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		public override ushort GetActionType() { return (ushort)ClientActionType.CREATE_CHANNEL; }
		public override bool LiveDataQueue() { return true; }

		public CreateChannelAction() { }

		public CreateChannelAction(int entityTypeId, byte instanceFlags, string channelName, ArraySegment<byte> propBytes, ImpunityCallback<uint> onComplete = null)
		{
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			Name = channelName;
			PropBytes = propBytes;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.CreateChannel(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, Name, PropBytes);
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

		public override ushort GetActionType() { return (ushort)ClientActionType.CREATE_OBJECT; }
		public override bool LiveDataQueue() { return true; }

		public CreateObjectAction() { }

		public CreateObjectAction(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, ImpunityCallback<uint> onComplete = null)
		{
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			ChannelId = channelId;
			PropBytes = propBytes;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.CreateObject(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, ChannelId, PropBytes);
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
		public override bool LiveDataQueue() { return true; }

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
		public override bool LiveDataQueue() { return true; }

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
		public override bool LiveDataQueue() { return true; }

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
		public override bool LiveDataQueue() { return true; }

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
		public override bool LiveDataQueue() { return true; }

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
		public override bool LiveDataQueue() { return true; }

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
		public override bool LiveDataQueue() { return true; }

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



	public class SubscribeChannelAction : ClientActionResultBase<uint>
	{
		[BsonField("cn")]
		public string Name;

		public override ushort GetActionType() { return (ushort)ClientActionType.SUBSCRIBE_CHANNEL; }
		public override bool LiveDataQueue() { return true; }

		public SubscribeChannelAction() { }

		public SubscribeChannelAction(string channelName, ImpunityCallback<uint> onComplete = null)
		{
			Name = channelName;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.SubscribeToChannel(Origin.ConnectionReplicant, Name);
		}
	}

	public class UnsubscribeChannelAction : ClientActionResultlessBase
	{
		[BsonField("cid")]
		public uint ID;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNSUBSCRIBE_CHANNEL; }
		public override bool LiveDataQueue() { return true; }

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


	public class SendBroadcastMessageAction : ClientActionResultlessBase
	{
		[BsonField("mt")]
		public int MessageType;

		[BsonField("mb")]
		public BsonValue MessageBody;

		public override ushort GetActionType() { return (ushort)ClientActionType.BROADCAST_MESSAGE; }
		public override bool LiveDataQueue() { return true; }

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