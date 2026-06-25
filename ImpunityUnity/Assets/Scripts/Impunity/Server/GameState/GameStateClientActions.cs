using System;
using System.Collections.Generic;

using UltraLiteDB;

namespace Impunity.GameState
{
	/// <summary>Numeric identifiers for all client-to-server action types. Ranges: 100s = metadata, 200s = database, 300s = live state, 400s = messaging.</summary>
	public enum ClientActionType
	{
		ESTABLISH_CONNECTION = 100,
		SET_SUMMARY = 101,
		GET_SUMMARY = 102,
		GET_TIME = 103,

		COMPOUND_DATABASE = 200,
		INSERT_DOCUMENT = 201,
		UPDATE_DOCUMENT = 202,
		UPSERT_DOCUMENT = 203,
		MERGE_INTO_DOCUMENT = 204,
		MERGE_INSERT_DOCUMENT = 205,
		FIND_DOCUMENT_BY_ID = 206,
		DELETE_DOCUMENT = 207,
		LIST_DOCUMENTS = 208,

		CREATE_CHANNEL = 300,
		SUBSCRIBE_CHANNEL = 301,
		UNSUBSCRIBE_CHANNEL = 302,
		CREATE_OBJECT = 303,
		UPDATE_ENTITY = 304,
		DELETE_ENTITY = 305,
		EVENT_ENTITY = 306,
		LOCK_ENTITY = 307,
		UNLOCK_ENTITY = 308,
		LOCK_NAMED_LOCK = 309,
		UNLOCK_NAMED_LOCK = 310,

		BROADCAST_MESSAGE = 400,
	}

	/// <summary>Maps <see cref="ClientActionType"/> codes to their concrete action class types for deserialization.</summary>
	public static class ClientActionFactory
	{
		/// <summary>Returns the action class type for the given numeric action type. Throws if unknown.</summary>
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

		/// <summary>Returns the action class type for the given <see cref="ClientActionType"/> enum value.</summary>
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
				case ClientActionType.GET_TIME:
					return typeof(GetTimeAction);

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

				case ClientActionType.CREATE_CHANNEL:
					return typeof(CreateChannelAction);
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


	/// <summary>Action that does nothing server-side. Used as a roundtrip ping or callback trigger.</summary>
	public class NoOpAction : ClientActionResultlessBase
	{
		public override bool IsDBOperation() { return false; }

		public NoOpAction() { }

		public NoOpAction(ImpunityCallback? onComplete)
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

	/// <summary>Result returned by <see cref="EstablishConnectionAction"/> containing the server-assigned connection ID.</summary>
	public class EstablishConnectResult
	{
		[BsonField("cid")]
		public string ConnectionId = null!;
	}

	/// <summary>Handshake action: validates game ID, password, and format version, then registers the connection. Returns the assigned connection ID.</summary>
	public class EstablishConnectionAction : ClientActionResultBase<EstablishConnectResult>
	{
		[BsonField("gid")]
		public string? GameId = null!;

		[BsonField("pw")]
		public string? PasswordHash;

		[BsonField("f")]
		public GameStateFormatData Format = null!;

		[BsonField("k")]
		public string ConnectionKey = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.ESTABLISH_CONNECTION; }
		public override bool IsDBOperation() { return false; }

		public EstablishConnectionAction() { }

		public EstablishConnectionAction(string? gameId, string? passwordHash, GameStateFormatData format, string connectionKey, ImpunityCallback<EstablishConnectResult>? onComplete = null)
		{
			GameId = gameId;
			PasswordHash = passwordHash;
			Format = format;
			ConnectionKey = connectionKey;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.EstablishConnection(Origin, Format);
		}
	}

	/// <summary>Sets the game world's summary document (visible in server discovery). Runs on the live thread.</summary>
	public class SetGameSummaryAction : ClientActionResultlessBase
	{
		[BsonField("s")]
		public BsonDocument Summary = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.SET_SUMMARY; }
		public override bool IsDBOperation() { return false; }

		public SetGameSummaryAction() { }

		public SetGameSummaryAction(BsonDocument summary, ImpunityCallback? onComplete = null)
		{
			Summary = summary;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.SetGameSummary(Summary);
		}
	}

	/// <summary>Retrieves the game world's summary document. Runs on the DB thread.</summary>
	public class GetGameSummaryAction : ClientActionResultBase<BsonDocument>
	{
		public override ushort GetActionType() { return (ushort)ClientActionType.GET_SUMMARY; }
		public override bool IsDBOperation() { return true; }

		public GetGameSummaryAction() { }

		public GetGameSummaryAction(ImpunityCallback<BsonDocument>? onComplete = null)
		{
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.GetGameSummary();
		}
	}

