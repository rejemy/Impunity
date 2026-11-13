using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UltraLiteDB;
using Impunity.GameState;


namespace Impunity.Connection
{

	/// <summary>Marks a class as a distributed entity type with a unique integer ID. Optionally specifies a factory method and persistence key.</summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class DistributedEntity : Attribute
	{
		internal int EntityId;
		/// <summary>Optional name of a public static parameterless method on the annotated type that returns an
		/// <see cref="IDistributedEntity"/>. When set, the manager calls it to construct instances instead of
		/// <c>Activator.CreateInstance</c> — useful for types without a public default constructor or that need
		/// custom setup. Null to use the default constructor.</summary>
		public string? FactoryMethod { get; set; }
		/// <summary>Optional database key under which entities of this type are persisted. When non-null the type is
		/// persisted (and reloaded across server restarts); the value must be non-empty and not start with '_'.
		/// A type must set this for any of its fields to be persistable. The key is stored with every persisted
		/// entity as its durable type identity (numeric entity ids are wire-only), so it must be unique across
		/// entity types and must never change once data exists.</summary>
		public string? PersistAs { get; set; }

		/// <summary>Marks the annotated class as a distributed entity type.</summary>
		/// <param name="entityId">Unique positive integer id for this entity type, sent on the wire in place of the
		/// type name. Must be unique across all distributed entity types registered with a manager.</param>
		public DistributedEntity(int entityId)
		{
			EntityId = entityId;
		}

		private static readonly ConcurrentDictionary<Type, int> EntityTypeIdCache = new ConcurrentDictionary<Type, int>();

		/// <summary>Returns the entity type id declared by the <see cref="DistributedEntity"/> attribute on
		/// <paramref name="type"/>, or 0 if it has none. Cached per type; used by the entity base constructors
		/// so a freshly-constructed instance knows its type id without going through the manager.</summary>
		internal static int GetEntityTypeId(Type type)
		{
			return EntityTypeIdCache.GetOrAdd(type, static t =>
				((DistributedEntity?)GetCustomAttribute(t, typeof(DistributedEntity)))?.EntityId ?? 0);
		}
	}

	/// <summary>Stores a distributed field's value in the database under the given key.
	/// <para>Every field whose type implements <see cref="IDistributedField"/> is replicated automatically — that is
	/// what those types are for — so there is no attribute to opt a field <i>into</i> replication. This attribute adds
	/// the one thing the type cannot express: that the value should also be <b>persisted</b>, and under what key.</para>
	/// <para>The declaring entity type must itself be persisted (have its own <c>PersistAs</c>), the field must not be
	/// temporal, and the key must be non-empty and not start with '_'. A subclass that is not itself persisted may
	/// inherit persisted fields from a persisted base type; on that subclass the inherited fields are
	/// replicated-only.</para>
	/// <para>Unlike a wire id, this key is the field's <b>durable</b> identity and must never change once data
	/// exists — see the guide's discussion of wire ids vs. durable keys.</para></summary>
	[AttributeUsage(AttributeTargets.Field)]
	public class PersistAs : Attribute
	{
		/// <summary>The database key this field's value is stored under.</summary>
		public string Key { get; private set; }

		/// <summary>Persists the annotated distributed field under <paramref name="key"/>.</summary>
		/// <param name="key">Durable storage key. Must be non-empty, must not start with '_', and must never change
		/// once saved data exists.</param>
		public PersistAs(string key)
		{
			Key = key;
		}
	}

	/// <summary>Assigns each distributed field its wire id.
	/// <para>A field is distributed if — and only if — its type implements <see cref="IDistributedField"/>. There is
	/// no opt-in attribute: those types exist to be replicated, so declaring one is the declaration of intent.</para>
	/// <para>Ids are assigned per <b>concrete runtime type</b> over that type's entire inheritance chain: base classes
	/// first, then by ordinal field name within each class, numbered from 1. Because each registered entity type gets
	/// its own property table on the server, sibling subclasses never have to agree on numbering — an inherited field
	/// may hold a different id in each subclass — which is what removes the old "unique across a class and all its
	/// parents" burden.</para>
	/// <para>Consequences worth knowing: adding a field to a subclass never renumbers its base's fields; reordering or
	/// moving declarations is free; <b>renaming</b> a field renumbers and so is a schema change (the format checksum
	/// changes — bump the version). Numeric ids are wire-only and never stored, so no renumbering ever touches saved
	/// data.</para>
	/// <para>The ordering is imposed rather than taken from reflection: <c>Type.GetFields</c> does not guarantee an
	/// order, and the flattened call also omits a base type's private fields, so the chain is walked one level at a
	/// time with <see cref="BindingFlags.DeclaredOnly"/> and sorted.</para></summary>
	public static class DistributedFieldIds
	{
		/// <summary>Highest assignable field id. Each field maps to one bit of the 64-bit dirty mask
		/// (<c>1UL &lt;&lt; (id - 1)</c>), so a concrete type may have at most 63 distributed fields in total.</summary>
		public const int MaxFieldId = 63;

		private static readonly ConcurrentDictionary<Type, Dictionary<string, byte>> Cache
			= new ConcurrentDictionary<Type, Dictionary<string, byte>>();

		/// <summary>Returns this type's distributed field ids, keyed by CLR field name. Built once per type and
		/// cached; safe to call from an entity constructor, so offline instances that are never registered still get
		/// correct ids.</summary>
		public static Dictionary<string, byte> ForType(Type entityType)
		{
			return Cache.GetOrAdd(entityType, BuildIds);
		}

		/// <summary>Returns the id assigned to a single field, or 0 if the type has no distributed field by that
		/// name.</summary>
		public static byte GetFieldId(Type entityType, string fieldName)
		{
			return ForType(entityType).TryGetValue(fieldName, out byte id) ? id : (byte)0;
		}

		private static Dictionary<string, byte> BuildIds(Type entityType)
		{
			// Base first, so adding a field to a subclass never renumbers the fields it inherited.
			List<Type> chain = new List<Type>();
			for (Type? level = entityType; level != null && level != typeof(object); level = level.BaseType)
			{
				chain.Add(level);
			}
			chain.Reverse();

			Dictionary<string, byte> ids = new Dictionary<string, byte>();
			int next = 1;

			foreach (Type level in chain)
			{
				// DeclaredOnly one level at a time: a flattened GetFields omits a base type's private fields, and its
				// ordering is not contractual, so gather per level and sort by name for a stable assignment.
				List<FieldInfo> declared = new List<FieldInfo>();
				foreach (FieldInfo field in level.GetFields(BindingFlags.Instance | BindingFlags.Public
															| BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (typeof(IDistributedField).IsAssignableFrom(field.FieldType))
					{
						declared.Add(field);
					}
				}

				declared.Sort((f1, f2) => string.CompareOrdinal(f1.Name, f2.Name));

				foreach (FieldInfo field in declared)
				{
					if (ids.ContainsKey(field.Name))
					{
						throw new Exception("Distributed entity " + entityType.Name + " declares a distributed field '"
							+ field.Name + "' that shadows an inherited one; distributed field names must be unique "
							+ "across the whole inheritance chain");
					}

					if (next > MaxFieldId)
					{
						throw new Exception("Distributed entity " + entityType.Name + " has more than " + MaxFieldId
							+ " distributed fields across its inheritance chain; each field needs one bit of the "
							+ "64-bit dirty mask");
					}

					ids[field.Name] = (byte)next++;
				}
			}

			return ids;
		}
	}


