using System;
using System.Collections.Generic;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	public abstract class BaseGameConnection : IDisposable
	{
		public abstract void Connect(ImpunityCallback onComplete);

		public abstract void Update();

		public abstract void Dispose();

		public abstract void DoAction(GameStateActionBase action);


		// -------- API Calls

		public void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete)
		{
			DoAction(new SetGameSummaryAction(summary, onComplete));
		}

		public void GetGameSummary(ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new GetGameSummaryAction(onComplete));
		}

		public void EnsureFormat(GameStateFormat format, ImpunityCallback onComplete)
		{
			DoAction(new EnsureFormatAction(format, onComplete));
		}

		public void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete)
		{
			DoAction(new InsertDocumentAction(collectionId, doc, onComplete));
		}

		public void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpdateDocumentAction(collectionId, doc, onComplete));
		}

		public void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete)
		{
			DoAction(new UpsertDocumentAction(collectionId, doc, onComplete));
		}

		public void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete)
		{
			DoAction(new FindDocumentByIdAction(collectionId, id, onComplete));
		}

		public void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete)
		{
			DoAction(new DeleteDocumentAction(collectionId, id, onComplete));
		}

		public void ListDocuments(int collectionId, ImpunityCallback<List<BsonDocument>> onComplete)
		{
			DoAction(new ListDocumentsAction(collectionId, onComplete));
		}

		public void CompoundAction(IEnumerable<GameStateActionBase> actions, ImpunityCallback<List<ActionResult>> onComplete)
        {
			DoAction(new CompoundAction(actions, onComplete));
        }
	}

}