using System;

using UnityEngine;

namespace Dreamwing.Pooling
{

	public class GameObjectPool : MonoBehaviour
	{
		public const string PooledSubObjectTag = "SubObj";

		public static GameObjectPool Instance;

		private int NextInstanceId = 1;
		public Poolable[] Prefabs;
		private Transform[] Pools;

		void Awake()
		{
			Instance = this;
		}

		void Start()
		{
			Init();
		}

		public void Init()
		{
			int numPrefabs = Prefabs.Length;

			Pools = new Transform[numPrefabs];

			for (int i = 0; i < numPrefabs; i++)
			{
				Poolable poolable = Prefabs[i];
				if (poolable == null)
					continue;
				GameObject prefab = poolable.gameObject;

				GameObject pool = new GameObject(prefab.name + "Pool");
				pool.transform.SetParent(transform, false);
				pool.SetActive(false);
				Pools[i] = pool.transform;
			}
		}

		private GameObject GetPooled(int type)
		{
			GameObject inst = null;
			Transform pool = Pools[type];
			if (pool.childCount == 0)
			{
				GameObject prefab = Prefabs[type].gameObject;
				inst = GameObject.Instantiate(prefab);
			}
			else
			{
				inst = pool.GetChild(pool.childCount - 1).gameObject;
			}

			Poolable poolComponent = inst.GetComponent<Poolable>();
			poolComponent.PoolType = type;
			poolComponent.InstanceId = NextInstanceId++;

			if (NextInstanceId == 0)
			{
				// Skip 0 as a valid instance id
				NextInstanceId = 1;
			}

			return inst;
		}


		public GameObject Instantiate(int type, Transform parent)
		{
			GameObject inst = GetPooled(type);
			inst.transform.SetParent(parent, true);
			return inst;
		}

		public T Instantiate<T>(int type, Transform parent)
		{
			return Instantiate(type, parent).GetComponent<T>();
		}

		public GameObject Instantiate(int type, Transform parent, Vector3 localPostion)
		{
			GameObject inst = GetPooled(type);
			inst.transform.SetParent(parent, true);
			inst.transform.localPosition = localPostion;

			return inst;
		}

		public T Instantiate<T>(int type, Transform parent, Vector3 localPostion)
		{
			return Instantiate(type, parent, localPostion).GetComponent<T>();
		}

		public void Recycle(Poolable obj)
		{
			Recycle(obj.gameObject);
		}

		public void Recycle(GameObject obj)
		{
			// First all children
			int c = obj.transform.childCount;
			while (c > 0)
			{
				GameObject child = obj.transform.GetChild(c - 1).gameObject;
				Recycle(child);
				c--;
			}

			if (obj.tag == PooledSubObjectTag)
			{
				// Don't do anything to pooled object subobjects, they stay with their parent
				return;
			}

			Poolable poolComponent = obj.GetComponent<Poolable>();
			if (poolComponent == null)
			{
				// Not recycleable, just destroy
				GameObject.Destroy(obj);
				return;
			}
			else
			{
				Transform pool = Pools[(int)poolComponent.PoolType];
				if (obj.transform.parent == pool)
				{
					// already recycled
					return;
				}

				// Let object clean itself up
				try
				{
					poolComponent.OnRecycle();
				}
				catch (Exception e)
				{
					Debug.LogError("Exception recycling poolable object:");
					Debug.LogException(e);
				}

				// Move to pool
				obj.transform.SetParent(pool, false);
				poolComponent.InstanceId = 0;

				// Reset gameobject stuff
				obj.name = "Pooled object";
				obj.SetActive(true);
				obj.transform.localPosition = new Vector3();
				obj.transform.localRotation = new Quaternion();
				obj.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);


			}
		}
	}

}