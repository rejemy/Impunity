using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;
using Impunity.Networking;

namespace Impunity.Connection
{
    /*
    public static class MessageHandlerFactory
    {
        public static Type GetActionClassType(int type)
        {
            ServerMessageTypes typeEnum;

            try
            {
                typeEnum = (ServerMessageTypes)type;
            }
            catch
            {
                throw new Exception("Unknown action type id: " + type);
            }

            return GetActionClassType(typeEnum);
        }

        public static Type GetActionClassType(ServerMessageTypes type)
        {
            switch (type)
            {
                case GameStateActionType.COMPOUND:
                    return typeof(CompoundAction);

                case GameStateActionType.SET_SUMMARY:
                    return typeof(SetGameSummaryAction);
                case GameStateActionType.GET_SUMMARY:
                    return typeof(GetGameSummaryAction);
                case GameStateActionType.ENSURE_FORMAT:
                    return typeof(EnsureFormatAction);
                case GameStateActionType.INSERT_DOCUMENT:
                    return typeof(InsertDocumentAction);
                case GameStateActionType.UPDATE_DOCUMENT:
                    return typeof(UpdateDocumentAction);
                case GameStateActionType.UPSERT_DOCUMENT:
                    return typeof(UpsertDocumentAction);
                case GameStateActionType.FIND_DOCUMENT_BY_ID:
                    return typeof(FindDocumentByIdAction);
                case GameStateActionType.DELETE_DOCUMENT:
                    return typeof(DeleteDocumentAction);
                case GameStateActionType.LIST_DOCUMENTS:
                    return typeof(ListDocumentsAction);

                case GameStateActionType.TRY_TO_LOCK:
                    return typeof(TryToLockAction);
                case GameStateActionType.UNLOCK:
                    return typeof(UnlockAction);

                case GameStateActionType.BROADCAST_MESSAGE:
                    return typeof(BroadcastMessageAction);
            }

            throw new Exception("Action type id with no entry in factory: " + type);
        }
    }
    */

    /*
    public class BroadcastMessageHandlerAction : ServerMessageBase<BroadcastMessage>
    {
        public override ushort GetActionType()
        {
            throw new NotImplementedException();
        }

        public override ActionResult GetResult()
        {
            throw new NotImplementedException();
        }

        public override Type GetResultType()
        {
            throw new NotImplementedException();
        }

        public override bool HasCallback()
        {
            throw new NotImplementedException();
        }

        public override void InvokeOnCompleteCallback()
        {
            throw new NotImplementedException();
        }

        protected override void DoAction(GameStateLive livestate, GameStateDB db)
        {
            throw new NotImplementedException();
        }
    }
    */

}