	/// <summary>Client-side interface for a distributed entity. Provides identity, dirty tracking, lock/event/delete operations, and lifecycle callbacks.</summary>
	public interface IDistributedEntity
	{
		/// <summary>The server-assigned unique id for this entity instance, or 0 before it has been registered
		/// (e.g. a freshly constructed or editor-built instance). Set by the manager when the entity is created
		/// locally or received from the server.</summary>
		uint DistributedEntityId { get; set; }
		/// <summary>The distributed type id declared by this class's <see cref="DistributedEntity"/> attribute.
		/// Populated at construction from the attribute, so even offline instances know their type id.</summary>
		int DistributedEntityType { get; set; }
		/// <summary>When true, this client owns the entity's state: the server accepts this client's updates without
		/// echoing them back and locks the entity to this client on creation. Mutually exclusive with
		/// <see cref="IsPersisted"/>. Set before creating the entity to request client authority.</summary>
		bool IsClientAuthoritative { get; set; }
		/// <summary>When true, this entity is automatically deleted when this client disconnects. Complimentes
		/// IsClientAuthoritative for anything that should not stick around after a client leaves. Mutually exclusive with
		/// <see cref="IsPersisted"/>. Set before creating the entity to mark the entity as scoped to the client.</summary>
		bool DeleteOnDisconnect { get; set; }
		/// <summary>Whether this entity is stored in the server's database and reloaded across server restarts. Set
		/// before creating the entity to request persistence — the entity type, and for objects the containing channel,
		/// must also be persisted; restored from server instance flags for entities received from the server. Mutually
		/// exclusive with <see cref="IsClientAuthoritative"/>.</summary>
		bool IsPersisted { get; set; }
		/// <summary>Whether the entity currently holds a server-side exclusive lock (held by any client). Kept in sync
		/// with the server: set true just before <see cref="OnLocked"/> and false just before <see cref="OnUnlocked"/>,
		/// and seeded from the server's lock state when the entity is first received. While an entity is locked by
		/// another client the server rejects this client's property updates and delete requests.</summary>
		bool IsLocked { get; set; }

		/// <summary>The <see cref="ClientEntityManager"/> that owns this entity. Assigned when the entity is registered;
		/// entity operations (events, delete, lock/unlock) are routed through its connection.</summary>
		ClientEntityManager Manager { get; set; }

		/// <summary>Bitmask of distributed fields with changes not yet sent to the server. Each field's bit is
		/// <c>(1UL &lt;&lt; (fieldId - 1))</c>. Set via <see cref="SetDirty"/> when a field is mutated and cleared by the
		/// manager after the changes are serialized.</summary>
		ulong DirtyBits { get; }
		/// <summary>True if any pending dirty field requires guaranteed (reliable) delivery; when set, the next update
		/// for this entity is sent reliably rather than as a best-effort datagram. Cleared together with
		/// <see cref="DirtyBits"/>.</summary>
		bool DirtyGuaranteed { get; }

		/// <summary>Per-entity outgoing sequence counter, incremented each time this entity sends an update.</summary>
		uint SendSeq { get; set; }
		/// <summary>Per-field last-received sequence numbers, indexed by field ID. Used to detect and discard stale out-of-order updates.</summary>
		uint[]? FieldRecvSeq { get; set; }

		/// <summary>Marks one or more distributed fields as changed so their new values are sent on the next update
		/// flush. Normally called by the generated field setters rather than directly: ORs <paramref name="fieldBitmask"/>
		/// into <see cref="DirtyBits"/>, ORs <paramref name="guaranteed"/> into <see cref="DirtyGuaranteed"/>, and
		/// registers the entity with its manager for the next flush.</summary>
		/// <param name="fieldBitmask">Bit(s) of the changed field(s); each field's bit is <c>(1UL &lt;&lt; (fieldId - 1))</c>.</param>
		/// <param name="guaranteed">True to require reliable delivery of this update; ORed with any existing guarantee for the pending batch.</param>
		void SetDirty(ulong fieldBitmask, bool guaranteed);
		/// <summary>Clears all pending dirty bits and the guaranteed flag. Called by the manager after the entity's
		/// changes have been serialized; not normally called by application code.</summary>
		void ClearDirty();
		/// <summary>Initializes this entity's distributed field instances. The base implementation does nothing; the
		/// source generator overrides it to construct each field and wire it to this entity. Invoked from the base
		/// constructor.</summary>
		void InitializeDistributedFields();

