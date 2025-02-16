using System.Collections.Generic;

using UnityEngine;

using Impunity.Connection;

using UltraLiteDB;

namespace Impunity.Unity
{

	public abstract class DistributedMonoBehvaiourEntityBase : MonoBehaviour, IDistributedEntity
	{
		public string Name { get; set; }
		public uint DistributedEntityId { get; set; }
		public int DistributedEntityType { get; set; }
		public bool IsClientAuthoritative { get; set; }

		public ClientEntityManager Manager { get; set; }
		public IDistributedChannel Channel { get; set; }

		public ulong DirtyBits { get; private set; }
		public void SetDirty(byte fieldId)
		{
			DirtyBits |= 1ul << (fieldId - 1);
			Manager?.SetDirty(this);
		}

		public void ClearDirty()
		{
			DirtyBits = 0ul;
		}

		public virtual void InitializeDistributedFields() {}

		public void TriggerEvent(int eventType, BsonValue eventData, ImpunityCallback onComplete)
		{
			Manager.Connection.TriggerEntityEvent(DistributedEntityId, eventType, eventData, onComplete);
		}

		public void Delete(BsonValue deleteData, ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.DeleteEntity(DistributedEntityId, null, deleteData, onComplete);
		}

		public void Lock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.TryToLockEntity(DistributedEntityId, onComplete);
		}

		public void Lock(string key, ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.TryToLockEntity(DistributedEntityId, key, onComplete);
		}

		public void Unlock(ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.UnlockEntity(DistributedEntityId, onComplete);
		}

		public void Unlock(string key, ImpunityCallback<bool> onComplete)
		{
			Manager.Connection.UnlockEntity(DistributedEntityId, key, onComplete);
		}

		public virtual void OnEventTriggered(int eventType, BsonValue eventData) { }
		public virtual void OnDeleted(BsonValue deleteData) { }
		public virtual void OnUndistributed() { }
	}

	public abstract class DistributedMonoBehvaiourChannelBase : DistributedMonoBehvaiourEntityBase, IDistributedChannel
	{
		public Dictionary<uint, IDistributedEntity> DistributedObjects { get; private set; } = new Dictionary<uint, IDistributedEntity>();

		public void Unsubscribe(ImpunityCallback onComplete)
		{
			Manager.UnsubscribeFromChannel(this, onComplete);
		}

		public virtual void OnObjectAdded(IDistributedEntity entity, bool newlyCreated)
		{
			DistributedObjects.Add(entity.DistributedEntityId, entity);
		}

		public virtual void OnObjectRemoved(uint entityId, bool destroyed)
		{
			DistributedObjects.Remove(entityId);
		}

	}
}