	/// <summary>Returns the server's current UTC time in Unix milliseconds. Runs immediately (no thread queuing).</summary>
	public class GetTimeAction : ClientActionResultBase<long>
	{
		public override ushort GetActionType() { return (ushort)ClientActionType.GET_TIME; }
		public override bool IsDBOperation() { return false; }
		public override bool IsImmediate() { return true; }

		public GetTimeAction() { }

		public GetTimeAction(ImpunityCallback<long>? onComplete = null)
		{
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = GameStateServer.GetServerTime();
		}
	}

	// Database operations

	/// <summary>Batches multiple DB actions into a single request. All sub-actions run sequentially on the DB thread. Returns a list of individual results.</summary>
	public class CompoundDatabaseAction : ClientActionResultBase<List<ActionResult>>
	{
		[BsonField("as")]
		public List<GameStateActionBase> Actions = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.COMPOUND_DATABASE; }
		public override bool IsDBOperation() { return true; }

		public CompoundDatabaseAction() { }

		public CompoundDatabaseAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>>? onComplete = null)
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


		// Custom deserializer so that we know what generic type to expect for each result in the list
		public override void DeserializeResults(ArraySegment<byte> messageBytes)
		{
			BsonDocument resultBody = BsonReader.Deserialize(messageBytes);

			BsonMapper mapper = ImpunityUtil.GetBsonMapper();

			BsonValue errorVal = resultBody["e"];
			if (!errorVal.IsNull)
			{
				Error = mapper.ToObject<ImpunityErrorResponse>(errorVal.AsDocument!);
			}

			BsonArray resultArray = (BsonArray)resultBody["r"];

			Result = new List<ActionResult>(resultArray.Count);

			for (int i = 0; i < Actions.Count; i++)
			{
				GameStateActionBase action = Actions[i];
				BsonDocument resultVal = resultArray[i].AsDocument!;

				Type resultType = action.GetResultType();

				Result.Add((ActionResult)mapper.ToObject(resultType, resultVal)!);
			}
		}

	}

	/// <summary>Inserts a new document into a DB collection. Returns the assigned document ID.</summary>
	public class InsertDocumentAction : ClientActionResultBase<BsonValue>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.INSERT_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public InsertDocumentAction() { }

		public InsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue>? onComplete = null)
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

	/// <summary>Replaces an existing document in a DB collection. Returns true if the document was found and updated.</summary>
	public class UpdateDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.UPDATE_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public UpdateDocumentAction() { }

		public UpdateDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete = null)
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

	/// <summary>Inserts or replaces a document in a DB collection. Returns true if the document was inserted as new, false if it replaced an existing one.</summary>
	public class UpsertDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.UPSERT_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public UpsertDocumentAction() { }

		public UpsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete = null)
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

	/// <summary>Merges fields from the given document into an existing document. Returns true if the target document was found.</summary>
	public class MergeIntoDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.MERGE_INTO_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public MergeIntoDocumentAction() { }

		public MergeIntoDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete = null)
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

	/// <summary>Merges fields into an existing document, or inserts as new if not found.</summary>
	public class MergeInsertDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("d")]
		public BsonDocument Doc = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.MERGE_INSERT_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public MergeInsertDocumentAction() { }

		public MergeInsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool>? onComplete = null)
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

	/// <summary>Retrieves a single document from a DB collection by its ID.</summary>
	public class FindDocumentByIdAction : ClientActionResultBase<BsonDocument>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("did")]
		public BsonValue Id = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.FIND_DOCUMENT_BY_ID; }
		public override bool IsDBOperation() { return true; }

		public FindDocumentByIdAction() { }

		public FindDocumentByIdAction(int collectionId, BsonValue id, ImpunityCallback<BsonDocument>? onComplete = null)
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

	/// <summary>Deletes a document from a DB collection by its ID. Returns true if found and deleted.</summary>
	public class DeleteDocumentAction : ClientActionResultBase<bool>
	{
		[BsonField("cid")]
		public int CollectionId;
		[BsonField("did")]
		public BsonValue Id = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.DELETE_DOCUMENT; }
		public override bool IsDBOperation() { return true; }

		public DeleteDocumentAction() { }

		public DeleteDocumentAction(int collectionId, BsonValue id, ImpunityCallback<bool>? onComplete = null)
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

	/// <summary>Lists all documents in a DB collection.</summary>
	public class ListDocumentsAction : ClientActionResultBase<List<BsonDocument>>
	{
		[BsonField("cid")]
		public int CollectionId;

		public override ushort GetActionType() { return (ushort)ClientActionType.LIST_DOCUMENTS; }
		public override bool IsDBOperation() { return true; }

		public ListDocumentsAction() { }

		public ListDocumentsAction(int collectionId, ImpunityCallback<List<BsonDocument>>? onComplete = null)
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

	/// <summary>Serializable data for creating a live entity: type ID, instance flags, initial property bytes, and optional unique name.</summary>
	public class ObjectCreateData
	{
		[BsonField("t")]
		public int EntityTypeId;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("n")]
		public string? UniqueName;

		public ObjectCreateData() { }

		public ObjectCreateData(int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, string? uniqueName)
		{
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			PropBytes = propBytes;
			UniqueName = uniqueName;
		}

	}

	/// <summary>Creates a new live state channel with an optional initial entity and child objects. Returns true on success.</summary>
	public class CreateChannelAction : ClientActionResultBase<bool>
	{
		[BsonField("cn")]
		public string Name = null!;

		[BsonField("rep")]
		public bool ReplaceIfPresent;

		[BsonField("t")]
		public int EntityTypeId;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("os")]
		public List<ObjectCreateData>? Objects;


		public override ushort GetActionType() { return (ushort)ClientActionType.CREATE_CHANNEL; }
		public override bool IsDBOperation() { return false; }

		public CreateChannelAction() { }

		public CreateChannelAction(string channelName, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, bool replace, ImpunityCallback<bool>? onComplete = null)
		{
			Name = channelName;
			ReplaceIfPresent = replace;
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			PropBytes = propBytes;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.CreateChannel(Origin.ConnectionReplicant, Name, ReplaceIfPresent, EntityTypeId, InstanceFlags, PropBytes, Objects);
		}
	}


	/// <summary>Subscribes the connection to a channel, loading it from DB if needed. Returns the channel's entity ID. Implements <see cref="GameStateChannelLoadListener"/> for async DB load completion.</summary>
	public class SubscribeChannelAction : ClientActionResultBase<uint>, GameStateChannelLoadListener
	{
		[BsonField("cn")]
		public string Name = null!;

		[BsonField("cim")]
		public bool CreateIfMissing;

		[BsonField("t")]
		public int EntityTypeId;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("os")]
		public List<ObjectCreateData>? Objects;

		public override ushort GetActionType() { return (ushort)ClientActionType.SUBSCRIBE_CHANNEL; }
		public override bool IsDBOperation() { return false; }

		public SubscribeChannelAction() { }

		public SubscribeChannelAction(string channelName, bool createIfMissing, int entityTypeId, byte instanceFlags, ArraySegment<byte> propBytes, ImpunityCallback<uint>? onComplete = null)
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
			GameStateEntity? entity = game.Live.GetNamedEntity(Name);
			if (entity == null)
			{
				AwaitingTask = true;

				GameStateChannelLoadProxy loadProxy = game.Live.LoadChannel(Name);
				loadProxy.AddListener(this);
				if (CreateIfMissing)
				{
					loadProxy.SetCreateIfMissing(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, PropBytes, Objects);
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
					loadProxy.SetCreateIfMissing(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, PropBytes, Objects);
				}

				return;
			}

			SubscribeToChannelAction(game);
		}

		public void OnChannelLoaded(GameStateServer game, ImpunityErrorResponse? error, GameStateChannel loadedChannel)
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

	/// <summary>Unsubscribes the connection from a channel by its entity ID.</summary>
	public class UnsubscribeChannelAction : ClientActionResultlessBase
	{
		[BsonField("cid")]
		public uint ID;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNSUBSCRIBE_CHANNEL; }
		public override bool IsDBOperation() { return false; }

		public UnsubscribeChannelAction() { }

		public UnsubscribeChannelAction(uint channelId, ImpunityCallback? onComplete = null)
		{
			ID = channelId;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.UnsubscribeFromChannel(Origin.ConnectionReplicant, ID);
		}
	}

	/// <summary>Creates a new entity object within a channel. Returns the assigned entity ID.</summary>
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
		public string? UniqueName;

		[BsonField("r")]
		public bool ReplaceExisting;

		public override ushort GetActionType() { return (ushort)ClientActionType.CREATE_OBJECT; }
		public override bool IsDBOperation() { return false; }

		public CreateObjectAction() { }

		public CreateObjectAction(int entityTypeId, byte instanceFlags, uint channelId, ArraySegment<byte> propBytes, string? uniqueName, bool replace, ImpunityCallback<uint>? onComplete = null)
		{
			EntityTypeId = entityTypeId;
			InstanceFlags = instanceFlags;
			ChannelId = channelId;
			PropBytes = propBytes;
			UniqueName = uniqueName;
			ReplaceExisting = replace;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.CreateObject(Origin.ConnectionReplicant, EntityTypeId, InstanceFlags, ChannelId, PropBytes, UniqueName, ReplaceExisting, false);
		}
	}

	/// <summary>Applies a property update to a live entity using serialized update bytes.</summary>
	public class UpdateEntityAction : ClientActionResultlessBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("ub")]
		public ArraySegment<byte> UpdateBytes;

		[BsonField("sq")]
		public ushort Seq;

		public override ushort GetActionType() { return (ushort)ClientActionType.UPDATE_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public UpdateEntityAction() { }

		public UpdateEntityAction(uint entityId, ArraySegment<byte> updateBytes, ushort seq, ImpunityCallback? onComplete = null)
		{
			EntityId = entityId;
			UpdateBytes = updateBytes;
			Seq = seq;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.UpdateEntity(Origin.ConnectionReplicant, EntityId, UpdateBytes, this.Guaranteed, Seq);
		}
	}

	/// <summary>Deletes a live entity by ID, with optional associated data for cleanup.</summary>
	public class DeleteEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("dd")]
		public BsonValue DeleteData = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.DELETE_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public DeleteEntityAction() { }

		public DeleteEntityAction(uint entityId, BsonValue deleteData, ImpunityCallback<bool>? onComplete = null)
		{
			EntityId = entityId;
			DeleteData = deleteData;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.DeleteEntity(Origin.ConnectionReplicant, EntityId, DeleteData);
		}
	}

	/// <summary>Sends a one-shot event to all subscribers of an entity's channel.</summary>
	public class EventEntityAction : ClientActionResultlessBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("mt")]
		public int EventType;

		[BsonField("mb")]
		public BsonValue EventData = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.EVENT_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public EventEntityAction() { }

		public EventEntityAction(uint entityId, int eventType, BsonValue eventData, ImpunityCallback? onComplete = null)
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

	/// <summary>Acquires an exclusive lock on a live entity for this connection.</summary>
	public class LockEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		public override ushort GetActionType() { return (ushort)ClientActionType.LOCK_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public LockEntityAction() { }

		public LockEntityAction(uint entityId, ImpunityCallback<bool>? onComplete = null)
		{
			EntityId = entityId;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.LockEntity(Origin.ConnectionReplicant, EntityId);
		}
	}

	/// <summary>Releases an exclusive lock on a live entity.</summary>
	public class UnlockEntityAction : ClientActionResultBase<bool>
	{
		[BsonField("id")]
		public uint EntityId;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNLOCK_ENTITY; }
		public override bool IsDBOperation() { return false; }

		public UnlockEntityAction() { }

		public UnlockEntityAction(uint entityId, ImpunityCallback<bool>? onComplete = null)
		{
			EntityId = entityId;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.Live.UnlockEntity(Origin.ConnectionReplicant, EntityId);
		}
	}

	/// <summary>Acquires a named mutex lock. Optionally waits if currently held by another connection.</summary>
	public class LockNamedLockAction : ClientActionResultBase<bool>
	{
		[BsonField("ln")]
		public string Name = null!;

		[BsonField("w")]
		public bool WaitForUnlock;

		public override ushort GetActionType() { return (ushort)ClientActionType.LOCK_NAMED_LOCK; }
		public override bool IsDBOperation() { return false; }

		public LockNamedLockAction() { }

		public LockNamedLockAction(string lockName, bool wait, ImpunityCallback<bool>? onComplete = null)
		{
			Name = lockName;
			WaitForUnlock = wait;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.TryToLockNamedLock(Origin.ConnectionReplicant, Name, WaitForUnlock);
		}
	}

	/// <summary>Releases a named mutex lock.</summary>
	public class UnlockNamedLockAction : ClientActionResultBase<bool>
	{
		[BsonField("ln")]
		public string Name = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.UNLOCK_NAMED_LOCK; }
		public override bool IsDBOperation() { return false; }

		public UnlockNamedLockAction() { }

		public UnlockNamedLockAction(string lockName, ImpunityCallback<bool>? onComplete = null)
		{
			Name = lockName;
			OnCompleteCallback = onComplete;
		}

		protected override void DoAction(GameStateServer game)
		{
			Result = game.Live.UnlockNamedLock(Origin.ConnectionReplicant, Name);
		}
	}

	/// <summary>Broadcasts a typed message to all connected clients.</summary>
	public class SendBroadcastMessageAction : ClientActionResultlessBase
	{
		[BsonField("mt")]
		public int MessageType;

		[BsonField("mb")]
		public BsonValue MessageBody = null!;

		public override ushort GetActionType() { return (ushort)ClientActionType.BROADCAST_MESSAGE; }
		public override bool IsDBOperation() { return false; }

		public SendBroadcastMessageAction() { }

		public SendBroadcastMessageAction(int messageType, BsonValue message, ImpunityCallback? onComplete = null)
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
