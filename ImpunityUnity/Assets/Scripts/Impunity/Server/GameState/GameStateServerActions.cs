using System;
using System.Collections.Generic;

using UltraLiteDB;


namespace Impunity.GameState
{

	/// <summary>Numeric identifiers for server-to-client push messages. 100s = entity/channel state, 200s = messaging/locks.</summary>
	public enum ServerActionType
	{
		CLIENT_REPLY = 1,

		CHANNEL_CREATE_MESSAGE = 100,
		OBJECT_CREATE_MESSAGE = 101,
		ENTITY_UPDATE_MESSAGE = 102,
		ENTITY_EVENT_MESSAGE = 103,
		ENTITY_DELETE_MESSAGE = 104,
		ENTITY_LOCKED_MESSAGE = 105,
		ENTITY_UNLOCKED_MESSAGE = 106,

		BROADCAST_MESSAGE = 200,
		NAMED_LOCK_UNLOCKED_MESSAGE = 201,
	}


	/// <summary>Maps <see cref="ServerActionType"/> codes to their concrete server message class types for deserialization.</summary>
	public static class ServerActionFactory
	{
		/// <summary>Returns the server action class type for the given numeric type. Throws if unknown.</summary>
		public static Type GetActionClassType(int type)
		{
			ServerActionType typeEnum;

			try
			{
				typeEnum = (ServerActionType)type;
			}
			catch
			{
				throw new Exception("Unknown server action type id: " + type);
			}

			return GetActionClassType(typeEnum);
		}

		public static Type GetActionClassType(ServerActionType type)
		{
			switch (type)
			{
				case ServerActionType.CLIENT_REPLY:
					throw new Exception("Tried to get classtype for a server action reply");

				case ServerActionType.CHANNEL_CREATE_MESSAGE:
					return typeof(ChannelCreateMessageAction);
				case ServerActionType.OBJECT_CREATE_MESSAGE:
					return typeof(ObjectCreateMessageAction);
				case ServerActionType.ENTITY_UPDATE_MESSAGE:
					return typeof(EntityUpdateMessageAction);
				case ServerActionType.ENTITY_EVENT_MESSAGE:
					return typeof(EntityEventMessageAction);
				case ServerActionType.ENTITY_DELETE_MESSAGE:
					return typeof(EntityDeleteMessageAction);
				case ServerActionType.ENTITY_LOCKED_MESSAGE:
					return typeof(EntityLockedMessageAction);
				case ServerActionType.ENTITY_UNLOCKED_MESSAGE:
					return typeof(EntityUnlockedMessageAction);

				case ServerActionType.BROADCAST_MESSAGE:
					return typeof(BroadcastMessageAction);

				case ServerActionType.NAMED_LOCK_UNLOCKED_MESSAGE:
					return typeof(NamedLockUnlockedMessageAction);
			}

			throw new Exception("Action type id with no entry in factory: " + type);
		}
	}



	/// <summary>Server push: a channel was created or the client subscribed to it. Includes the channel entity and all child objects.</summary>
	public class ChannelCreateMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint ChannelId;

		[BsonField("n")]
		public string ChannelName = null!;

