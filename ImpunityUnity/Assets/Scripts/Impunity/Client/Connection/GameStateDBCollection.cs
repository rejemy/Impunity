
using System.Collections.Generic;

using UltraLiteDB;


namespace Impunity.Connection
{

	/// <summary>
	/// Typed wrapper around the server's key-value database collections. Automatically maps between
	/// <typeparamref name="DTYPE"/> objects and BSON documents using the configured <see cref="BsonMapper"/>.
	/// </summary>
	/// <typeparam name="DTYPE">The document type to store and retrieve.</typeparam>
	public class GameStateDBCollection<DTYPE>
	{
		/// <summary>The BSON mapper used for object-to-document conversion.</summary>
		public BsonMapper Mapper;
		BaseGameConnection Connection;
		int CollectionId;

		/// <summary>Creates a typed collection wrapper for the given collection ID on the specified connection.</summary>
		public GameStateDBCollection(BaseGameConnection connection, int collectionId, BsonMapper? mapper = null)
		{
			Connection = connection;
			CollectionId = collectionId;
			Mapper = mapper ?? BsonMapper.Global;
		}

		/// <summary>Inserts a new document. Returns the assigned ID via callback.</summary>
		public void InsertDocument(DTYPE doc, ImpunityCallback<BsonValue> onComplete)
		{
			Connection.InsertDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Replaces an existing document (matched by ID).</summary>
		public void UpdateDocument(DTYPE doc, ImpunityCallback<bool> onComplete)
		{
			Connection.UpdateDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Inserts or replaces a document (matched by ID).</summary>
		public void UpsertDocument(DTYPE doc, ImpunityCallback<bool> onComplete)
		{
			Connection.UpsertDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Retrieves a document by ID, deserializing it to <typeparamref name="DTYPE"/>.</summary>
		public void FindDocumentById(BsonValue id, ImpunityCallback<DTYPE> onComplete)
		{
			Connection.FindDocumentById(CollectionId, id, (err, bson) =>
			{
				DTYPE doc = Mapper.ToObject<DTYPE>(bson);
				onComplete(err, doc);
			});
		}

		/// <summary>Deletes a document by ID.</summary>
		public void DeleteDocument(BsonValue id, ImpunityCallback<bool> onComplete)
		{
			Connection.DeleteDocument(CollectionId, id, onComplete);
		}

		/// <summary>Lists all documents in this collection, deserialized to <typeparamref name="DTYPE"/>.</summary>
		public void ListDocuments(ImpunityCallback<List<DTYPE>?> onComplete)
		{
			Connection.ListDocuments(CollectionId, (err, bsonlist) =>
			{
				List<DTYPE>? doclist = null;
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
