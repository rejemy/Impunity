using System;
using System.Collections.Generic;

using UltraLiteDB;

using Impunity.Networking;

namespace Impunity.GameState
{
    public enum GameStateActionType
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
    }

    public static class GameActionFactory
    {
        public static Type GetActionClassType(int type)
        {
            GameStateActionType typeEnum;

            try
            {
                typeEnum = (GameStateActionType)type;
            }
            catch
            {
                throw new Exception("Unknown action type id: " + type);
            }

            return GetActionClassType(typeEnum);
        }

        public static Type GetActionClassType(GameStateActionType type)
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
            }

            throw new Exception("Action type id with no entry in factory: " + type);
        }
    }


    public class NoOpAction : GameStateActionResultlessBase
    {
        public NoOpAction() { }

        public NoOpAction(ImpunityCallback onComplete)
        {
            OnCompleteCallback = onComplete;
        }

        public override ushort GetActionType() { return 0; }
        public override bool HasCallback() { return true; }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            // Is actually a no-op
            throw new NotImplementedException();
        }
    }

    public class SetGameSummaryAction : GameStateActionResultlessBase
    {
        [BsonField("s")]
        public BsonDocument Summary;

        public override ushort GetActionType() { return (ushort)GameStateActionType.SET_SUMMARY; }

        public SetGameSummaryAction() { }

        public SetGameSummaryAction(BsonDocument summary, ImpunityCallback onComplete = null)
        {
            Summary = summary;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            db.SetGameSummary(Summary);
        }
    }

    public class GetGameSummaryAction : GameStateActionResultBase<BsonDocument>
    {
        public override ushort GetActionType() { return (ushort)GameStateActionType.GET_SUMMARY; }

        public GetGameSummaryAction() { }

        public GetGameSummaryAction(ImpunityCallback<BsonDocument> onComplete = null)
        {
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.GetGameSummary();
        }
    }

    public class EnsureFormatAction : GameStateActionResultlessBase
    {
        [BsonField("f")]
        public GameStateFormat Format;

        public override ushort GetActionType() { return (ushort)GameStateActionType.ENSURE_FORMAT; }

        public EnsureFormatAction() { }

        public EnsureFormatAction(GameStateFormat format, ImpunityCallback onComplete = null)
        {
            Format = format;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            db.EnsureFormat(Format);
        }
    }

    public class InsertDocumentAction : GameStateActionResultBase<BsonValue>
    {
        [BsonField("cid")]
        public int CollectionId;
        [BsonField("d")]
        public BsonDocument Doc;

        public override ushort GetActionType() { return (ushort)GameStateActionType.INSERT_DOCUMENT; }

        public InsertDocumentAction() { }

        public InsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete = null)
        {
            CollectionId = collectionId;
            Doc = doc;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.InsertDocument(CollectionId, Doc);
        }
    }

    public class UpdateDocumentAction : GameStateActionResultBase<bool>
    {
        [BsonField("cid")]
        public int CollectionId;
        [BsonField("d")]
        public BsonDocument Doc;

        public override ushort GetActionType() { return (ushort)GameStateActionType.UPDATE_DOCUMENT; }

        public UpdateDocumentAction() { }

        public UpdateDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete = null)
        {
            CollectionId = collectionId;
            Doc = doc;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.UpdateDocument(CollectionId, Doc);
        }
    }

    public class UpsertDocumentAction : GameStateActionResultBase<bool>
    {
        [BsonField("cid")]
        public int CollectionId;
        [BsonField("d")]
        public BsonDocument Doc;

        public override ushort GetActionType() { return (ushort)GameStateActionType.UPSERT_DOCUMENT; }

        public UpsertDocumentAction() { }

        public UpsertDocumentAction(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete = null)
        {
            CollectionId = collectionId;
            Doc = doc;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.UpsertDocument(CollectionId, Doc);
        }
    }

    public class FindDocumentByIdAction : GameStateActionResultBase<BsonDocument>
    {
        [BsonField("cid")]
        public int CollectionId;
        [BsonField("did")]
        public BsonValue Id;

        public override ushort GetActionType() { return (ushort)GameStateActionType.FIND_DOCUMENT_BY_ID; }

        public FindDocumentByIdAction() { }

        public FindDocumentByIdAction(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete = null)
        {
            CollectionId = collectionId;
            Id = id;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.FindDocumentById(CollectionId, Id);
        }
    }

    public class DeleteDocumentAction : GameStateActionResultBase<bool>
    {
        [BsonField("cid")]
        public int CollectionId;
        [BsonField("did")]
        public BsonValue Id;

        public override ushort GetActionType() { return (ushort)GameStateActionType.DELETE_DOCUMENT; }

        public DeleteDocumentAction() { }

        public DeleteDocumentAction(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete = null)
        {
            CollectionId = collectionId;
            Id = id;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.DeleteDocument(CollectionId, Id);
        }
    }

    public class ListDocumentsAction : GameStateActionResultBase<List<BsonDocument>>
    {
        [BsonField("cid")]
        public int CollectionId;

        public override ushort GetActionType() { return (ushort)GameStateActionType.LIST_DOCUMENTS; }

        public ListDocumentsAction() { }

        public ListDocumentsAction(int collectionId, ImpunityCallback<List<BsonDocument>> onComplete = null)
        {
            CollectionId = collectionId;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = db.ListDocuments(CollectionId);
        }
    }

    public class CompoundAction : GameStateActionResultBase<List<ActionResult>>
    {
        [BsonField("as")]
        public List<GameStateActionBase> Actions;

        public override ushort GetActionType() { return (ushort)GameStateActionType.COMPOUND; }

        public CompoundAction() { }

        public CompoundAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete = null)
        {
            Actions = new List<GameStateActionBase>(actions);
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = new List<ActionResult>();

            bool error = false;
            foreach (GameStateActionBase action in Actions)
            {
                action.Run(entities, db);

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

    public class TryToLockAction : GameStateActionResultBase<bool>
    {
        [BsonField("lm")]
        public string Name;

        public override ushort GetActionType() { return (ushort)GameStateActionType.TRY_TO_LOCK; }

        public TryToLockAction() { }

        public TryToLockAction(string lockName, ImpunityCallback<bool> onComplete = null)
        {
            Name = lockName;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = entities.TryToLock(null, Name);
        }
    }

    public class UnlockAction : GameStateActionResultBase<bool>
    {
        [BsonField("lm")]
        public string Name;

        public override ushort GetActionType() { return (ushort)GameStateActionType.UNLOCK; }

        public UnlockAction() { }

        public UnlockAction(string lockName, ImpunityCallback<bool> onComplete = null)
        {
            Name = lockName;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateEntities entities, GameStateDB db)
        {
            Result = entities.Unlock(null, Name);
        }
    }

}