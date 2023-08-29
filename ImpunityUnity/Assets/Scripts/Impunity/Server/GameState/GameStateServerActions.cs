using System;


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

        BROADCAST_MESSAGE = 200,
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

                case ServerActionType.BROADCAST_MESSAGE:
                    return typeof(BroadcastMessageAction);
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
        public byte[] PropBytes;

        [BsonField("obs")]
        public ObjectCreateMessageAction[] ObjectsInChannel;

        public override ushort GetActionType() { return (ushort)ServerActionType.CHANNEL_CREATE_MESSAGE; }

        // Called in client main thread
        public override void DoAction(IServerMessageHandler handler)
        {
            handler.HandleCreateChannel(ChannelId, ChannelName, ChannelType, PropBytes);
            foreach (ObjectCreateMessageAction objCreate in ObjectsInChannel)
            {
                handler.HandleCreateObject(objCreate.ObjectId, objCreate.ChannelId, objCreate.ObjectType, objCreate.PropBytes);
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
        public byte[] PropBytes;

        public override ushort GetActionType() { return (ushort)ServerActionType.OBJECT_CREATE_MESSAGE; }

        // Called in client main thread
        public override void DoAction(IServerMessageHandler handler)
        {
            handler.HandleCreateObject(ObjectId, ChannelId, ObjectType, PropBytes);
        }
    }

    public class EntityUpdateMessageAction : ServerActionBase
    {
        [BsonField("id")]
        public uint EntityId;

        [BsonField("ub")]
        public byte[] UpdateBytes;

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
}