using System;
using System.Collections.Generic;

using UltraLiteDB;

using Impunity.Networking;

namespace Impunity.GameState
{
    public enum ClientActionType
    {
        COMPOUND = 1,

        SET_SUMMARY = 101,
        GET_SUMMARY = 102,
        ENSURE_FORMAT = 103,
        INSERT_DOCUMENT = 104,
        UPDATE_DOCUMENT = 105,
        UPSERT_DOCUMENT = 106,
        FIND_DOCUMENT_BY_ID = 107,
        DELETE_DOCUMENT = 108,
        LIST_DOCUMENTS = 109,

        TRY_TO_LOCK = 201,
        UNLOCK = 202,

        BROADCAST_MESSAGE = 301,
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

                case ClientActionType.SET_SUMMARY:
                    return typeof(SetGameSummaryAction);
                case ClientActionType.GET_SUMMARY:
                    return typeof(GetGameSummaryAction);
                case ClientActionType.ENSURE_FORMAT:
                    return typeof(EnsureFormatAction);
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

                case ClientActionType.TRY_TO_LOCK:
                    return typeof(TryToLockAction);
                case ClientActionType.UNLOCK:
                    return typeof(UnlockAction);

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

    public class EnsureFormatAction : ClientActionResultlessBase
    {
        [BsonField("f")]
        public GameStateFormat Format;

        public override ushort GetActionType() { return (ushort)ClientActionType.ENSURE_FORMAT; }

        public EnsureFormatAction() { }

        public EnsureFormatAction(GameStateFormat format, ImpunityCallback onComplete = null)
        {
            Format = format;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateServer game)
        {
            game.EnsureFormat(Format);
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

            bool error = false;
            foreach (GameStateActionBase action in Actions)
            {
                action.Run(game);

                Result.Add(action.GetResult());

                if (action.Error != null)
                {
                    error = true;
                }
            }

            if (error)
            {
                Error = new ImpunityError("Compound action error");
            }
        }


        // Custom deserializer so that we know what generic type to expect for each result nin the list
        public override void DeserializeResults(BsonDocument resultBody)
        {
            BsonMapper mapper = ImpunityNetworkingUtil.GetBsonMapper();

            BsonValue errorVal = resultBody["e"];
            if (!errorVal.IsNull)
            {
                Error = mapper.ToObject<ImpunityError>(errorVal.AsDocument);
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

    public class TryToLockAction : ClientActionResultBase<bool>
    {
        [BsonField("ln")]
        public string Name;

        [BsonField("k")]
        public string Key;

        public override ushort GetActionType() { return (ushort)ClientActionType.TRY_TO_LOCK; }

        public TryToLockAction() { }

        public TryToLockAction(string lockName, string key, ImpunityCallback<bool> onComplete = null)
        {
            Name = lockName;
            Key = key;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateServer game)
        {
            Result = game.Live.TryToLock(Origin.ConnectionReplicant, Name, Key);
        }
    }

    public class UnlockAction : ClientActionResultBase<bool>
    {
        [BsonField("ln")]
        public string Name;

        [BsonField("k")]
        public string Key;

        public override ushort GetActionType() { return (ushort)ClientActionType.UNLOCK; }

        public UnlockAction() { }

        public UnlockAction(string lockName, string key, ImpunityCallback<bool> onComplete = null)
        {
            Name = lockName;
            Key = key;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateServer game)
        {
            Result = game.Live.Unlock(Origin.ConnectionReplicant, Name, Key);
        }
    }

    public class EntityUpdate
    {
        public int EntityId;
        bool Create;

    }

    public class SendBroadcastMessageAction : ClientActionResultlessBase
    {
        [BsonField("mt")]
        public int MessageType;

        [BsonField("mb")]
        public BsonValue MessageBody;

        public override ushort GetActionType() { return (ushort)ClientActionType.BROADCAST_MESSAGE; }


        public SendBroadcastMessageAction() { }

        public SendBroadcastMessageAction(int messageType, BsonValue message)
        {
            MessageType = messageType;
            MessageBody = message;
        }

        protected override void DoAction(GameStateServer game)
        {
            game.Live.SendBroadcastMessage(MessageType, MessageBody, Origin.ConnectionId);
        }
    }

}