		// Distributed actions client->server
		/// <summary>Fires a one-shot event on this entity. The server relays it to every client subscribed to the
		/// entity's channel, including this one. Events carry no entity state and are not persisted.</summary>
		/// <param name="eventType">Application-defined event type id.</param>
		/// <param name="eventData">Arbitrary BSON payload delivered with the event.</param>
		/// <param name="onComplete">Invoked once the server has accepted the request, or with a non-null error if it failed. May be null.</param>
		void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete);
		/// <summary>Fires a one-shot event on this entity, choosing whether this connection receives it back. The server
		/// relays it to every other client subscribed to the entity's channel, and to this one too unless
		/// <paramref name="echo"/> is <see cref="EntityEventEcho.ExcludeSender"/>. The event is stamped with this client's
		/// estimate of the server time (<see cref="EntityEvent.SentServerMs"/>). Events carry no entity state and are not
		/// persisted.</summary>
		/// <param name="eventType">Application-defined event type id.</param>
		/// <param name="eventData">Arbitrary BSON payload delivered with the event.</param>
		/// <param name="echo">Whether the sending connection receives its own event.</param>
		/// <param name="onComplete">Invoked once the server has accepted the request, or with a non-null error if it failed. May be null.</param>
		void TriggerEvent(int eventType, BsonValue eventData, EntityEventEcho echo, ImpunityCallback? onComplete);
		/// <summary>Immediately flushes this entity's pending dirty fields to the server as a guarded (optimistic-concurrency)
		/// update, instead of waiting for the per-frame dirty sweep. The server applies and relays the update only if this
		/// client has seen the latest change to every field being written; otherwise the whole update is rejected (nothing
		/// applied) and <paramref name="onComplete"/> receives <see cref="GuardedResult.Stale"/> — use this to resolve
		/// races (e.g. two players picking the same berry: both write, only the winner gets
		/// <see cref="GuardedResult.Applied"/>). On both outcomes the winning field values are already applied locally by
		/// the time the callback runs, so it is safe to read the entity's current values in the callback.</summary>
		/// <remarks>
		/// <para>This is the low-level primitive. Prefer <see cref="Transact"/> (with <c>maxAttempts: 1</c> when losing
		/// means "don't"): it waits for this connection's own writes to the entity to be confirmed first. A bare
		/// <c>UpdateIfUnchanged</c> does not, so a second one to the same field sent before the first one's callback is
		/// stale against yourself.</para>
		/// <para>Locks: holding this entity's lock bypasses the staleness check. If another connection holds it, the
		/// update fails (<see cref="GuardedResult.Failed"/>, <see cref="ImpunityErrorCode.ActionBlockedByLock"/>); an
		/// explicit lock always wins over an optimistic write and there is nothing to retry until it is released.</para>
		/// </remarks>
		/// <param name="onComplete">Receives <see cref="GuardedResult.Applied"/>, <see cref="GuardedResult.Stale"/>
		/// (both with a null error), or <see cref="GuardedResult.Failed"/> with the error (locked by another connection,
		/// entity gone, connection broken). Delivered on a later <c>Update()</c>, never reentrantly. May be null.</param>
		/// <param name="onUpdatedAction">Optional database action to ride this update (shorthand for
		/// <see cref="AddConditionalAction"/> followed by the flush). It runs only if the update is accepted; on rejection it
		/// is skipped, so a retry must pass it again.</param>
		void UpdateIfUnchanged(ImpunityCallback<GuardedResult>? onComplete, GameStateActionBase? onUpdatedAction = null);
		/// <summary>
		/// Attaches a database action that rides this entity's next update to the server, in the same message, and runs
		/// only if the server applies that update — e.g. applying a potion's effect to the player entity and removing the
		/// potion from the inventory, or putting an item in a chest and removing it from the inventory.
		/// <para>
		/// "Next update" is whichever send happens first: the per-frame sweep in <c>Update()</c>, an
		/// <see cref="UpdateIfUnchanged"/>, or the flush before an <see cref="Unlock"/>. Field changes made before that send
		/// travel with the action. If no field is dirty (say the <c>Set</c> was a no-op) an empty update is sent just to
		/// carry it. An update carrying an action is always sent reliably.
		/// </para>
		/// <para>
		/// The action's own callback fires after the update is processed: with its own result, or with
		/// <see cref="ImpunityErrorCode.ActionConditionNotMet"/> if it was not run — the update was rejected (stale
		/// guarded update, entity locked by another client, entity gone) or the entity was removed before it was sent.
		/// Several actions attached before one send travel together and each still gets its own callback. Not a
		/// transaction: a failed action does not undo the update. See docs/guides/DistributedEntities.md, "Conditional actions".
		/// </para>
		/// </summary>
		/// <param name="action">A document action (insert, update, upsert, merge, delete, find, list) or a
		/// <see cref="CompoundDatabaseAction"/>. Anything else throws <see cref="System.ArgumentException"/>.</param>
		void AddConditionalAction(GameStateActionBase action);
		/// <summary>
		/// Adds a note to this entity's next update: why it changed (who hit it, from which side, with what). The note is
		/// relayed with that update to every subscriber, this client included on the echo, and is never stored; a later
		/// subscriber sees the state, not the note. Receivers read the update's notes as <see cref="CurrentUpdateNotes"/>
		/// from field <c>OnChanged</c> handlers while the update is applied, and get <see cref="OnUpdateNote"/> once per
		/// note afterwards.
		/// </summary>
		/// <remarks>
		/// <para>"Next update" is whichever send happens first, as for <see cref="AddConditionalAction"/>: the per-frame
		/// sweep, an <see cref="UpdateIfUnchanged"/>, a transaction's update, or the flush before an <see cref="Unlock"/>.
		/// Field changes made before that send travel with it; with none, an empty update is sent just to carry the note.
		/// An update carrying a note is always sent reliably.</para>
		/// <para>Notes accumulate, like conditional actions: every note added before the send travels with it and is
		/// delivered in the order added, so two systems noting the same entity in one frame never overwrite each other.
		/// A note belongs to the update, not to a field: every field changed by that update sees all of its notes, so
		/// make a note say what it is about (a <c>"kind"</c> key, say) and have handlers pick out their own.</para>
		/// <para>A rejected or dropped update (stale guarded update, locked by another client) takes its notes with it.
		/// On a client-authoritative entity the writer gets no echo, so it never sees its own notes: present the change
		/// locally, as for any client-authoritative <c>Set</c>.</para>
		/// <para>Does nothing on an entity that is not registered with a connection.</para>
		/// </remarks>
		/// <param name="note">The note. Must not be null or BSON null.</param>
		/// <exception cref="System.ArgumentNullException"><paramref name="note"/> is null or BSON null.</exception>
		void AddUpdateNote(BsonValue note);
		/// <summary>The notes on the update being applied to this entity right now, in the order the writer added them;
		/// empty otherwise. Non-empty only while a noted update is applied (its fields' <c>OnChanged</c> handlers) and
		/// during <see cref="OnUpdateNote"/>. Always empty for local <c>Set</c>s, including on a client-authoritative
		/// entity.</summary>
		IReadOnlyList<UpdateNote> CurrentUpdateNotes { get; }
		/// <summary>Requests that the server delete this entity. On success the entity is removed and all subscribers
		/// (including this client) receive <see cref="OnDeleted"/> followed by <see cref="OnUndistributed"/>. Deleting a
		/// channel also removes all of its objects.</summary>
		/// <param name="deleteData">Optional BSON payload relayed to subscribers via <see cref="OnDeleted"/>.</param>
		/// <param name="onComplete">Receives <c>true</c> if the entity was deleted, or <c>false</c> if the request was
		/// rejected because the entity is locked by another client; the error argument is non-null on failure. May be null.</param>
		/// <param name="onDeletedAction">Optional database action sent in the same message and run by the server only if
		/// this request deleted the entity — e.g. adding a picked-up item to the player's inventory. Its own callback
		/// fires after <paramref name="onComplete"/>. See <see cref="BaseGameConnection.DeleteEntity"/>.</param>
		void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete, GameStateActionBase? onDeletedAction = null);
		/// <summary>Deletes this entity only if the fields the delete depends on are unchanged since this client last
		/// received them: the optimistic, guarded form of <see cref="Delete"/>, as <see cref="UpdateIfUnchanged"/> is of an
		/// update. Kill a creature only if its health is still what you saw; take a stack only if its count hasn't moved.
		/// </summary>
		/// <remarks>
		/// <para>Name the guarded fields with their <c>Guard</c> properties, combined with <c>|</c> (<c>Health.Guard | Owner.Guard</c>).
		/// Guard what the decision read, not every field: a wandering creature's position changes constantly, and guarding
		/// it would make the delete fail for no reason. An empty guard throws; use <see cref="Delete"/> for an unguarded
		/// delete.</para>
		/// <para>If a guarded field changed first, nothing happens: <paramref name="onComplete"/> gets
		/// <see cref="GuardedResult.Stale"/> and <paramref name="onDeletedAction"/> is skipped
		/// (<see cref="ImpunityErrorCode.ActionConditionNotMet"/>), exactly as for any delete that didn't happen. When it
		/// fires, the winner's values are already applied locally.</para>
		/// <para>Locks: a lock holder bypasses the guard. If another connection holds the lock the delete fails
		/// (<see cref="GuardedResult.Failed"/>, <see cref="ImpunityErrorCode.ActionBlockedByLock"/>), as a guarded
		/// update does; an explicit lock always wins over an optimistic write.</para>
		/// <para>The guard is this client's last-received view, so this connection's own writes still awaiting their
		/// echo count as changes it hasn't seen: guard a field you just wrote and the delete is stale against yourself.
		/// <c>Transact</c> queues behind your own writes; a bare <c>DeleteIfUnchanged</c> does not, so prefer
		/// <c>tx.Delete</c> inside a <see cref="Transact"/> unless you know nothing of yours is in flight.</para>
		/// </remarks>
		/// <param name="guard">The fields the delete depends on: their <c>Guard</c>s combined with <c>|</c>. Must be non-empty and
		/// name only this entity's distributed fields.</param>
		/// <param name="deleteData">Optional BSON payload relayed to subscribers via <see cref="OnDeleted"/>.</param>
		/// <param name="onComplete">Receives <see cref="GuardedResult.Applied"/> if the entity was deleted,
		/// <see cref="GuardedResult.Stale"/> if a guarded field changed first (both with a null error), or
		/// <see cref="GuardedResult.Failed"/> with the error (<see cref="ImpunityErrorCode.ActionBlockedByLock"/>,
		/// <see cref="ImpunityErrorCode.ActionNotFound"/>, connection broken). May be null.</param>
		/// <param name="onDeletedAction">Optional database action run by the server only if this request deleted the
		/// entity; its callback fires after <paramref name="onComplete"/>. See <see cref="BaseGameConnection.DeleteEntity"/>.
		/// If the entity has no connection, both callbacks fire immediately, in that order, with
		/// <see cref="ImpunityErrorCode.ActionBadRequest"/>.</param>
		/// <exception cref="System.ArgumentException">The guard is empty or names fields of a different entity.
		/// Nothing was sent, and <paramref name="onDeletedAction"/> is still the caller's to resolve.</exception>
		void DeleteIfUnchanged(FieldGuard guard, BsonValue? deleteData, ImpunityCallback<GuardedResult>? onComplete, GameStateActionBase? onDeletedAction = null);
		/// <summary>Replaces this object with <paramref name="newObject"/> only if the fields the decision depends on are
		/// unchanged since this client last received them: the guarded replace, as <see cref="DeleteIfUnchanged"/> is the
		/// guarded delete. A hole becomes the crop planted in it, only if it is still an empty hole.</summary>
		/// <remarks>
		/// <para>The replacement is created in this object's channel under its unique name (given to
		/// <paramref name="newObject"/> if it has none), and replaces exactly this entity or nothing. Subscribers see this
		/// object's <see cref="OnDeleted"/>, carrying <paramref name="replaceData"/>, then the new object.</para>
		/// <para>This is the low-level primitive, with the same caveat as <see cref="DeleteIfUnchanged"/>: this
		/// connection's own writes still awaiting their echo count as changes it hasn't seen. Prefer
		/// <c>tx.ReplaceWith</c> inside a <see cref="Transact"/>, which queues behind them and reruns on staleness.</para>
		/// <para>Locks: a lock holder bypasses the guard; another connection's lock fails the replace
		/// (<see cref="GuardedResult.Failed"/>, <see cref="ImpunityErrorCode.ActionBlockedByLock"/>).</para>
		/// </remarks>
		/// <param name="guard">The fields the replace depends on: their <c>Guard</c>s combined with <c>|</c>. Must be
		/// non-empty and name only this entity's fields.</param>
		/// <param name="newObject">The replacement. A unique name different from this object's is an error.</param>
		/// <param name="replaceData">Optional BSON payload saying why, relayed to subscribers in this object's
		/// <see cref="OnDeleted"/> (<see cref="EntityDeletion.Data"/>).</param>
		/// <param name="onComplete">Receives <see cref="GuardedResult.Applied"/> once <paramref name="newObject"/> is
		/// registered, <see cref="GuardedResult.Stale"/> if a guarded field changed first (both with a null error), or
		/// <see cref="GuardedResult.Failed"/> with the error (<see cref="ImpunityErrorCode.ActionBlockedByLock"/>;
		/// <see cref="ImpunityErrorCode.ActionNotFound"/> or <see cref="ImpunityErrorCode.ActionUniqueNameExists"/> when
		/// the entity was already deleted or replaced; connection broken). May be null.</param>
		/// <param name="onCreatedAction">Optional database action run by the server only if the replace goes through; its
		/// callback fires after <paramref name="onComplete"/>. If the entity has no connection, both callbacks fire
		/// immediately, in that order, with <see cref="ImpunityErrorCode.ActionBadRequest"/>.</param>
		/// <exception cref="System.ArgumentException">The guard is empty or names fields of a different entity, or
		/// <paramref name="newObject"/> has a different unique name. Nothing was sent, and
		/// <paramref name="onCreatedAction"/> is still the caller's to resolve.</exception>
		/// <exception cref="System.InvalidOperationException">This entity is not an object with a unique name in a
		/// channel.</exception>
		void ReplaceIfUnchanged<T>(FieldGuard guard, T newObject, BsonValue? replaceData, ImpunityCallback<GuardedResult>? onComplete, GameStateActionBase? onCreatedAction = null) where T : class, IDistributedObject;
		/// <summary>Attempts to acquire the server-side exclusive lock on this entity. While locked, other clients'
		/// updates and deletes are rejected by the server. Release with <see cref="Unlock"/>.</summary>
		/// <param name="onComplete">Receives <c>true</c> if the lock was acquired (or this client already holds it),
		/// <c>false</c> if another client holds it; the error argument is non-null on failure. May be null.</param>
		void TryLock(ImpunityCallback<bool> onComplete);
		/// <summary>Acquires the exclusive lock, queueing for it on the server when another client holds it. The
		/// callback fires with <see cref="LockWaitResult.Locked"/> once the lock is held — immediately if it was free,
		/// or later when the server hands it over — or <see cref="LockWaitResult.Error"/> on failure. The queue is
		/// first-come-first-served, so there is no race to win once queued, and no timeout is applied: use
		/// <see cref="RunExclusive"/> if you need one.</summary>
		/// <param name="onComplete">Receives the <see cref="LockWaitResult"/> and any error. May be null.</param>
		void WaitForLock(ImpunityCallback<LockWaitResult> onComplete);
		/// <summary>Releases the exclusive lock this client holds on the entity. On success all subscribers receive
		/// <see cref="OnUnlocked"/>, unless the lock was handed straight to a queued waiter.</summary>
		/// <param name="onComplete">Receives <c>true</c> if the lock was released, <c>false</c> if this client did not
		/// hold the lock; the error argument is non-null on failure. May be null.</param>
		void Unlock(ImpunityCallback<bool> onComplete);
		/// <summary>
		/// Acquires this entity's exclusive lock, runs <paramref name="body"/> while holding it, then flushes the
		/// edits it made and releases the lock — the safe way to do a read-modify-write that must not interleave with
		/// other clients.
		/// </summary>
		/// <remarks>
		/// <para>Waiting is first-come-first-served: if another client holds the lock, this call queues on the server
		/// and is handed the lock in turn, with no race against other waiters.</para>
		/// <para>Handoff guarantee: the body is guaranteed to observe every guaranteed-field edit made by the previous
		/// holder's scope. Fields written with <c>SetUnguaranteed</c> are <em>not</em> covered — they are sent
		/// unreliably and may arrive after this body has run, or not at all.</para>
		/// <para>The lock is released even if <paramref name="body"/> throws; the exception is reported as
		/// <see cref="RunExclusiveResult.Failed"/> and any edits made before it threw are still flushed.</para>
		/// <para>Scopes from this client on the same entity run one at a time, in call order.</para>
		/// </remarks>
		/// <param name="body">The logic to run under the lock, on the main thread.</param>
		/// <param name="onComplete">Receives <see cref="RunExclusiveResult.Ran"/> if the body ran,
		/// <see cref="RunExclusiveResult.TimedOut"/> if the lock never became available, or
		/// <see cref="RunExclusiveResult.Failed"/> with an error. May be null.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock — the body's own running time is never counted.
		/// Negative (the default) uses <see cref="ClientEntityManager.DefaultExclusiveTimeoutSeconds"/>; zero fails
		/// immediately when the lock is held by someone else.</param>
		void RunExclusive(Action body, ImpunityCallback<RunExclusiveResult>? onComplete, float timeoutSeconds = -1f);
		/// <summary>
		/// Runs a read-modify-write on this entity and commits it as one guarded write, running the body again if
		/// another client changed the fields it depends on first. This is the default way to make a contended change:
		/// no lock, so nobody waits, and this connection's own earlier writes are always confirmed before the body runs.
		/// </summary>
		/// <remarks>
		/// <para><b>Rules for the body.</b> It may run more than once, so it must have no side effects outside this
		/// entity: no writes to other entities, no database calls, no sounds or UI. Read with <c>Get()</c>, write with
		/// <c>Set</c>, return true to commit or false to send nothing. Database work that must happen with the commit
		/// is attached with <c>tx.AddAction</c> (or passed to <c>tx.Delete</c> / <c>tx.ReplaceWith</c>),
		/// created fresh on every run; everything else belongs in <paramref name="onComplete"/>. A transaction covers
		/// exactly one entity. Calling <see cref="Delete"/>, <see cref="DeleteIfUnchanged"/> or
		/// <see cref="UpdateIfUnchanged"/> on the entity from inside its own body throws (the transaction then reports
		/// <see cref="TransactResult.Failed"/>): use the scope.</para>
		/// <para><b>What gets guarded.</b> Every field the body set, plus any it names with <c>tx.DependsOn(fields)</c>
		/// (fields it read but did not write). If any of them changed since this client last received it, the server
		/// rejects the write, the winner's values are applied locally, and the body runs again against them, up to
		/// <paramref name="maxAttempts"/> runs in total. Pass <c>maxAttempts: 1</c> for a one-shot claim where losing
		/// means "don't" (the result is then <see cref="TransactResult.Conflict"/>).</para>
		/// <para><b>Locks.</b> An explicit lock always wins. If another connection holds this entity's lock
		/// (<see cref="TryLock"/>, <see cref="RunExclusive"/>), the transaction is not retried and does not wait: it
		/// reports <see cref="TransactResult.Failed"/> with <see cref="ImpunityErrorCode.ActionBlockedByLock"/>. A lock
		/// is held on purpose and possibly for a long time (a player rearranging an open chest), so treat this as
		/// "busy", the same as any other write refused by the lock. If this connection holds the lock, the staleness
		/// check is bypassed and the transaction commits on its first run.</para>
		/// <para><b>Decline or throw.</b> Nothing is sent. The fields the body set are discarded and its replicated
		/// actions are resolved with <see cref="ImpunityErrorCode.ActionConditionNotMet"/>. Exception: on a
		/// client-authoritative entity <c>Set</c> has already applied locally and the server must hear about it, so the
		/// fields are still sent by the next flush. On such entities decide before you set.</para>
		/// <para><b>Delete or replace.</b> <c>tx.Delete(guard, ...)</c> and <c>tx.ReplaceWith(guard, newObject, ...)</c>
		/// make the write a guarded delete or replace of this entity instead of an update: the guard is required,
		/// rejection reruns the body the same way, and transactions queued behind a successful one fail because the
		/// entity is gone. Such a body must not also set fields, attach conditional actions or set a note (the
		/// transaction fails).</para>
		/// <para><b>Ordering.</b> Transactions on the same entity from this connection run one at a time, in call order.
		/// A body runs only when none of this connection's writes to the entity are awaiting a reply (changes already
		/// pending are flushed first, as an ordinary update, and are never discarded by a decline), so <c>Get()</c>
		/// returns confirmed values, this connection's own included. A rerun also waits until the rejected attempt's
		/// conditional actions have reported, so state the application keeps for them (a reserved inventory slot) is
		/// already released. If nothing is in flight, the first run happens inside this call. A body that sets nothing
		/// and attaches nothing commits without sending. See docs/guides/DistributedEntities.md, "Transactions", for
		/// the full mechanism.</para>
		/// <para><b>No connection.</b> <c>Set</c> applies locally, so the body runs at once and its return value is the
		/// outcome; <paramref name="onComplete"/> then fires immediately. A delete or replace fails there.</para>
		/// </remarks>
		/// <param name="body">Reads and sets this entity's fields, or deletes or replaces it through the scope; returns
		/// true to commit, false to send nothing.</param>
		/// <param name="onComplete">Invoked exactly once with <see cref="TransactResult.Committed"/>,
		/// <see cref="TransactResult.Declined"/>, <see cref="TransactResult.Conflict"/> (out of attempts), or
		/// <see cref="TransactResult.Failed"/> with an error (the body threw; the entity was deleted or unsubscribed;
		/// locked by another connection; connection broken). Delivered on a later <c>Update()</c>, never reentrantly.
		/// May be null.</param>
		/// <param name="maxAttempts">Runs of the body before giving up with <see cref="TransactResult.Conflict"/>.
		/// Negative (the default) uses <see cref="ClientEntityManager.DefaultTransactMaxAttempts"/>; the minimum is 1.</param>
		void Transact(Func<TransactScope, bool> body, ImpunityCallback<TransactResult>? onComplete, int maxAttempts = -1);

