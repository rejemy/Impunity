using System;


using UltraLiteDB;

using Impunity.Connection;

namespace Impunity.GameState
{

    public enum ServerActionType
    {
        CLIENT_REPLY = 1,

        BROADCAST_MESSAGE = 100,
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
                case ServerActionType.BROADCAST_MESSAGE:
                    return typeof(BroadcastMessageAction);
            }

            throw new Exception("Action type id with no entry in factory: " + type);
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

        // Called in main thread
        public override void DoAction(BaseGameConnection connection)
        {
            try
            {
                connection.OnBroadcastMessage?.Invoke(MessageType, MessageBody, SentBy);
            }
            catch(Exception e)
            {
                ImpunityLogger.LogError(e, "Exception in OnBroadcastMessage handler");
            }
        }

        
    }
}