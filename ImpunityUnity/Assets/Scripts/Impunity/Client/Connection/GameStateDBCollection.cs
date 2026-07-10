
using System.Collections.Generic;

using UltraLiteDB;


namespace Impunity.Connection
{

	/// <summary>
	/// Typed, strongly-mapped view over one of the server's document database collections.
	/// <para>
	/// This is a thin convenience layer over the raw document API on <see cref="BaseGameConnection"/>
	/// (<see cref="BaseGameConnection.InsertDocument"/>, <c>FindDocumentById</c>, …): it converts
	/// <typeparamref name="DTYPE"/> instances to <see cref="BsonDocument"/>s on the way out and back to
	/// <typeparamref name="DTYPE"/> on the way in, using <see cref="Mapper"/>. The wire payload is always BSON;
	/// the mapping happens entirely client-side, so it does not have to match how the server stores the document.
	/// </para>
	/// <para>
	/// Every operation is asynchronous: the request is queued on the connection and the result is delivered to
	/// the supplied callback when <see cref="BaseGameConnection.Update"/> next processes completed actions
	/// (i.e. on the main thread). Task-based equivalents are provided by
	/// <see cref="GameStateDBCollectionAsyncExtensions"/>, and Unity coroutine variants by the <c>…Yield</c>
	/// extensions.
	/// </para>
	/// <para>
	/// Documents are keyed by their BSON <c>_id</c> field. Operations that target an existing document
	/// (update, upsert, find, delete) match on that <c>_id</c>; insert assigns one automatically when the
	/// document has none.
	/// </para>
	/// </summary>
	/// <typeparam name="DTYPE">The CLR document type stored in this collection.</typeparam>
	public class GameStateDBCollection<DTYPE>
	{
		/// <summary>
		/// The mapper used to convert between <typeparamref name="DTYPE"/> and <see cref="BsonDocument"/>.
		/// Defaults to <see cref="BsonMapper.Global"/> when none is supplied to the constructor. Note this is the
		/// client's mapper for this wrapper only; the server (de)serializes documents with its own internal mapper,
		/// so custom type registrations that affect storage must be made on both sides.
		/// </summary>
		public BsonMapper Mapper;
		BaseGameConnection Connection;
		int CollectionId;

		/// <summary>Creates a typed view over a server collection.</summary>
		/// <param name="connection">The connection whose server hosts the collection. All operations are routed through it.</param>
		/// <param name="collectionId">
		/// The collection's numeric id. Must match the <see cref="GameStateCollection.Index"/> of a collection declared
		/// in the connection's <see cref="GameStateFormat"/>. Ids below 10 are reserved for internal use and are rejected by the server.
		/// </param>
		/// <param name="mapper">Optional override for the object↔document mapper. Falls back to <see cref="BsonMapper.Global"/> when null.</param>
		public GameStateDBCollection(BaseGameConnection connection, int collectionId, BsonMapper? mapper = null)
		{
			Connection = connection;
			CollectionId = collectionId;
			Mapper = mapper ?? BsonMapper.Global;
		}

		/// <summary>Inserts a new document into the collection.</summary>
		/// <param name="doc">The document to store. If it has no <c>_id</c>, the server assigns one.</param>
		/// <param name="onComplete">
		/// Invoked on the main thread with the assigned <c>_id</c> of the new document, or an error
		/// (e.g. a duplicate <c>_id</c> surfaces as <see cref="ImpunityErrorCode.ActionBadRequest"/>).
		/// </param>
		public void InsertDocument(DTYPE doc, ImpunityCallback<BsonValue> onComplete)
		{
			Connection.InsertDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Replaces an existing document, matched by its <c>_id</c>.</summary>
		/// <param name="doc">The replacement document. Its <c>_id</c> selects the row to overwrite.</param>
		/// <param name="onComplete">Invoked on the main thread with <c>true</c> if a matching document existed and was replaced, <c>false</c> if none was found.</param>
		public void UpdateDocument(DTYPE doc, ImpunityCallback<bool> onComplete)
		{
			Connection.UpdateDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Inserts the document if no row with its <c>_id</c> exists, otherwise replaces that row.</summary>
		/// <param name="doc">The document to insert or replace.</param>
		/// <param name="onComplete">
		/// Invoked on the main thread with <c>true</c> if the document was inserted as new, or <c>false</c> if it
		/// replaced an existing one (this is UltraLiteDB's upsert convention).
		/// </param>
		public void UpsertDocument(DTYPE doc, ImpunityCallback<bool> onComplete)
		{
			Connection.UpsertDocument(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Retrieves a single document by its <c>_id</c> and maps it to <typeparamref name="DTYPE"/>.</summary>
		/// <param name="id">The <c>_id</c> of the document to fetch.</param>
		/// <param name="onComplete">Invoked on the main thread with the mapped document or null if not found, or an error.</param>
		public void FindDocumentById(BsonValue id, ImpunityCallback<DTYPE?> onComplete)
		{
			Connection.FindDocumentById(CollectionId, id, (err, bson) =>
			{
				DTYPE? doc = (bson != null) ? Mapper.ToObject<DTYPE>(bson) : default;
				onComplete(err, doc);
			});
		}

		/// <summary>Deletes a document by its <c>_id</c>.</summary>
		/// <param name="id">The <c>_id</c> of the document to delete.</param>
		/// <param name="onComplete">Invoked on the main thread with <c>true</c> if a matching document was found and deleted, <c>false</c> otherwise.</param>
		public void DeleteDocument(BsonValue id, ImpunityCallback<bool> onComplete)
		{
			Connection.DeleteDocument(CollectionId, id, onComplete);
		}

		/// <summary>Retrieves every document in the collection, each mapped to <typeparamref name="DTYPE"/>.</summary>
		/// <param name="onComplete">
		/// Invoked on the main thread with the mapped documents. The list is <c>null</c> when the underlying request
		/// yielded no list (e.g. on error); an existing-but-empty collection yields an empty list.
		/// </param>
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
