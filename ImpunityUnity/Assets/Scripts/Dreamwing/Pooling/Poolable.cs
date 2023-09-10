using UnityEngine;

namespace Dreamwing.Pooling
{

	public struct PoolableHandle
	{
		private Poolable Object;
		private int InstanceId;

		public PoolableHandle(Poolable obj)
		{
			if (obj == null)
			{
				Object = null;
				InstanceId = 0;
				return;
			}
			Object = obj;
			InstanceId = Object.InstanceId;
		}

		public T get<T>() where T : Poolable
		{
			if (Object == null || Object.InstanceId != InstanceId)
				return null;
			return Object as T;
		}

		public GameObject get()
		{
			if (Object == null || Object.InstanceId != InstanceId)
				return null;
			return Object.gameObject;
		}

		// Returns true if this was a valid object that has expired
		// returns false if it's valid, or has never been valid
		public bool isExpired()
		{
			return InstanceId != 0 && (Object == null || Object.InstanceId != InstanceId);
		}

		public void clear()
		{
			Object = null;
			InstanceId = 0;
		}

		public static PoolableHandle makeFrom(object obj)
		{
			if (obj is Component)
			{
				if (obj is Poolable)
				{
					return new PoolableHandle((Poolable)obj);
				}

				return new PoolableHandle(((Component)obj).gameObject.GetComponent<Poolable>());
			}
			else if (obj is GameObject)
			{
				return new PoolableHandle(((GameObject)obj).GetComponent<Poolable>());
			}

			return new PoolableHandle(null);
		}
	}

	public class Poolable : MonoBehaviour
	{
		public int PoolType { get; internal set; }
		public int InstanceId { get; internal set; }

		public virtual void OnRecycle()
		{

		}
	}
}