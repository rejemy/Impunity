
using System.Collections.Generic;

using UltraLiteDB;


namespace Impunity.Connection
{

	public class GameStateDBCollection<DTYPE>
    {
		public BsonMapper Mapper;
        BaseGameConnection Connection;
        int CollectionId;

        public GameStateDBCollection(BaseGameConnection connection, int collectionId, BsonMapper mapper = null)
        {
            Connection = connection;
            CollectionId = collectionId;
			Mapper = mapper;
			if (Mapper == null)
            {
				Mapper = new BsonMapper();
            }

		}

		public void InsertDocument(DTYPE doc, ImpunityCallback<BsonValue> onComplete)
		{
			Connection.InsertDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		public void UpdateDocument(DTYPE doc, ImpunityCallback<bool> onComplete)
		{
			Connection.UpdateDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		public void UpsertDocument(DTYPE doc, ImpunityCallback<bool> onComplete)
		{
			Connection.UpsertDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		public void FindDocumentById(BsonValue id, ImpunityCallback<DTYPE> onComplete)
		{
			Connection.FindDocumentById(CollectionId, id, (err, bson) =>
			{
				DTYPE doc = Mapper.ToObject<DTYPE>(bson);
				onComplete(err, doc);
			});
		}

		public void DeleteDocument(BsonValue id, ImpunityCallback<bool> onComplete)
		{
			Connection.DeleteDocument(CollectionId, id, onComplete);
		}

		public void ListDocuments(ImpunityCallback<List<DTYPE>> onComplete)
		{
			Connection.ListDocuments(CollectionId, (err, bsonlist)=>
			{
				List<DTYPE> doclist = null;
				if (bsonlist != null)
				{
					doclist = new List<DTYPE>(bsonlist.Count);
					foreach (BsonDocument bson in bsonlist)
					{
						doclist.Add(Mapper.ToObject<DTYPE>(bson));
					}
				}

				onComplete(err, doclist);
			});
		}
	}

}