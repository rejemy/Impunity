using System;
using System.Collections.Generic;

using UltraLiteDB;


namespace Impunity.GameState
{

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


	public static class ServerActionFactory
	{
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

	

	public class ChannelCreateMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint ChannelId;

		[BsonField("n")]
		public string ChannelName;

		[BsonField("t")]
		public int ChannelType;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("obs")]
		public ObjectCreateMessageAction[] ObjectsInChannel;

		public override ushort GetActionType() { return (ushort)ServerActionType.CHANNEL_CREATE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleCreateChannel(ChannelId, ChannelName, ChannelType, PropBytes);
			foreach (ObjectCreateMessageAction objCreate in ObjectsInChannel)
			{
				handler.HandleCreateObject(objCreate.ObjectId, objCreate.ChannelId, objCreate.ObjectType, objCreate.PropBytes, objCreate.UniqueName, false);
			}
		}
	}

	public class ObjectCreateMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint ObjectId;

		[BsonField("cid")]
		public uint ChannelId;

		[BsonField("t")]
		public int ObjectType;

		[BsonField("pb")]
		public ArraySegment<byte> PropBytes;

		[BsonField("n")]
		public string UniqueName;

		public override ushort GetActionType() { return (ushort)ServerActionType.OBJECT_CREATE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleCreateObject(ObjectId, ChannelId, ObjectType, PropBytes, UniqueName, true);
		}
	}

	public class EntityUpdateMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("ub")]
		public ArraySegment<byte> UpdateBytes;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_UPDATE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityUpdate(EntityId, UpdateBytes);
		}
	}

	public class EntityEventMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("et")]
		public int EventType;

		[BsonField("ed")]
		public BsonValue EventData;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_EVENT_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityEvent(EntityId, EventType, EventData);
		}
	}

	public class EntityDeleteMessageAction : ServerActionBase
	{
		[BsonField("id")]
		public uint EntityId;

		[BsonField("dd")]
		public BsonValue DeleteData;

		public override ushort GetActionType() { return (ushort)ServerActionType.ENTITY_DELETE_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleEntityDelete(EntityId, DeleteData);
		}
	}

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


	public class NamedLockUnlockedMessageAction : ServerActionBase
	{
		[BsonField("ln")]
		public string Name;

		public override ushort GetActionType() { return (ushort)ServerActionType.NAMED_LOCK_UNLOCKED_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleNamedLockUnlocked(Name);
		}
	}

	public class BroadcastMessageAction : ServerActionBase
	{

		[BsonField("mt")]
		public int MessageType;

		[BsonField("mb")]
		public BsonValue MessageBody;

		[BsonField("s")]
		public string SentBy;

		public override ushort GetActionType() { return (ushort)ServerActionType.BROADCAST_MESSAGE; }

		// Called in client main thread
		public override void DoAction(IServerMessageHandler handler)
		{
			handler.HandleBroadcastMessage(MessageType, MessageBody, SentBy);
		}

	}

	// Actions sent to the DB from the Live server

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

	public class CreatePersistedEntityAction : ClientActionResultlessBase
	{
		public string EntityId;
		public string ChannelId;
		public int EntityType;
		public byte InstanceFlags;
		public List<LiveEntityPersistedPropertyData> Properties;
		


		public override ushort GetActionType() { throw new Exception("Not supported"); }
		public override bool IsDBOperation() { return true; }

		public CreatePersistedEntityAction(string entityId, string channelId, int entityType, byte instanceFlags, List<LiveEntityPersistedPropertyData> properties)
		{
			EntityId = entityId;
			ChannelId = channelId;
			EntityType = entityType;
			InstanceFlags = instanceFlags;
			Properties = properties;
		}

		protected override void DoAction(GameStateServer game)
		{
			game.DB.CreateLiveEntity(EntityId, ChannelId, EntityType, InstanceFlags, Properties);
		}
	}

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

	public class LiveEntityData
	{
		public string EntityId;
		public int EntityType;
		public byte InstanceFlags;

		public List<LiveEntityPersistedPropertyData> Properties;

		public LiveEntityData(string entityId)
		{
			EntityId = entityId;
		}
	}

	public class LiveChannelData : LiveEntityData
	{
		public List<LiveEntityData> ChannelObjects;

		public LiveChannelData(string channelName) : base(channelName)
		{
			
		}
	}

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