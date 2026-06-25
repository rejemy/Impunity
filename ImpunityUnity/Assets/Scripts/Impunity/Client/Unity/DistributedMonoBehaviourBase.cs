using System.Collections.Generic;

using UnityEngine;

using Impunity.Connection;

using UltraLiteDB;
using System;

namespace Impunity.Unity
{

	/// <summary>
	/// Base MonoBehaviour implementing <see cref="IDistributedEntity"/> for Unity GameObjects that participate
	/// in distributed state replication. Provides dirty-bit tracking, lock management, and entity lifecycle callbacks.
	/// Subclass this to create distributed entities that are Unity scene objects.
	/// </summary>
	public abstract class DistributedMonoBehvaiourEntityBase : MonoBehaviour, IDistributedEntity
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
		/// <summary>Pending callbacks registered by <see cref="WaitForLock"/> while the lock was held by another client;
		/// completed with <see cref="LockWaitResult.Unlocked"/> in <see cref="OnUnlocked"/>.</summary>
		private event ImpunityCallback<LockWaitResult>? LockWaiter;

		/// <summary>The <see cref="ClientEntityManager"/> that owns this entity. Assigned when the entity is registered;
		/// entity operations (events, delete, lock/unlock) are routed through its connection.</summary>
		public ClientEntityManager Manager { get; set; } = null!;

		/// <summary>Bitmask of distributed fields with changes not yet sent to the server. Each field's bit is
		/// <c>(1UL &lt;&lt; (fieldId - 1))</c>. Set via <see cref="SetDirty"/> when a field is mutated and cleared by the
		/// manager after the changes are serialized.</summary>
		public ulong DirtyBits { get; private set; }
		/// <summary>True if any pending dirty field requires guaranteed (reliable) delivery; when set, the next update
		/// for this entity is sent reliably rather than as a best-effort datagram. Cleared together with
		/// <see cref="DirtyBits"/>.</summary>
		public bool DirtyGuaranteed { get; private set; }

		/// <summary>Per-entity outgoing sequence counter, incremented each time this entity sends an update.</summary>
		public ushort SendSeq { get; set; }
		/// <summary>Per-field last-received sequence numbers, indexed by field ID. Used to detect and discard stale out-of-order updates.</summary>
		public ushort[]? FieldRecvSeq { get; set; }

		/// <summary>Constructs the entity, resolving its <see cref="DistributedEntityType"/> from the
		/// <see cref="DistributedEntity"/> attribute and calling <see cref="InitializeDistributedFields"/>, so the
		/// instance is usable (including with the manager's persisted-field BSON helpers) even before it is registered.
		/// Uses only managed reflection and a field set — no Unity API calls — so it is safe in a MonoBehaviour constructor.</summary>
		protected DistributedMonoBehvaiourEntityBase()
		{
			// Mirror DistributedEntityBase: resolve the intrinsic [DistributedEntity] type id at
			// construction so offline/editor-built instances know their type id without the manager.
			// Pure managed reflection + a field set — no Unity API calls — so it is safe in a
			// MonoBehaviour constructor.
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
			Manager?.Connection?.TriggerEntityEvent(DistributedEntityId, eventType, eventData, onComplete);
		}

