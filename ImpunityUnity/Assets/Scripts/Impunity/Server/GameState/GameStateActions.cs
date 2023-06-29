using System;

using UltraLiteDB;

namespace Impunity.GameState
{
    public enum GameStateActionType
    {
        SET_SUMMARY = 1,
        GET_SUMMARY = 2,
        ENSURE_FORMAT = 3,
        INSERT_DOCUMENT = 4,
        UPDATE_DOCUMENT = 5,
        UPSERT_DOCUMENT = 6,
        FIND_DOCUMENT_BY_ID = 7,
        DELETE_DOCUMENT = 8,

        COMPOUND = 100,
    }

    public static class GameActionFactory
    {
        public static Type GetActionClassType(GameStateActionType type)
        {
            switch (type)
            {
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
                case GameStateActionType.COMPOUND:
                    return typeof(CompoundAction);
            }

            throw new Exception("Unknown type id: " + type);
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

        protected override void DoAction(GameStateLogic game)
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

        protected override void DoAction(GameStateLogic game)
        {
            game.SetGameSummary(Summary);
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

        protected override void DoAction(GameStateLogic game)
        {
            Result = game.GetGameSummary();
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

        protected override void DoAction(GameStateLogic game)
        {
            game.EnsureFormat(Format);
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

        protected override void DoAction(GameStateLogic game)
        {
            Result = game.InsertDocument(CollectionId, Doc);
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

        protected override void DoAction(GameStateLogic game)
        {
            Result = game.UpdateDocument(CollectionId, Doc);
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

        protected override void DoAction(GameStateLogic game)
        {
            Result = game.UpsertDocument(CollectionId, Doc);
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

        protected override void DoAction(GameStateLogic game)
        {
            Result = game.FindDocumentById(CollectionId, Id);
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

        protected override void DoAction(GameStateLogic game)
        {
            Result = game.DeleteDocument(CollectionId, Id);
        }
    }

    public class CompoundAction : GameStateActionResultlessBase
    {
        [BsonField("as")]
        public GameStateActionBase[] Actions;

        public override ushort GetActionType() { return (ushort)GameStateActionType.COMPOUND; }

        public CompoundAction() { }

        public CompoundAction(GameStateActionBase[] actions, ImpunityCallback onComplete = null)
        {
            Actions = actions;
            OnCompleteCallback = onComplete;
        }

        protected override void DoAction(GameStateLogic game)
        {
            bool error = false;
            foreach (GameStateActionBase action in Actions)
            {
                action.Run(game);
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
    }

}