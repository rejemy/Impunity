using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UltraLiteDB;


namespace Impunity.Connection
{

	/// <summary>Marks a class as a distributed entity type with a unique integer ID. Optionally specifies a factory method and persistence key.</summary>
	[AttributeUsage(AttributeTargets.Class)]
	public class DistributedEntity : Attribute
	{
		internal int EntityId;
		public string? FactoryMethod { get; set; }
		public string? PersistAs { get; set; }

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

	/// <summary>Marks a field as a distributed property with a unique byte ID (1-63). Optionally specifies a persistence key.</summary>
	[AttributeUsage(AttributeTargets.Field)]
	public class Distributed : Attribute
	{
		internal byte FieldId;
		public string? PersistAs { get; set; }

		public Distributed(byte fieldId)
		{
			FieldId = fieldId;
		}
	}


	/// <summary>Client-side interface for a distributed entity. Provides identity, dirty tracking, lock/event/delete operations, and lifecycle callbacks.</summary>
	public interface IDistributedEntity
	{
		uint DistributedEntityId { get; set; }
		int DistributedEntityType { get; set; }
		bool IsClientAuthoritative { get; set; }
		bool IsPersisted { get; set; }
		bool IsLocked { get; set; }

		ClientEntityManager Manager { get; set; }

		ulong DirtyBits { get; }
		bool DirtyGuaranteed { get; }

		/// <summary>Per-entity outgoing sequence counter, incremented each time this entity sends an update.</summary>
		ushort SendSeq { get; set; }
		/// <summary>Per-field last-received sequence numbers, indexed by field ID. Used to detect and discard stale out-of-order updates.</summary>
		ushort[]? FieldRecvSeq { get; set; }

		void SetDirty(ulong fieldBitmask, bool guaranteed);
		void ClearDirty();
		void InitializeDistributedFields();

		// Distributed actions client->server
		void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete);
		void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete);
		void TryLock(ImpunityCallback<bool> onComplete);
		void WaitForLock(ImpunityCallback<LockWaitResult> onComplete);
		void Unlock(ImpunityCallback<bool> onComplete);

		// Distributed events server->client
		void OnFullyInitialized();
		event Action? OnFullyInitializedEvent;

		void OnEventTriggered(int eventType, BsonValue eventData);
		event Action<int,BsonValue>? OnEventTriggeredEvent;

		// When an object is specifically deleted 
		void OnDeleted(BsonValue? deleteData);
		event Action<BsonValue?>? OnDeletedEvent;

		void OnLocked();
		event Action? OnLockedEvent;

		void OnUnlocked();
		event Action? OnUnlockedEvent;

