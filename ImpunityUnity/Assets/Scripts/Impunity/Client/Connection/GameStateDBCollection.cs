
using System;
using System.Collections.Generic;

using UltraLiteDB;
using Impunity.GameState;


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
	/// (update, upsert, merge, find, delete) match on that <c>_id</c>; insert assigns one automatically when the
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
		/// <para>
		/// Documents are read from a database any connected client can write, so treat them as untrusted. A
		/// member declared as a base class, interface or <c>object</c> is stored with a <c>_type</c> name, and the
		/// mapper only reads that back for types it allows: allow your own data types (<c>AllowType</c>,
		/// <c>AllowTypes("MyGame.Data.*")</c>, or <c>RegisterTypeId</c>), preferably on a dedicated mapper passed to
		/// the constructor, since <see cref="BsonMapper.Global"/> is shared with the rest of the app. Never use
		/// <c>AllowAllTypes</c> or <c>AllowTypes("*")</c> here: a document could then create any type in the process.
		/// </para>
		/// </summary>
		public BsonMapper Mapper;
		BaseGameConnection Connection;

		/// <summary>The server collection's numeric id, for building raw document actions against it.</summary>
		public int CollectionId { get; }

		/// <summary>Creates a typed view over a server collection.</summary>
		/// <param name="connection">The connection whose server hosts the collection. All operations are routed through it.</param>
		/// <param name="collectionId">
		/// The collection's numeric id. Must match the <see cref="GameStateCollection.Index"/> of a collection declared
		/// in the connection's <see cref="GameStateFormat"/>. Ids below 10 are reserved for internal use and are rejected by the server.
		/// </param>
		/// <param name="mapper">Optional override for the object↔document mapper. Falls back to <see cref="BsonMapper.Global"/> when null. See <see cref="Mapper"/> for which types it must allow.</param>
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

		/// <summary>Merges the top-level fields of <paramref name="patch"/> into the existing document with the same
		/// <c>_id</c>, leaving its other fields intact, then removes the fields named in <paramref name="unsetKeys"/>.
		/// Nothing is inserted if the document doesn't exist.</summary>
		/// <remarks>A patch is partial, so it is a raw <see cref="BsonDocument"/> rather than a
		/// <typeparamref name="DTYPE"/>. Build its values with <see cref="Mapper"/> (<c>Mapper.Serialize(value)</c>) so
		/// they are stored the way <see cref="FindDocumentById"/> expects to read them back. Merges to different fields
		/// of one document commute, which makes a field-per-key document safe to write from conditional actions; see
		/// the "Ordering against later writes" note in docs/guides/DistributedEntities.md.</remarks>
		/// <param name="patch">The fields to write. Must include the target <c>_id</c>.</param>
		/// <param name="onComplete">Invoked on the main thread with <c>true</c> if the document existed and was merged, <c>false</c> if it was not found.</param>
		/// <param name="unsetKeys">Top-level fields to remove, or null for none. Must not include <c>_id</c> or a field
		/// <paramref name="patch"/> also sets.</param>
		/// <exception cref="ArgumentException"><paramref name="patch"/> has no <c>_id</c>.</exception>
		public void MergeIntoDocument(BsonDocument patch, ImpunityCallback<bool> onComplete, IEnumerable<string>? unsetKeys = null)
		{
			CheckPatch(patch);
			Connection.MergeIntoDocument(CollectionId, patch, onComplete, unsetKeys);
		}

		/// <summary>Merges <paramref name="patch"/> into the existing document with the same <c>_id</c> as
		/// <see cref="MergeIntoDocument"/> does, or inserts it as a new document if there is none.</summary>
		/// <param name="patch">The fields to write, or the whole new document. Must include the target <c>_id</c>.</param>
		/// <param name="onComplete">Invoked on the main thread with <c>true</c> if a new document was inserted, or
		/// <c>false</c> if an existing one was merged into.</param>
		/// <param name="unsetKeys">Top-level fields to remove from an existing document, or null for none.</param>
		/// <exception cref="ArgumentException"><paramref name="patch"/> has no <c>_id</c>.</exception>
		public void MergeInsertDocument(BsonDocument patch, ImpunityCallback<bool> onComplete, IEnumerable<string>? unsetKeys = null)
		{
			CheckPatch(patch);
			Connection.MergeInsertDocument(CollectionId, patch, onComplete, unsetKeys);
		}

		// A patch without an _id is always a bug, so it is refused before anything is sent (and, from a builder, before
		// an action exists to resolve). The server checks the rest.
		private static void CheckPatch(BsonDocument patch)
		{
			if (patch == null)
			{
				throw new ArgumentNullException(nameof(patch));
			}
			if (!patch.TryGetValue("_id", out BsonValue id) || id == null || id.IsNull)
			{
				throw new ArgumentException("A merge patch must include the target document's _id", nameof(patch));
			}
		}

		/// <summary>Retrieves a single document by its <c>_id</c> and maps it to <typeparamref name="DTYPE"/>.</summary>
		/// <param name="id">The <c>_id</c> of the document to fetch.</param>
		/// <param name="onComplete">
		/// Invoked on the main thread with the mapped document or null if not found, or an error. A document that
		/// can't be mapped to <typeparamref name="DTYPE"/> is reported as <see cref="ImpunityErrorCode.ClientMappingError"/>.
		/// </param>
		public void FindDocumentById(BsonValue id, ImpunityCallback<DTYPE?> onComplete)
		{
			Connection.FindDocumentById(CollectionId, id, (err, bson) =>
			{
				DTYPE? doc = default;
				if (bson != null)
				{
					ImpunityErrorResponse? mapError = TryMap(bson, out DTYPE mapped);
					if (mapError != null)
					{
						onComplete(mapError, default);
						return;
					}
					doc = mapped;
				}
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
		/// yielded no list (e.g. on error); an existing-but-empty collection yields an empty list. If any document
		/// can't be mapped to <typeparamref name="DTYPE"/>, the whole call fails with
		/// <see cref="ImpunityErrorCode.ClientMappingError"/> naming that document's <c>_id</c>.
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
						ImpunityErrorResponse? mapError = TryMap(bson, out DTYPE mapped);
						if (mapError != null)
						{
							onComplete(mapError, null);
							return;
						}
						doclist.Add(mapped);
					}
				}

				onComplete(err, doclist);
			});
		}

		// Maps a document read from the server, returning an error instead of throwing. The collection is writable by
		// every client, so a document may fail to map (a disallowed _type, a mistyped member). Thrown from the
		// callback, that would only be logged by Update(), and the caller's onComplete would never run.
		private ImpunityErrorResponse? TryMap(BsonDocument bson, out DTYPE doc)
		{
			try
			{
				doc = Mapper.ToObject<DTYPE>(bson);
				return null;
			}
			catch (Exception e)
			{
				doc = default!;
				return new ImpunityErrorResponse(ImpunityErrorCode.ClientMappingError,
					"Couldn't map document " + bson["_id"] + " to " + typeof(DTYPE).Name + ": " + e.Message);
			}
		}

		// ----- Action builders
		//
		// These build the same request as the methods above but return it instead of sending it, for use as the
		// conditional action of an entity create or delete (BaseGameConnection.CreateObject's onCreatedAction,
		// DeleteEntity's onDeletedAction, entity.Delete's onDeletedAction), or as a step of a CompoundDatabaseAction.
		// The callback fires with the action's own result once the server has run it — or, used as a conditional, with
		// ImpunityErrorCode.ActionConditionNotMet if the entity operation did not succeed and it was never run.

		/// <summary>Builds, without sending, an insert of <paramref name="doc"/>. See <see cref="InsertDocument"/>.</summary>
		/// <param name="doc">The document to store. If it has no <c>_id</c>, the server assigns one.</param>
		/// <param name="onComplete">Receives the assigned <c>_id</c>, or an error. May be null for fire-and-forget.</param>
		public InsertDocumentAction MakeInsertAction(DTYPE doc, ImpunityCallback<BsonValue>? onComplete = null)
		{
			return new InsertDocumentAction(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Builds, without sending, a replace of the document matching <paramref name="doc"/>'s <c>_id</c>. See <see cref="UpdateDocument"/>.</summary>
		/// <param name="doc">The replacement document.</param>
		/// <param name="onComplete">Receives <c>true</c> if a matching document was replaced, <c>false</c> if none existed. May be null.</param>
		public UpdateDocumentAction MakeUpdateAction(DTYPE doc, ImpunityCallback<bool>? onComplete = null)
		{
			return new UpdateDocumentAction(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Builds, without sending, an insert-or-replace of <paramref name="doc"/>. See <see cref="UpsertDocument"/>.</summary>
		/// <param name="doc">The document to insert or replace.</param>
		/// <param name="onComplete">Receives <c>true</c> if inserted as new, <c>false</c> if it replaced an existing one. May be null.</param>
		public UpsertDocumentAction MakeUpsertAction(DTYPE doc, ImpunityCallback<bool>? onComplete = null)
		{
			return new UpsertDocumentAction(CollectionId, Mapper.ToDocument(doc), onComplete);
		}

		/// <summary>Builds, without sending, a merge of <paramref name="patch"/> into an existing document. See <see cref="MergeIntoDocument"/>.</summary>
		/// <param name="patch">The fields to write. Must include the target <c>_id</c>.</param>
		/// <param name="onComplete">Receives <c>true</c> if the document existed and was merged, <c>false</c> if not. May be null.</param>
		/// <param name="unsetKeys">Top-level fields to remove, or null for none.</param>
		/// <exception cref="ArgumentException"><paramref name="patch"/> has no <c>_id</c>.</exception>
		public MergeIntoDocumentAction MakeMergeIntoAction(BsonDocument patch, ImpunityCallback<bool>? onComplete = null, IEnumerable<string>? unsetKeys = null)
		{
			CheckPatch(patch);
			return new MergeIntoDocumentAction(CollectionId, patch, onComplete, unsetKeys);
		}

		/// <summary>Builds, without sending, a merge-or-insert of <paramref name="patch"/>. See <see cref="MergeInsertDocument"/>.</summary>
		/// <param name="patch">The fields to write, or the whole new document. Must include the target <c>_id</c>.</param>
		/// <param name="onComplete">Receives <c>true</c> if inserted as new, <c>false</c> if merged into an existing one. May be null.</param>
		/// <param name="unsetKeys">Top-level fields to remove from an existing document, or null for none.</param>
		/// <exception cref="ArgumentException"><paramref name="patch"/> has no <c>_id</c>.</exception>
		public MergeInsertDocumentAction MakeMergeInsertAction(BsonDocument patch, ImpunityCallback<bool>? onComplete = null, IEnumerable<string>? unsetKeys = null)
		{
			CheckPatch(patch);
			return new MergeInsertDocumentAction(CollectionId, patch, onComplete, unsetKeys);
		}

		/// <summary>Builds, without sending, a delete of the document with the given <c>_id</c>. See <see cref="DeleteDocument"/>.</summary>
		/// <param name="id">The <c>_id</c> of the document to delete.</param>
		/// <param name="onComplete">Receives <c>true</c> if a matching document was deleted, <c>false</c> if none existed. May be null.</param>
		public DeleteDocumentAction MakeDeleteAction(BsonValue id, ImpunityCallback<bool>? onComplete = null)
		{
			return new DeleteDocumentAction(CollectionId, id, onComplete);
		}
	}

}