		// Distributed events server->client
		/// <summary>Called once the entity has been created locally and its initial property values from the server
		/// have been applied — i.e. the entity is ready to use. For a channel received with existing members, this fires
		/// on the channel before its child objects are created.</summary>
		void OnFullyInitialized();
		/// <summary>Raised by <see cref="OnFullyInitialized"/> when the entity is fully initialized.</summary>
		event Action? OnFullyInitializedEvent;

		/// <summary>Called when a one-shot event fired on this entity (via <c>TriggerEvent</c> on any client, including
		/// this one unless it excluded itself) is received.</summary>
		/// <param name="e">The event: its type id, payload, and when it was sent.</param>
		void OnEventTriggered(EntityEvent e);
		/// <summary>Raised by <see cref="OnEventTriggered"/> when an event is received for this entity.</summary>
		event Action<EntityEvent>? OnEventTriggeredEvent;

		/// <summary>Called once per note (see <see cref="AddUpdateNote"/>), in the order added, after the update carrying
		/// it has been applied to this entity, its fields' <c>OnChanged</c> handlers included. Also called for a note on
		/// an update that changed no field. <see cref="CurrentUpdateNotes"/> still holds all of the update's notes.</summary>
		/// <param name="note">The note, its send time, and whether this connection sent it.</param>
		void OnUpdateNote(UpdateNote note);
		/// <summary>Raised by <see cref="OnUpdateNote"/>.</summary>
		event Action<UpdateNote>? OnUpdateNoteEvent;