		[BsonField("t")]
		public int ChannelType;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("l")]
		public bool IsLocked;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		/// <summary>The channel entity's current <c>OutSeq</c> at snapshot time. The client seeds every field's received-seq
		/// with it so exclusive updates are not falsely rejected against modifications made before this client subscribed.</summary>
		[BsonField("sq")]
		public ushort Seq;

		[BsonField("obs")]
		public ObjectCreateMessageAction[] ObjectsInChannel = Array.Empty<ObjectCreateMessageAction>();

		public override ushort GetActionType() { return (ushort)ServerActionType.CHANNEL_CREATE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleCreateChannel(ChannelId, ChannelName, ChannelType, IsLocked, InstanceFlags, PropBytes, Seq);
			foreach (ObjectCreateMessageAction objCreate in ObjectsInChannel)
			{
				handler.HandleCreateObject(objCreate.ObjectId, objCreate.ChannelId, objCreate.ObjectType, objCreate.IsLocked,
														objCreate.InstanceFlags, objCreate.PropBytes, objCreate.UniqueName, false, objCreate.Seq);
			}
		}
	}

	/// <summary>Server push: a new object was created in a channel the client is subscribed to.</summary>
	public class ObjectCreateMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint ObjectId;

		[BsonField("cid")]
		public uint ChannelId;

		[BsonField("t")]
		public int ObjectType;

		[BsonField("if")]
		public byte InstanceFlags;

		[BsonField("l")]
		public bool IsLocked;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("n")]
		public string? UniqueName;

		/// <summary>The object entity's current <c>OutSeq</c> at snapshot time. See <see cref="ChannelCreateMessageAction.Seq"/>.</summary>
		[BsonField("sq")]
		public ushort Seq;

		public override ushort GetActionType() { return (ushort)ServerActionType.OBJECT_CREATE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleCreateObject(ObjectId, ChannelId, ObjectType, IsLocked, InstanceFlags, PropBytes, UniqueName, true, Seq);
		}
	}

	/// <summary>Server push: an entity's properties were updated.</summary>
	public class EntityUpdateMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("ub")]
		public ArraySegment<byte> UpdateBytes;

		[BsonField("sq")]
		public ushort Seq;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_UPDATE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityUpdate(EntityId, UpdateBytes, Seq);
		}
	}

	/// <summary>Server push: a one-shot event was fired on an entity.</summary>
	public class EntityEventMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("et")]
		public int EventType;

		[BsonField("ed")]
		public BsonValue EventData = null!;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_EVENT_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityEvent(EntityId, EventType, EventData);
		}
	}

	/// <summary>Server push: an entity was deleted.</summary>
	public class EntityDeleteMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("dd")]
		public BsonValue? DeleteData;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_DELETE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityDelete(EntityId, DeleteData);
		}
	}

	/// <summary>Server push: an entity was locked by a connection.</summary>
	public class EntityLockedMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_LOCKED_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityLocked(EntityId);
		}
	}

	/// <summary>Server push: an entity was unlocked.</summary>
	public class EntityUnlockedMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_UNLOCKED_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityUnlocked(EntityId);
		}
	}


	/// <summary>Server push: a named lock was released, notifying waiting clients.</summary>
	public class NamedLockUnlockedMessageAction : ServerActionBase
	{
		[BsonField("ln")]
		public string Name = null!;

		public override ushort GetActionType() { return (ushort)ServerActionType.NAMED_LOCK_UNLOCKED_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleNamedLockUnlocked(Name);
		}
	}

	/// <summary>Server push: a broadcast message sent by another client.</summary>
	public class BroadcastMessageAction : ServerActionBase
	{

		[BsonField("mt")]
		public int MessageType;

		[BsonField("mb")]
		public BsonValue MessageBody = null!;

		[BsonField("s")]
		public string SentBy = null!;

		public override ushort GetActionType() { return (ushort)ServerActionType.BROADCAST_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleBroadcastMessage(MessageType, MessageBody, SentBy);
		}

	}

	// -----  Actions sent to the DB from the Live server

	/// <summary>Internal action: updates the DB schema and persists metadata when the game format changes. Queued from the live thread to the DB thread.</summary>
	public class UpdateDBFormatAction : ClientActionResultlessBase
	{
		public GameStateCollection[] Collections;
		public GameMetadata Metadata;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public UpdateDBFormatAction(GameStateCollection[] collections, GameMetadata metadata)
		{
			Collections = collections;
			Metadata = metadata;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.DB.SetFormat(Collections);
			game.DB.SaveMetadata(Metadata);
		}
	}

	// ----- Internal migration DB actions
	//
	// Each runs its file work on the DB thread, then hops back to the live thread (via QueueDBReply, which makes the
	// live worker call InvokeOnCompleteCallback) to finish the corresponding migration step where the migration state
	// lives. See GameStateServer.HandleBeginMigration / HandleCommitMigration / HandleAbortMigration.

	/// <summary>Internal DB action: snapshots the database and writes the migration marker, then resumes the begin-migration handshake.</summary>
	public class MigrationBackupDBAction : ClientActionResultlessBase
	{
		private readonly MigrationMarker Marker;
		private readonly GameStateActionBase BeginAction;
		private GameStateServer Game = null!;
		private bool BackupOk;
		private ImpunityErrorResponse? BackupError;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public MigrationBackupDBAction(MigrationMarker marker, GameStateActionBase beginAction)
		{
			Marker = marker;
			BeginAction = beginAction;
		}

		protected override void DoAction(GameStateServer game)
		{
			Game = game;
			try
			{
				game.DB.BackupForMigration(Marker);
				BackupOk = true;
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Migration backup failed", e);
				BackupOk = false;
				BackupError = new ImpunityErrorResponse(ImpunityErrorCode.InternalServerError, e);
			}
			game.QueueDBReply(this);
		}

		public override void InvokeOnCompleteCallback()
		{
			Game.FinishBeginMigration(BeginAction, BackupOk, BackupError);
		}
	}

	/// <summary>Internal DB action: deletes the snapshot and marker after a successful commit, then finishes the commit.</summary>
	public class MigrationFinalizeDBAction : ClientActionResultlessBase
	{
		private readonly GameStateActionBase CommitAction;
		private GameStateServer Game = null!;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public MigrationFinalizeDBAction(GameStateActionBase commitAction)
		{
			CommitAction = commitAction;
		}

		protected override void DoAction(GameStateServer game)
		{
			Game = game;
			try
			{
				game.DB.ClearMigrationFiles();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Migration finalize cleanup failed", e);
			}
			game.QueueDBReply(this);
		}

		public override void InvokeOnCompleteCallback()
		{
			Game.FinishCommitMigration(CommitAction);
		}
	}

	/// <summary>Internal DB action: restores the pre-migration snapshot on abort, then finishes the abort.</summary>
	public class MigrationRestoreDBAction : ClientActionResultlessBase
	{
		private readonly GameStateCollection[] Collections;
		private readonly GameStateActionBase? AbortAction;
		private GameStateServer Game = null!;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public MigrationRestoreDBAction(GameStateCollection[] collections, GameStateActionBase? abortAction)
		{
			Collections = collections;
			AbortAction = abortAction;
		}

		protected override void DoAction(GameStateServer game)
		{
			Game = game;
			try
			{
				game.DB.RestoreFromMigrationBackup(Collections);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Migration restore failed", e);
			}
			game.QueueDBReply(this);
		}

		public override void InvokeOnCompleteCallback()
		{
			Game.FinishAbortMigration(AbortAction);
		}
	}

	/// <summary>Name-value pair for a single persisted property of a live entity.</summary>
	public class LiveEntityPersistedPropertyData
	{
		public string PropertyName;
		public BsonValue PropertyValue;

		public LiveEntityPersistedPropertyData(string propName, BsonValue value)
		{
			PropertyName = propName;
			PropertyValue = value;
		}
	}

	/// <summary>Internal DB action: persists a newly created live entity to the database.</summary>
	public class CreatePersistedEntityAction : ClientActionResultlessBase
	{
		public string EntityId;
		public string ChannelId;
		public bool IsChannel;
		/// <summary>The entity type's PersistAs key — the durable type identity stored in the database.</summary>
		public string EntityTypeKey;
		public byte InstanceFlags;
		public List<LiveEntityPersistedPropertyData>? Properties;



		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public CreatePersistedEntityAction(string entityId, string channelId, bool isChannel, string entityTypeKey, byte instanceFlags, List<LiveEntityPersistedPropertyData>? properties)
		{
			EntityId = entityId;
			ChannelId = channelId;
			IsChannel = isChannel;
			EntityTypeKey = entityTypeKey;
			InstanceFlags = instanceFlags;
			Properties = properties;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.DB.CreateLiveEntity(EntityId, ChannelId, IsChannel, EntityTypeKey, InstanceFlags, Properties);
		}
	}

	/// <summary>Internal DB action: updates persisted properties of an existing live entity.</summary>
	public class UpdatePersistedEntityPropertiesAction : ClientActionResultlessBase
	{
		public string EntityId;
		public string ChannelId;
		public List<LiveEntityPersistedPropertyData> Properties;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public UpdatePersistedEntityPropertiesAction(string entityId, string channelId, List<LiveEntityPersistedPropertyData> properties)
		{
			EntityId = entityId;
			ChannelId = channelId;
			Properties = properties;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.DB.UpdateLiveEntityProperties(EntityId, ChannelId, Properties);
		}
	}

	/// <summary>Internal DB action: deletes a persisted channel and all its child entities from the database.</summary>
	public class DeletePersistedChannelAction : ClientActionResultlessBase
	{
		public string ChannelId;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public DeletePersistedChannelAction(string channelId)
		{
			ChannelId = channelId;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.DB.DeleteLiveChannel(ChannelId);
		}
	}

	/// <summary>Internal DB action: deletes a single persisted object from the database.</summary>
	public class DeletePersistedObjectAction : ClientActionResultlessBase
	{
		public string ObjectId;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public DeletePersistedObjectAction(string objectId)
		{
			ObjectId = objectId;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.DB.DeleteLiveObject(ObjectId);
		}
	}

	/// <summary>Data loaded from DB for a single live entity: ID, type, flags, and persisted properties.</summary>
	public class LiveEntityData
	{
		public string EntityId;
		/// <summary>The entity type's PersistAs key as stored in the database (the <c>t</c> value); resolved
		/// against the live type registry on load.</summary>
		public string? EntityTypeKey;
		public byte InstanceFlags;

		public List<LiveEntityPersistedPropertyData> Properties = null!;

		public LiveEntityData(string entityId)
		{
			EntityId = entityId;
		}
	}

	/// <summary>Data loaded from DB for a channel entity, including its child objects.</summary>
	public class LiveChannelData : LiveEntityData
	{
		public List<LiveEntityData> ChannelObjects = null!;

		public LiveChannelData(string channelName) : base(channelName)
		{

		}
	}

	/// <summary>Internal DB action: loads a channel and its child objects from the database. Queues itself back to the live thread on completion via <see cref="GameStateServer.QueueDBReply"/>.</summary>
	public class LoadChannelAction : ClientActionResultBase<LiveChannelData>
	{
		public string ChannelId;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public LoadChannelAction(string channelId)
		{
			ChannelId = channelId;
		}

		// Run in DB thread
		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.LoadChannelData(ChannelId);
			game.QueueDBReply(this);
		}
	}

	/// <summary>Internal DB action: checks whether a named entity exists in the database. Queues itself back to the live thread on completion.</summary>
	public class CheckEntityExistanceAction : ClientActionResultBase<bool>
	{
		public string Name;

		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public CheckEntityExistanceAction(string name)
		{
			Name = name;
		}

		// Run in DB thread
		protected override void DoAction(GameStateServer game)
		{
			Result = game.DB.DoesNamedEntityExistInDB(Name);
			game.QueueDBReply(this);
		}
	}
}