		/// <summary>Requests that the server delete this entity. On success the entity is removed and all subscribers
		/// (including this client) receive <see cref="OnDeleted"/> followed by <see cref="OnUndistributed"/>. Deleting a
		/// channel also removes all of its objects.</summary>
		/// <param name="deleteData">Optional BSON payload relayed to subscribers via <see cref="OnDeleted"/>.</param>
		/// <param name="onComplete">Receives <c>true</c> if the entity was deleted, or <c>false</c> if the request was
		/// rejected because the entity is locked by another client; the error argument is non-null on failure. May be null.</param>
		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			Manager?.Connection?.DeleteEntity(DistributedEntityId, deleteData, onComplete);
		}

		/// <summary>Attempts to acquire the server-side exclusive lock on this entity. While locked, other clients'
		/// updates and deletes are rejected by the server. Release with <see cref="Unlock"/>.</summary>
		/// <param name="onComplete">Receives <c>true</c> if the lock was acquired (or this client already holds it),
		/// <c>false</c> if another client holds it; the error argument is non-null on failure. May be null.</param>
		public void TryLock(ImpunityCallback<bool> onComplete)
		{
			Manager?.Connection?.TryToLockEntity(DistributedEntityId, onComplete);
		}

		/// <summary>Attempts to acquire the exclusive lock and, if it is currently held by another client, waits for it
		/// to become available. The callback fires with <see cref="LockWaitResult.Locked"/> if the lock is granted
		/// immediately, <see cref="LockWaitResult.Error"/> on failure, or — if another client holds it — with
		/// <see cref="LockWaitResult.Unlocked"/> once that client releases the lock. All current waiters are notified on
		/// release. NOTE: the deferred <see cref="LockWaitResult.Unlocked"/> result signals availability only and does
		/// NOT re-acquire the lock; call <see cref="TryLock"/> again to claim it. <see cref="LockWaitResult.Timeout"/> is
		/// never produced by the current implementation (no timeout is applied) — flagged for review.</summary>
		/// <param name="onComplete">Receives the <see cref="LockWaitResult"/> and any error. May be null.</param>
		public void WaitForLock(ImpunityCallback<LockWaitResult> onComplete)
		{
			Manager?.Connection?.TryToLockEntity(DistributedEntityId, (err, lockResult) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, LockWaitResult.Error);
				}
				else if (lockResult)
				{
					onComplete?.Invoke(err, LockWaitResult.Locked);
				}
				else
				{
					LockWaiter += onComplete;
				}
			});
		}

		/// <summary>Releases the exclusive lock this client holds on the entity. On success all subscribers receive
		/// <see cref="OnUnlocked"/>.</summary>
		/// <param name="onComplete">Receives <c>true</c> if the lock was released, <c>false</c> if this client did not
		/// hold the lock; the error argument is non-null on failure. May be null.</param>
		public void Unlock(ImpunityCallback<bool> onComplete)
		{
			Manager?.Connection?.UnlockEntity(DistributedEntityId, onComplete);
		}

		/// <summary>Called when the entity becomes locked on the server, by any client. <see cref="IsLocked"/> is set
		/// true before this fires.</summary>
		public virtual void OnLocked()
		{
			OnLockedEvent?.Invoke();
		}
		/// <summary>Raised by <see cref="OnLocked"/> when the entity becomes locked.</summary>
		public event Action? OnLockedEvent;

		/// <summary>Called when the entity's lock is released on the server. <see cref="IsLocked"/> is set false before
		/// this fires, and any pending <see cref="WaitForLock"/> callbacks are then completed with
		/// <see cref="LockWaitResult.Unlocked"/>.</summary>
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

			try
			{
				LockWaiter?.Invoke(null, LockWaitResult.Unlocked);
			}
			catch (Exception ex)
			{
				ImpunityLogger.LogError("Exception in entity WaitForLock callback handler:", ex);
			}
			LockWaiter = null;
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
		/// <summary>Called when a one-shot event fired on this entity (via <see cref="TriggerEvent"/> on any client,
		/// including this one) is received.</summary>
		/// <param name="eventType">The application-defined event type id supplied by the sender.</param>
		/// <param name="eventData">The BSON payload supplied by the sender.</param>
		public virtual void OnEventTriggered(int eventType, BsonValue eventData)
		{
			OnEventTriggeredEvent?.Invoke(eventType, eventData);
		}
		/// <summary>Raised by <see cref="OnEventTriggered"/> when an event is received for this entity.</summary>
		public event Action<int, BsonValue>? OnEventTriggeredEvent;
		/// <summary>Called when this entity has been deleted on the server. Always followed by
		/// <see cref="OnUndistributed"/>. (Objects of a deleted channel instead receive only <see cref="OnUndistributed"/>.)</summary>
		/// <param name="deleteData">Optional BSON payload supplied to <see cref="Delete"/> by the deleter, or null.</param>
		public virtual void OnDeleted(BsonValue? deleteData)
		{
			OnDeletedEvent?.Invoke(deleteData);
		}
		/// <summary>Raised by <see cref="OnDeleted"/> when the entity is deleted.</summary>
		public event Action<BsonValue?>? OnDeletedEvent;
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

	/// <summary>
	/// Base MonoBehaviour implementing <see cref="IDistributedObject"/> for Unity GameObjects that represent
	/// distributed objects.
	/// </summary>
	public abstract class DistributedMonoBehvaiourObjectBase : DistributedMonoBehvaiourEntityBase, IDistributedObject
	{
		/// <summary>Optional name that is unique within the object's channel. May not contain '/'. For persisted objects
		/// it is used as the database key (the server generates a GUID if none is set). Leave null for an anonymous object.</summary>
		public string? UniqueName { get; set; }

		/// <summary>The channel this object belongs to. Assigned by the <see cref="ClientEntityManager"/> when the object
		/// is created or received, and read on deletion to notify the channel via <see cref="IDistributedChannel.OnObjectRemoved"/>.</summary>
		public IDistributedChannel? Channel { get; set; } = null!;

	}

	/// <summary>
	/// Base MonoBehaviour implementing <see cref="IDistributedChannel"/> for Unity GameObjects that represent
	/// distributed channels. Tracks child objects and provides channel lifecycle callbacks.
	/// </summary>
	public abstract class DistributedMonoBehvaiourChannelBase : DistributedMonoBehvaiourEntityBase, IDistributedChannel
	{
		/// <summary>The channel's name, unique within the server and used for subscription and lookup. May not contain
		/// '/'. Set when the channel is created or subscribed to.</summary>
		public string Name { get; set; } = null!;
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
			Manager?.UnsubscribeFromChannel(this, onComplete, immediate);
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
}