		// When an object is specifically deleted
		/// <summary>Called when this entity has been deleted on the server. Always followed by
		/// <see cref="OnUndistributed"/>. (Objects of a deleted channel instead receive only <see cref="OnUndistributed"/>.)</summary>
		/// <param name="deletion">The deleter's cause payload (<c>deleteData</c> / <c>replaceData</c>), when it was sent, and whether this
		/// connection did it.</param>
		void OnDeleted(EntityDeletion deletion);
		/// <summary>Raised by <see cref="OnDeleted"/> when the entity is deleted.</summary>
		event Action<EntityDeletion>? OnDeletedEvent;

		/// <summary>Called when the entity becomes locked on the server, by any client. <see cref="IsLocked"/> is set
		/// true before this fires.</summary>
		void OnLocked();
		/// <summary>Raised by <see cref="OnLocked"/> when the entity becomes locked.</summary>
		event Action? OnLockedEvent;

		/// <summary>Called when the entity's lock is released on the server and nobody was queued for it.
		/// <see cref="IsLocked"/> is set false before this fires. When the lock is instead handed to a queued waiter,
		/// listeners see <see cref="OnLocked"/> for the new holder rather than this.</summary>
		void OnUnlocked();
		/// <summary>Raised by <see cref="OnUnlocked"/> when the entity's lock is released.</summary>
		event Action? OnUnlockedEvent;

