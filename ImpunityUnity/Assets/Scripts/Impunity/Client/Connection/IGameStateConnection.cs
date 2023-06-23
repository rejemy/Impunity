using System;

using UltraLiteDB;


namespace Impunity.Connection
{

	public interface IGameStateConnection : IDisposable
	{
		void Connect(ImpunityCallback onComplete);

		void Update();

		void SetGameSummary(BsonDocument summary, ImpunityCallback onComplete);
		void GetSummary(ImpunityCallback<BsonDocument> onComplete);

		void EnsureFormat(GameStateFormat format, ImpunityCallback onComplete);

		void InsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<BsonValue> onComplete);
		void UpdateDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete);
		void UpsertDocument(int collectionId, BsonDocument doc, ImpunityCallback<bool> onComplete);
		void FindDocumentById(int collectionId, BsonValue id, ImpunityCallback<BsonDocument> onComplete);
		void DeleteDocument(int collectionId, BsonValue id, ImpunityCallback<bool> onComplete);
	}

}