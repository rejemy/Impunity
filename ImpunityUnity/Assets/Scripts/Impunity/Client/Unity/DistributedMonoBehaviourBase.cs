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
		public uint DistributedEntityId { get; set; }
		public int DistributedEntityType { get; set; }
		public bool IsClientAuthoritative { get; set; }
		public bool IsPersisted { get; set; }
		public bool IsLocked { get; set; }
		private event ImpunityCallback<LockWaitResult>? LockWaiter;

		public ClientEntityManager Manager { get; set; } = null!;

		public ulong DirtyBits { get; private set; }
		public bool DirtyGuaranteed { get; private set; }

		public ushort SendSeq { get; set; }
		public ushort[]? FieldRecvSeq { get; set; }

		protected DistributedMonoBehvaiourEntityBase()
		{
			// Mirror DistributedEntityBase: resolve the intrinsic [DistributedEntity] type id at
			// construction so offline/editor-built instances know their type id without the manager.
			// Pure managed reflection + a field set — no Unity API calls — so it is safe in a
			// MonoBehaviour constructor.
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
			Manager?.Connection?.TriggerEntityEvent(DistributedEntityId, eventType, eventData, onComplete);
		}

		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			Manager?.Connection?.DeleteEntity(DistributedEntityId, deleteData, onComplete);
		}

		public void TryLock(ImpunityCallback<bool> onComplete)
		{
			Manager?.Connection?.TryToLockEntity(DistributedEntityId, onComplete);
		}

		public void WaitForLock(ImpunityCallback<LockWaitResult> onComplete)
		{
			Manager?.Connection?.TryToLockEntity(DistributedEntityId, (err, lockResult) =>
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
			Manager?.Connection?.UnlockEntity(DistributedEntityId, onComplete);
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

	/// <summary>
	/// Base MonoBehaviour implementing <see cref="IDistributedObject"/> for Unity GameObjects that represent
	/// distributed objects.
	/// </summary>
	public abstract class DistributedMonoBehvaiourObjectBase : DistributedMonoBehvaiourEntityBase, IDistributedObject
	{
		public string? UniqueName { get; set; }

		public IDistributedChannel? Channel { get; set; } = null!;

	}

	/// <summary>
	/// Base MonoBehaviour implementing <see cref="IDistributedChannel"/> for Unity GameObjects that represent
	/// distributed channels. Tracks child objects and provides channel lifecycle callbacks.
	/// </summary>
	public abstract class DistributedMonoBehvaiourChannelBase : DistributedMonoBehvaiourEntityBase, IDistributedChannel
	{
		public string Name { get; set; } = null!;
		public Dictionary<uint, IDistributedObject> DistributedObjects { get; private set; } = new Dictionary<uint, IDistributedObject>();

		public void Unsubscribe(ImpunityCallback onComplete, bool immediate = false)
		{
			Manager?.UnsubscribeFromChannel(this, onComplete, immediate);
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
}