		// When an object is no longer distributed due to the client unsubscribing from a channel,
		// or the channel being deleted, also called after OnDeleted
		/// <summary>Called when the entity stops being replicated to this client and its local reference is released by
		/// the manager. Fires when the client unsubscribes from the channel, when the channel is deleted, or after the
		/// entity itself is deleted (always after <see cref="OnDeleted"/>). Use it to release any local resources that
		/// hold the entity.</summary>
		void OnUndistributed();
		/// <summary>Raised by <see cref="OnUndistributed"/> when the entity stops being replicated to this client.</summary>
		event Action? OnUndistributedEvent;
	}

	/// <summary>Client-side interface for a distributed object entity.</summary>
	public interface IDistributedObject : IDistributedEntity
	{
		/// <summary>Optional name that is unique within the object's channel. May not contain '/'. For persisted objects
		/// it is used as the database key (the server generates a GUID if none is set). Leave null for an anonymous object.</summary>
		string? UniqueName { get; set; }

		/// <summary>The channel this object belongs to. Assigned by the <see cref="ClientEntityManager"/> when the object
		/// is created or received, and read on deletion to notify the channel via <see cref="IDistributedChannel.OnObjectRemoved"/>.</summary>
		IDistributedChannel? Channel { get; set; }
	}

	/// <summary>Client-side interface for a distributed channel entity. Channels contain child objects and support subscription.</summary>
	public interface IDistributedChannel : IDistributedEntity
	{
		/// <summary>The channel's name, unique within the server and used for subscription and lookup. May not contain
		/// '/'. Set when the channel is created or subscribed to.</summary>
		string Name { get; set; }
		/// <summary>The objects currently in this channel that are replicated to this client, keyed by entity id.
		/// Maintained by <see cref="OnObjectAdded"/> / <see cref="OnObjectRemoved"/> as objects are created and removed.</summary>
		Dictionary<uint, IDistributedObject> DistributedObjects { get; }

		/// <summary>Unsubscribes this client from the channel and removes it (and its objects) from the manager.
		/// When <paramref name="immediate"/> is false (default), the channel and its objects stay live and keep
		/// receiving updates until the server acknowledges the unsubscribe, at which point <see cref="IDistributedEntity.OnUndistributed"/>
		/// fires on each and all references are released. When <paramref name="immediate"/> is true, the channel and its
		/// objects are unregistered synchronously and all further incoming updates for them are suppressed (no lifecycle
		/// callbacks) — the caller is responsible for cleaning them up.</summary>
		/// <param name="onComplete">Invoked when the server acknowledges the unsubscribe; the error argument is non-null on failure. May be null.</param>
		/// <param name="immediate">If true, tear down synchronously and suppress further updates instead of waiting for the server ack.</param>
		void Unsubscribe(ImpunityCallback onComplete, bool immediate = false);

		/// <summary>Called when an object is added to this channel and replicated to this client. The base
		/// implementation adds it to <see cref="DistributedObjects"/>. Fires both for the channel's pre-existing members
		/// at subscribe time and for objects created later.</summary>
		/// <param name="entity">The object added to the channel.</param>
		/// <param name="newlyCreated">True if the object was just created while this client was subscribed; false if it
		/// already existed and is part of the initial snapshot delivered on subscribe.</param>
		void OnObjectAdded(IDistributedObject entity, bool newlyCreated);
		/// <summary>Raised by <see cref="OnObjectAdded"/> when an object is added to this channel.</summary>
		event Action<IDistributedObject, bool>? OnObjectAddedEvent;

		/// <summary>Called when an object is removed from this channel. The base implementation removes it from
		/// <see cref="DistributedObjects"/>.</summary>
		/// <param name="entity">The object removed from the channel.</param>
		void OnObjectRemoved(IDistributedObject entity);
		/// <summary>Raised by <see cref="OnObjectRemoved"/> when an object is removed from this channel.</summary>
		event Action<IDistributedObject>? OnObjectRemovedEvent;
	}

	/// <summary>Base implementation of <see cref="IDistributedEntity"/> with dirty-bit tracking, lock waiting, and lifecycle callback stubs.</summary>
	public abstract class DistributedEntityBase : IDistributedEntity
	{
		/// <summary>The server-assigned unique id for this entity instance, or 0 before it has been registered
		/// (e.g. a freshly constructed or editor-built instance). Set by the manager when the entity is created
		/// locally or received from the server.</summary>
		public uint DistributedEntityId { get; set; }
		/// <summary>The distributed type id declared by this class's <see cref="DistributedEntity"/> attribute.
		/// Populated at construction from the attribute, so even offline instances know their type id.</summary>
		public int DistributedEntityType { get; set; }
		/// <summary>When true, this client owns the entity's state: the server accepts this client's updates without
		/// echoing them back and locks the entity to this client on creation. Mutually exclusive with
		/// <see cref="IsPersisted"/>. Set before creating the entity to request client authority.</summary>
		public bool IsClientAuthoritative { get; set; }
		/// <summary>When true, this entity is automatically deleted when this client disconnects. Complimentes
		/// IsClientAuthoritative for anything that should not stick around after a client leaves. Mutually exclusive with
		/// <see cref="IsPersisted"/>. Set before creating the entity to mark the entity as scoped to the client.</summary>
		public bool DeleteOnDisconnect { get; set; }
		/// <summary>Whether this entity is stored in the server's database and reloaded across server restarts. Set
		/// before creating the entity to request persistence — the entity type, and for objects the containing channel,
		/// must also be persisted; restored from server instance flags for entities received from the server. Mutually
		/// exclusive with <see cref="IsClientAuthoritative"/>.</summary>
		public bool IsPersisted { get; set; }
		/// <summary>Whether the entity currently holds a server-side exclusive lock (held by any client). Kept in sync
		/// with the server: set true just before <see cref="OnLocked"/> and false just before <see cref="OnUnlocked"/>,
		/// and seeded from the server's lock state when the entity is first received. While an entity is locked by
		/// another client the server rejects this client's property updates and delete requests.</summary>
		public bool IsLocked { get; set; }
		/// <summary>The <see cref="ClientEntityManager"/> that owns this entity. Assigned when the entity is registered;
		/// entity operations (events, delete, lock/unlock) are routed through its connection.</summary>
		public ClientEntityManager Manager { get; set; } = default!;

		/// <summary>Bitmask of distributed fields with changes not yet sent to the server. Each field's bit is
		/// <c>(1UL &lt;&lt; (fieldId - 1))</c>. Set via <see cref="SetDirty"/> when a field is mutated and cleared by the
		/// manager after the changes are serialized.</summary>
		public ulong DirtyBits { get; private set; }
		/// <summary>True if any pending dirty field requires guaranteed (reliable) delivery; when set, the next update
		/// for this entity is sent reliably rather than as a best-effort datagram. Cleared together with
		/// <see cref="DirtyBits"/>.</summary>
		public bool DirtyGuaranteed { get; private set; }

		/// <summary>Per-entity outgoing sequence counter, incremented each time this entity sends an update.</summary>
		public uint SendSeq { get; set; }
		/// <summary>Per-field last-received sequence numbers, indexed by field ID. Used to detect and discard stale out-of-order updates.</summary>
		public uint[]? FieldRecvSeq { get; set; }

		/// <summary>Constructs the entity, resolving its <see cref="DistributedEntityType"/> from the
		/// <see cref="DistributedEntity"/> attribute and calling <see cref="InitializeDistributedFields"/>, so the
		/// instance is usable (including with the manager's persisted-field BSON helpers) even before it is registered.</summary>
		protected DistributedEntityBase()
		{
			// The type id is intrinsic to the [DistributedEntity] attribute, so populate it at
			// construction. This makes offline/editor-built instances (which never go through
			// CreateObject/HandleCreate*) usable with the manager's persisted-field BSON methods.
			DistributedEntityType = DistributedEntity.GetEntityTypeId(GetType());
			InitializeDistributedFields();
		}

		/// <summary>Marks one or more distributed fields as changed so their new values are sent on the next update
		/// flush. Normally called by the generated field setters rather than directly: ORs <paramref name="fieldBitmask"/>
		/// into <see cref="DirtyBits"/>, ORs <paramref name="guaranteed"/> into <see cref="DirtyGuaranteed"/>, and
		/// registers the entity with its manager for the next flush.</summary>
		/// <param name="fieldBitmask">Bit(s) of the changed field(s); each field's bit is <c>(1UL &lt;&lt; (fieldId - 1))</c>.</param>
		/// <param name="guaranteed">True to require reliable delivery of this update; ORed with any existing guarantee for the pending batch.</param>
		public void SetDirty(ulong fieldBitmask, bool guaranteed)
		{
			DirtyBits |= fieldBitmask;
			DirtyGuaranteed |= guaranteed;
			Manager?.SetDirty(this);
		}

		/// <summary>Clears all pending dirty bits and the guaranteed flag. Called by the manager after the entity's
		/// changes have been serialized; not normally called by application code.</summary>
		public void ClearDirty()
		{
			DirtyBits = 0ul;
			DirtyGuaranteed = false;
		}

		/// <summary>Initializes this entity's distributed field instances. The base implementation does nothing; the
		/// source generator overrides it to construct each field and wire it to this entity. Invoked from the base
		/// constructor.</summary>
		public virtual void InitializeDistributedFields() { }

		/// <summary>Fires a one-shot event on this entity. The server relays it to every client subscribed to the
		/// entity's channel, including this one. Events carry no entity state and are not persisted.</summary>
		/// <param name="eventType">Application-defined event type id.</param>
		/// <param name="eventData">Arbitrary BSON payload delivered with the event.</param>
		/// <param name="onComplete">Invoked once the server has accepted the request, or with a non-null error if it failed. May be null.</param>
		public void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			TriggerEvent(eventType, eventData, EntityEventEcho.IncludeSender, onComplete);
		}

		/// <summary>Fires a one-shot event on this entity, choosing whether this connection receives it back. The server
		/// relays it to every other client subscribed to the entity's channel, and to this one too unless
		/// <paramref name="echo"/> is <see cref="EntityEventEcho.ExcludeSender"/>. The event is stamped with this client's
		/// estimate of the server time (<see cref="EntityEvent.SentServerMs"/>). Events carry no entity state and are not
		/// persisted.</summary>
		/// <param name="eventType">Application-defined event type id.</param>
		/// <param name="eventData">Arbitrary BSON payload delivered with the event.</param>
		/// <param name="echo">Whether the sending connection receives its own event.</param>
		/// <param name="onComplete">Invoked once the server has accepted the request, or with a non-null error if it failed. May be null.</param>
		public void TriggerEvent(int eventType, BsonValue eventData, EntityEventEcho echo, ImpunityCallback? onComplete)
		{
			BaseGameConnection? connection = Manager?.Connection;
			if (connection == null)
			{
				onComplete?.Invoke(NotRegisteredError());
				return;
			}

			connection.TriggerEntityEvent(DistributedEntityId, eventType, eventData, echo, onComplete);
		}

		// The error every request fails with when this entity has no manager or its manager has no connection. With no
		// connection there is no queue to defer through, so such refusals invoke their callbacks immediately.
		private protected static ImpunityErrorResponse NotRegisteredError()
		{
			return new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with a connection");
		}

		/// <inheritdoc/>
		public void UpdateIfUnchanged(ImpunityCallback<GuardedResult>? onComplete, GameStateActionBase? onUpdatedAction = null)
		{
			if (Manager == null)
			{
				// No manager means the entity was never registered with a connection; there is no queue to defer through.
				onComplete?.Invoke(NotRegisteredError(), GuardedResult.Failed);
				onUpdatedAction?.FailLocally(NotRegisteredError());
				return;
			}

			Manager.SendEntityUpdatesIfUnchanged(this, onComplete, onUpdatedAction);
		}

		/// <inheritdoc/>
		public void AddConditionalAction(GameStateActionBase action)
		{
			if (Manager == null)
			{
				throw new InvalidOperationException("Entity is not registered with a connection");
			}

			Manager.AddConditionalAction(this, action);
		}

		/// <inheritdoc/>
		public void AddUpdateNote(BsonValue note)
		{
			if (note == null || note.IsNull)
			{
				throw new ArgumentNullException(nameof(note), "An update note needs a payload");
			}

			Manager?.AddUpdateNote(this, note);
		}

		/// <inheritdoc/>
		public IReadOnlyList<UpdateNote> CurrentUpdateNotes => Manager?.GetCurrentUpdateNotes(this) ?? Array.Empty<UpdateNote>();

		/// <summary>Requests that the server delete this entity. On success the entity is removed and all subscribers
		/// (including this client) receive <see cref="OnDeleted"/> followed by <see cref="OnUndistributed"/>. Deleting a
		/// channel also removes all of its objects.</summary>
		/// <param name="deleteData">Optional BSON payload relayed to subscribers via <see cref="OnDeleted"/>.</param>
		/// <param name="onComplete">Receives <c>true</c> if the entity was deleted, or <c>false</c> if the request was
		/// rejected because the entity is locked by another client; the error argument is non-null on failure. May be null.</param>
		/// <param name="onDeletedAction">Optional database action sent in the same message and run by the server only if
		/// this request deleted the entity — e.g. adding a picked-up item to the player's inventory. Its own callback
		/// fires after <paramref name="onComplete"/>. See <see cref="BaseGameConnection.DeleteEntity"/>. If the entity has
		/// no connection, both callbacks fire immediately, in that order, with
		/// <see cref="ImpunityErrorCode.ActionBadRequest"/>.</param>
		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete, GameStateActionBase? onDeletedAction = null)
		{
			BaseGameConnection? connection = Manager?.Connection;
			if (connection == null)
			{
				onComplete?.Invoke(NotRegisteredError(), false);
				onDeletedAction?.FailLocally(NotRegisteredError());
				return;
			}

			Manager!.ThrowIfInTransactBody(this, nameof(Delete));
			connection.DeleteEntity(DistributedEntityId, deleteData, onComplete, onDeletedAction);
		}

		/// <summary>Deletes this entity only if the fields in <paramref name="guard"/> are unchanged since this client
		/// last received them. See <see cref="IDistributedEntity.DeleteIfUnchanged"/>.</summary>
		public void DeleteIfUnchanged(FieldGuard guard, BsonValue? deleteData, ImpunityCallback<GuardedResult>? onComplete, GameStateActionBase? onDeletedAction = null)
		{
			ClientEntityManager? manager = Manager;
			if (manager == null || manager.Connection == null)
			{
				onComplete?.Invoke(NotRegisteredError(), GuardedResult.Failed);
				onDeletedAction?.FailLocally(NotRegisteredError());
				return;
			}

			manager.DeleteEntityIfUnchanged(this, guard, deleteData, onComplete, onDeletedAction);
		}

		/// <inheritdoc/>
		public void ReplaceIfUnchanged<T>(FieldGuard guard, T newObject, BsonValue? replaceData, ImpunityCallback<GuardedResult>? onComplete, GameStateActionBase? onCreatedAction = null) where T : class, IDistributedObject
		{
			ClientEntityManager? manager = Manager;
			if (manager == null || manager.Connection == null)
			{
				onComplete?.Invoke(NotRegisteredError(), GuardedResult.Failed);
				onCreatedAction?.FailLocally(NotRegisteredError());
				return;
			}

			manager.ReplaceEntityIfUnchanged(this, guard, newObject, replaceData, onComplete, onCreatedAction);
		}

		/// <summary>Attempts to acquire the server-side exclusive lock on this entity. While locked, other clients'
		/// updates and deletes are rejected by the server. Release with <see cref="Unlock"/>.</summary>
		/// <param name="onComplete">Receives <c>true</c> if the lock was acquired (or this client already holds it),
		/// <c>false</c> if another client holds it; the error argument is non-null on failure. May be null.</param>
		public void TryLock(ImpunityCallback<bool> onComplete)
		{
			BaseGameConnection? connection = Manager?.Connection;
			if (connection == null)
			{
				onComplete?.Invoke(NotRegisteredError(), false);
				return;
			}

			connection.TryToLockEntity(DistributedEntityId, onComplete);
		}

		/// <summary>Acquires the exclusive lock, queueing for it on the server when another client holds it. The
		/// callback fires with <see cref="LockWaitResult.Locked"/> once the lock is held — immediately if it was free,
		/// or later when the server hands it over — or <see cref="LockWaitResult.Error"/> on failure. The queue is
		/// first-come-first-served, so there is no race to win once queued, and no timeout is applied: use
		/// <see cref="RunExclusive"/> if you need one.</summary>
		/// <param name="onComplete">Receives the <see cref="LockWaitResult"/> and any error. May be null.</param>
		public void WaitForLock(ImpunityCallback<LockWaitResult> onComplete)
		{
			if (Manager == null)
			{
				onComplete?.Invoke(NotRegisteredError(), LockWaitResult.Error);
				return;
			}

			Manager.WaitForEntityLock(this, onComplete);
		}

		/// <summary>
		/// Acquires this entity's exclusive lock, runs <paramref name="body"/> while holding it, then flushes the
		/// edits it made and releases the lock — the safe way to do a read-modify-write that must not interleave with
		/// other clients.
		/// </summary>
		/// <remarks>
		/// <para>Waiting is first-come-first-served: if another client holds the lock, this call queues on the server
		/// and is handed the lock in turn, with no race against other waiters.</para>
		/// <para>Handoff guarantee: the body is guaranteed to observe every guaranteed-field edit made by the previous
		/// holder's scope. Fields written with <c>SetUnguaranteed</c> are <em>not</em> covered — they are sent
		/// unreliably and may arrive after this body has run, or not at all.</para>
		/// <para>The lock is released even if <paramref name="body"/> throws; the exception is reported as
		/// <see cref="RunExclusiveResult.Failed"/> and any edits made before it threw are still flushed.</para>
		/// <para>Scopes from this client on the same entity run one at a time, in call order.</para>
		/// </remarks>
		/// <param name="body">The logic to run under the lock, on the main thread.</param>
		/// <param name="onComplete">Receives <see cref="RunExclusiveResult.Ran"/> if the body ran,
		/// <see cref="RunExclusiveResult.TimedOut"/> if the lock never became available, or
		/// <see cref="RunExclusiveResult.Failed"/> with an error. May be null.</param>
		/// <param name="timeoutSeconds">How long to wait for the lock — the body's own running time is never counted.
		/// Negative (the default) uses <see cref="ClientEntityManager.DefaultExclusiveTimeoutSeconds"/>; zero fails
		/// immediately when the lock is held by someone else.</param>
		public void RunExclusive(Action body, ImpunityCallback<RunExclusiveResult>? onComplete, float timeoutSeconds = -1f)
		{
			if (Manager == null)
			{
				onComplete?.Invoke(NotRegisteredError(), RunExclusiveResult.Failed);
				return;
			}

			Manager.RunExclusive(this, body, onComplete, timeoutSeconds);
		}

		/// <inheritdoc/>
		public void Transact(Func<TransactScope, bool> body, ImpunityCallback<TransactResult>? onComplete, int maxAttempts = -1)
		{
			if (Manager == null)
			{
				// Never registered: Set applies locally, so the body's own answer is the outcome.
				ClientEntityManager.TransactOffline(this, body, onComplete);
				return;
			}

			Manager.Transact(this, body, onComplete, maxAttempts);
		}


		/// <summary>Releases the exclusive lock this client holds on the entity. On success all subscribers receive
		/// <see cref="OnUnlocked"/>.</summary>
		/// <param name="onComplete">Receives <c>true</c> if the lock was released, <c>false</c> if this client did not
		/// hold the lock; the error argument is non-null on failure. May be null.</param>
		public void Unlock(ImpunityCallback<bool> onComplete)
		{
			BaseGameConnection? connection = Manager?.Connection;
			if (connection == null)
			{
				onComplete?.Invoke(NotRegisteredError(), false);
				return;
			}

			connection.UnlockEntity(DistributedEntityId, onComplete);
		}

		/// <summary>Called when the entity becomes locked on the server, by any client. <see cref="IsLocked"/> is set
		/// true before this fires.</summary>
		public virtual void OnLocked()
		{
			OnLockedEvent?.Invoke();
		}
		/// <summary>Raised by <see cref="OnLocked"/> when the entity becomes locked.</summary>
		public event Action? OnLockedEvent;

		/// <summary>Called when the entity's lock is released on the server and nobody was queued for it.
		/// <see cref="IsLocked"/> is set false before this fires. When the lock is instead handed to a queued waiter,
		/// listeners see <see cref="OnLocked"/> for the new holder rather than this.</summary>
		public virtual void OnUnlocked()
		{
			try
			{
				OnUnlockedEvent?.Invoke();
			}
			catch (Exception ex)
			{
				ImpunityLogger.LogError("Exception in OnUnlockedEvent:", ex);
			}

		}
		/// <summary>Raised by <see cref="OnUnlocked"/> when the entity's lock is released.</summary>
		public event Action? OnUnlockedEvent;

		/// <summary>Called once the entity has been created locally and its initial property values from the server
		/// have been applied — i.e. the entity is ready to use. For a channel received with existing members, this fires
		/// on the channel before its child objects are created.</summary>
		public virtual void OnFullyInitialized()
		{
			OnFullyInitializedEvent?.Invoke();
		}
		/// <summary>Raised by <see cref="OnFullyInitialized"/> when the entity is fully initialized.</summary>
		public event Action? OnFullyInitializedEvent;

		/// <summary>Called when a one-shot event fired on this entity (via <c>TriggerEvent</c> on any client, including
		/// this one unless it excluded itself) is received.</summary>
		/// <param name="e">The event: its type id, payload, and when it was sent.</param>
		public virtual void OnEventTriggered(EntityEvent e)
		{
			OnEventTriggeredEvent?.Invoke(e);
		}
		/// <summary>Raised by <see cref="OnEventTriggered"/> when an event is received for this entity.</summary>
		public event Action<EntityEvent>? OnEventTriggeredEvent;

		/// <inheritdoc/>
		public virtual void OnUpdateNote(UpdateNote note)
		{
			OnUpdateNoteEvent?.Invoke(note);
		}
		/// <inheritdoc/>
		public event Action<UpdateNote>? OnUpdateNoteEvent;

		/// <summary>Called when this entity has been deleted on the server. Always followed by
		/// <see cref="OnUndistributed"/>. (Objects of a deleted channel instead receive only <see cref="OnUndistributed"/>.)</summary>
		/// <param name="deletion">The deleter's cause payload (<c>deleteData</c> / <c>replaceData</c>), when it was sent, and whether this
		/// connection did it.</param>
		public virtual void OnDeleted(EntityDeletion deletion)
		{
			OnDeletedEvent?.Invoke(deletion);
		}
		/// <summary>Raised by <see cref="OnDeleted"/> when the entity is deleted.</summary>
		public event Action<EntityDeletion>? OnDeletedEvent;

		/// <summary>Called when the entity stops being replicated to this client and its local reference is released by
		/// the manager. Fires when the client unsubscribes from the channel, when the channel is deleted, or after the
		/// entity itself is deleted (always after <see cref="OnDeleted"/>). Use it to release any local resources that
		/// hold the entity.</summary>
		public virtual void OnUndistributed()
		{
			OnUndistributedEvent?.Invoke();
		}
		/// <summary>Raised by <see cref="OnUndistributed"/> when the entity stops being replicated to this client.</summary>
		public event Action? OnUndistributedEvent;
	}

	/// <summary>Base implementation of <see cref="IDistributedObject"/>.</summary>
	public abstract class DistributedObjectBase : DistributedEntityBase, IDistributedObject
	{
		/// <summary>Optional name that is unique within the object's channel. May not contain '/'. For persisted objects
		/// it is used as the database key (the server generates a GUID if none is set). Leave null for an anonymous object.</summary>
		public string? UniqueName { get; set; }

		/// <summary>The channel this object belongs to. Assigned by the <see cref="ClientEntityManager"/> when the object
		/// is created or received, and read on deletion to notify the channel via <see cref="IDistributedChannel.OnObjectRemoved"/>.</summary>
		public IDistributedChannel? Channel { get; set; } = null!;
	}

	/// <summary>Base implementation of <see cref="IDistributedChannel"/>. Maintains a dictionary of child objects and supports unsubscription.</summary>
	public abstract class DistributedChannelBase : DistributedEntityBase, IDistributedChannel
	{
		/// <summary>The channel's name, unique within the server and used for subscription and lookup. May not contain
		/// '/'. Set when the channel is created or subscribed to.</summary>
		public string Name { get; set; } = default!;

		/// <summary>The objects currently in this channel that are replicated to this client, keyed by entity id.
		/// Maintained by <see cref="OnObjectAdded"/> / <see cref="OnObjectRemoved"/> as objects are created and removed.</summary>
		public Dictionary<uint, IDistributedObject> DistributedObjects { get; private set; } = new Dictionary<uint, IDistributedObject>();

		/// <summary>Unsubscribes this client from the channel and removes it (and its objects) from the manager.
		/// When <paramref name="immediate"/> is false (default), the channel and its objects stay live and keep
		/// receiving updates until the server acknowledges the unsubscribe, at which point <see cref="IDistributedEntity.OnUndistributed"/>
		/// fires on each and all references are released. When <paramref name="immediate"/> is true, the channel and its
		/// objects are unregistered synchronously and all further incoming updates for them are suppressed (no lifecycle
		/// callbacks) — the caller is responsible for cleaning them up.</summary>
		/// <param name="onComplete">Invoked when the server acknowledges the unsubscribe; the error argument is non-null on failure. May be null.</param>
		/// <param name="immediate">If true, tear down synchronously and suppress further updates instead of waiting for the server ack.</param>
		public void Unsubscribe(ImpunityCallback onComplete, bool immediate = false)
		{
			if (Manager == null)
			{
				onComplete?.Invoke(NotRegisteredError());
				return;
			}

			Manager.UnsubscribeFromChannel(this, onComplete, immediate);
		}

		/// <summary>Called when an object is added to this channel and replicated to this client. The base
		/// implementation adds it to <see cref="DistributedObjects"/>. Fires both for the channel's pre-existing members
		/// at subscribe time and for objects created later.</summary>
		/// <param name="entity">The object added to the channel.</param>
		/// <param name="newlyCreated">True if the object was just created while this client was subscribed; false if it
		/// already existed and is part of the initial snapshot delivered on subscribe.</param>
		public virtual void OnObjectAdded(IDistributedObject entity, bool newlyCreated)
		{
			DistributedObjects.Add(entity.DistributedEntityId, entity);
			OnObjectAddedEvent?.Invoke(entity, newlyCreated);
		}
		/// <summary>Raised by <see cref="OnObjectAdded"/> when an object is added to this channel.</summary>
		public event Action<IDistributedObject, bool>? OnObjectAddedEvent;

		/// <summary>Called when an object is removed from this channel. The base implementation removes it from
		/// <see cref="DistributedObjects"/>.</summary>
		/// <param name="entity">The object removed from the channel.</param>
		public virtual void OnObjectRemoved(IDistributedObject entity)
		{
			DistributedObjects.Remove(entity.DistributedEntityId);
			OnObjectRemovedEvent?.Invoke(entity);
		}
		/// <summary>Raised by <see cref="OnObjectRemoved"/> when an object is removed from this channel.</summary>
		public event Action<IDistributedObject>? OnObjectRemovedEvent;

	}

	/// <summary>Default channel type used when no custom channel class is specified (type ID 0).</summary>
	public class GenericDistributedChannel : DistributedChannelBase
	{
		/// <summary>Creates an empty generic channel (no custom fields).</summary>
		public GenericDistributedChannel() : base()
		{
		}
	}


}