		// When an object is no longer distributed due to the client unsubscribing from a channel,
		// or the channel being deleted, also called after OnDeleted
		void OnUndistributed();
		event Action? OnUndistributedEvent;
	}

	/// <summary>Client-side interface for a distributed object entity.</summary>
	public interface IDistributedObject : IDistributedEntity
	{
		string? UniqueName { get; set; }

		IDistributedChannel? Channel { get; set; }
	}

	/// <summary>Client-side interface for a distributed channel entity. Channels contain child objects and support subscription.</summary>
	public interface IDistributedChannel : IDistributedEntity
	{
		string Name { get; set; }
		Dictionary<uint, IDistributedObject> DistributedObjects { get; }

		void Unsubscribe(ImpunityCallback onComplete, bool immediate = false);

		void OnObjectAdded(IDistributedObject entity, bool newlyCreated);
		event Action<IDistributedObject,bool>? OnObjectAddedEvent;

		void OnObjectRemoved(IDistributedObject entity);
		event Action<IDistributedObject>? OnObjectRemovedEvent;
	}

	/// <summary>Base implementation of <see cref="IDistributedEntity"/> with dirty-bit tracking, lock waiting, and lifecycle callback stubs.</summary>
	public abstract class DistributedEntityBase : IDistributedEntity
	{
		public uint DistributedEntityId { get; set; }
		public int DistributedEntityType { get; set; }
		public bool IsClientAuthoritative { get; set; }
		public bool IsPersisted { get; set; }
		public bool IsLocked { get; set; }
		private event ImpunityCallback<LockWaitResult>? LockWaiter;

		public ClientEntityManager Manager { get; set; } = default!;

		public ulong DirtyBits { get; private set; }
		public bool DirtyGuaranteed { get; private set; }

		public ushort SendSeq { get; set; }
		public ushort[]? FieldRecvSeq { get; set; }

		protected DistributedEntityBase()
		{
			// The type id is intrinsic to the [DistributedEntity] attribute, so populate it at
			// construction. This makes offline/editor-built instances (which never go through
			// CreateObject/HandleCreate*) usable with the manager's persisted-field BSON methods.
			DistributedEntityType = DistributedEntity.GetEntityTypeId(GetType());
			InitializeDistributedFields();
		}

		public void SetDirty(ulong fieldBitmask, bool guaranteed)
        {
			DirtyBits |= fieldBitmask;
			DirtyGuaranteed |= guaranteed;
			Manager?.SetDirty(this);
		}

		public void ClearDirty()
        {
			DirtyBits = 0ul;
			DirtyGuaranteed = false;
		}

		public virtual void InitializeDistributedFields() {}

		public void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			Manager.Connection?.TriggerEntityEvent(DistributedEntityId, eventType, eventData, onComplete);
		}

		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			Manager.Connection?.DeleteEntity(DistributedEntityId, deleteData, onComplete);
		}

		public void TryLock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection?.TryToLockEntity(DistributedEntityId, onComplete);
		}

		public void WaitForLock(ImpunityCallback<LockWaitResult> onComplete)
		{
			Manager.Connection?.TryToLockEntity(DistributedEntityId, (err, lockResult) =>
			{
				if (err != null)
				{
					onComplete?.Invoke(err, LockWaitResult.Error);
				}
				else if(lockResult)
				{
					onComplete?.Invoke(err, LockWaitResult.Locked);
				}
				else
				{
					LockWaiter += onComplete;
				}
			});
		}


		public void Unlock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection?.UnlockEntity(DistributedEntityId, onComplete);
		}
		
		public virtual void OnLocked()
		{
			OnLockedEvent?.Invoke();
		}
		public event Action? OnLockedEvent;

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
		public event Action? OnUnlockedEvent;

		public virtual void OnFullyInitialized()
		{
			OnFullyInitializedEvent?.Invoke();
		}
		public event Action? OnFullyInitializedEvent;

		public virtual void OnEventTriggered(int eventType, BsonValue eventData)
		{
			OnEventTriggeredEvent?.Invoke(eventType, eventData);
		}
		public event Action<int,BsonValue>? OnEventTriggeredEvent;

		public virtual void OnDeleted(BsonValue? deleteData)
		{
			OnDeletedEvent?.Invoke(deleteData);
		}
		public event Action<BsonValue?>? OnDeletedEvent;

		public virtual void OnUndistributed()
		{
			OnUndistributedEvent?.Invoke();
		}
		public event Action? OnUndistributedEvent;
	}

	/// <summary>Base implementation of <see cref="IDistributedObject"/>.</summary>
	public abstract class DistributedObjectBase : DistributedEntityBase, IDistributedObject
	{
		public string? UniqueName { get; set; }

		public IDistributedChannel? Channel { get; set; } = null!;
	}

	/// <summary>Base implementation of <see cref="IDistributedChannel"/>. Maintains a dictionary of child objects and supports unsubscription.</summary>
	public abstract class DistributedChannelBase : DistributedEntityBase, IDistributedChannel
	{
		public string Name { get; set; } = default!;

		public Dictionary<uint, IDistributedObject> DistributedObjects { get; private set; } = new Dictionary<uint, IDistributedObject>();

		public void Unsubscribe(ImpunityCallback onComplete, bool immediate = false)
		{
			Manager.UnsubscribeFromChannel(this, onComplete, immediate);
		}

		public virtual void OnObjectAdded(IDistributedObject entity, bool newlyCreated)
		{
			DistributedObjects.Add(entity.DistributedEntityId, entity);
			OnObjectAddedEvent?.Invoke(entity, newlyCreated);
		}
		public event Action<IDistributedObject,bool>? OnObjectAddedEvent;

		public virtual void OnObjectRemoved(IDistributedObject entity)
        {
			DistributedObjects.Remove(entity.DistributedEntityId);
			OnObjectRemovedEvent?.Invoke(entity);
		}
		public event Action<IDistributedObject>? OnObjectRemovedEvent;

	}

	/// <summary>Default channel type used when no custom channel class is specified (type ID 0).</summary>
	public class GenericDistributedChannel : DistributedChannelBase
	{
		public GenericDistributedChannel() : base()
		{
		}
	}


}