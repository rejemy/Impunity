using System.Collections.Generic;

namespace Dreamwing.Pooling
{
	public class PocoPool<T> where T : class, new()
	{
		Stack<T> Pool;

		public PocoPool()
		{
			Pool = new Stack<T>();
		}

		public T Get()
		{
			if (Pool.Count > 0)
				return Pool.Pop();

			return new T();
		}

		public void Return(T obj)
		{
			Pool.Push(obj);
		}
	}
}
