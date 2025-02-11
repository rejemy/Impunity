using System;
using System.Collections.Generic;
using System.IO;

namespace Impunity.Connection
{
	
	public interface IDistributedField
	{
		GameStateEntityFieldType FieldType { get; }
		GameStateEntityPropertyValueType ValueType { get; }
	}

	public struct DistributedValue<T,S> : IDistributedField where S : IDistributableValueSerializer<T>
	{
		public event Action<T,T> OnChanged;
		private static readonly S Serializer = default;
		
		IDistributedEntity Entity;
		byte FieldId;

		T CurrentValue;
		T NewValue;

		public void Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldId = fieldId;
		}

		public readonly T Get()
        {
			return CurrentValue;
        }

		public bool Set(T newValue)
		{
			NewValue = newValue;
			if (NewValue.Equals(CurrentValue))
			{
				return false;
			}

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		public readonly void WriteChangesTo(BinaryWriter w)
		{
			Serializer.WriteTo(NewValue, w);
		}


		public void ReadChangesFrom(BinaryReader r)
		{
			T oldValue = CurrentValue;
			CurrentValue = Serializer.ReadFrom(r);

			InvokeOnChanged(oldValue, CurrentValue);
		}

		private readonly void InvokeOnChanged(T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in onChange method", e);
			}
		}

		public readonly GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Value; }
		public readonly GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator T(DistributedValue<T,S> d) => d.CurrentValue;
	}

	public struct DistributedArray<T,S> : IDistributedField where S : IDistributableValueSerializer<T>
	{
		public event Action<T[],T[]> OnReplaced;
		public event Action<int,T,T> OnChanged;
		private static readonly S Serializer = default;

		T[] CurrentValue;
		T[] NewValue;
		Dictionary<int, T> Changes;

		IDistributedEntity Entity;
		byte FieldId;

		public void Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldId = fieldId;
		}

		public void Init(int size)
		{
			NewValue = new T[size];
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				T[] oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		public void Replace(IReadOnlyCollection<T> newArray)
		{
			NewValue = new T[newArray.Count];

			int i = 0;
			foreach (T value in newArray)
			{
				NewValue[i++] = value;
			}

			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				T[] oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		public readonly T Get(int index)
		{
			if(Changes.TryGetValue(index, out T value))
			{
				return value;
			}
			return CurrentValue[index];
		}

		public bool Set(int index, T newValue)
		{
			if (NewValue != null)
			{
				if (NewValue[index].Equals(newValue))
				{
					return false;
				}

				T oldValue = NewValue[index];
				NewValue[index] = newValue;

				Entity.SetDirty(FieldId);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(index, oldValue, newValue);
				}

				return true;
			}

			if (CurrentValue == null)
			{
				throw new Exception("Array must be initialized with a call to Init or Replace before a value can be set");
			}

			if (CurrentValue[index].Equals(newValue))
			{
				return false;
			}

			Changes.Add(index, newValue);

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue[index];
				CurrentValue[index] = newValue;

				InvokeOnChanged(index, oldValue, newValue);
			}

			return true;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			if (NewValue != null)
			{
				// Resend entire array
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)NewValue.Length);
				for(int index = 0; index < NewValue.Length; index++)
				{
					Serializer.WriteTo(NewValue[index], w);
				}
				NewValue = null;
			}
			else if (Changes != null)
			{
				// Send only changes
				w.Write((byte)DistributedCollectionUpdateType.Update);
				w.Write((ushort)Changes.Count);
				foreach (var change in Changes)
				{
					w.Write((ushort)change.Key);
					Serializer.WriteTo(change.Value, w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}


		public void ReadChangesFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					int index = r.ReadUInt16();
					T oldValue = CurrentValue[index];
					CurrentValue[index] = Serializer.ReadFrom(r);
					T newValue = CurrentValue[index];

					InvokeOnChanged(index, oldValue, newValue);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				Changes = new Dictionary<int, T>();

				int arraySize = r.ReadUInt16();

				T[] oldValue = CurrentValue;
				T[] newValue = new T[arraySize];

				for (int index = 0; index < arraySize; index++)
				{
					newValue[index] = Serializer.ReadFrom(r);
				}

				CurrentValue = newValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		private readonly void InvokeOnChanged(int index, T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(index, oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(T[] oldValue, T[] newValue)
		{
			try
			{
				OnReplaced?.Invoke(oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnReplaced handler method", e);
			}
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Array; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator T[](DistributedArray<T,S> d) => d.CurrentValue;
	}

	public struct DistributedQueue<T,S> : IDistributedField where S : IDistributableValueSerializer<T>
	{
		public event Action<T> OnChanged;
		public event Action<Queue<T>, Queue<T>> OnReplaced;

		private static readonly S Serializer = default;

		int CurrentCapacity;
		Queue<T> CurrentValue;

		int NewCapacity;
		Queue<T> NewValue;
		Queue<T> Changes;

		IDistributedEntity Entity;
		byte FieldId;

		public void Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldId = fieldId;
		}

		public void Init(int capacity)
		{
			NewCapacity = capacity;
			NewValue = new Queue<T>();
			Changes = new Queue<T>();

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				CurrentCapacity = NewCapacity;
				Queue<T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		public void Replace(int capacity, IEnumerable<T> initialValues)
		{
			NewCapacity = capacity;
			NewValue = new Queue<T>();
			Changes = new Queue<T>();

			foreach (T val in initialValues)
			{
				AddToNew(val);
			}

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				CurrentCapacity = NewCapacity;
				Queue<T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		private void AddToCurrent(T value)
		{
			if(CurrentValue.Count == CurrentCapacity)
			{
				CurrentValue.Dequeue();
			}

			CurrentValue.Enqueue(value);
		}

		private void AddToNew(T value)
		{
			if (NewValue.Count == NewCapacity)
			{
				NewValue.Dequeue();
			}

			NewValue.Enqueue(value);
		}

		private void AddToChanges(T value)
		{
			if (Changes.Count == CurrentCapacity)
			{
				Changes.Dequeue();
			}

			Changes.Enqueue(value);
		}


		public void Add(T newValue)
		{
			if (NewValue != null)
			{
				AddToNew(newValue);

				Entity.SetDirty(FieldId);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(newValue);
				}

				return;
			}

			AddToChanges(newValue);

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				AddToCurrent(newValue);

				InvokeOnChanged(newValue);
			}
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			if (NewValue != null)
			{
				// Resend entire queue
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)NewCapacity);
				w.Write((ushort)NewValue.Count);
				foreach (T value in NewValue)
				{
					Serializer.WriteTo(value, w);
				}
				NewValue = null;
			}
			else if (Changes != null)
			{
				// Send only changes
				w.Write((byte)DistributedCollectionUpdateType.Update);
				w.Write((ushort)Changes.Count);
				foreach (var change in Changes)
				{
					Serializer.WriteTo(change, w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					T val = Serializer.ReadFrom(r);
					AddToCurrent(val);

					InvokeOnChanged(val);
				}
			}
			else if(updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				NewValue = new Queue<T>();
				Changes = new Queue<T>();

				CurrentCapacity = r.ReadUInt16();
				int numValues = r.ReadUInt16();

				Queue<T> newValue = new Queue<T>(numValues);
				Queue<T> oldValue = CurrentValue;
				CurrentValue = newValue;

				for (int index = 0; index < numValues; index++)
				{
					T val = Serializer.ReadFrom(r);
					if (newValue.Count == CurrentCapacity)
					{
						newValue.Dequeue();
					}
					newValue.Enqueue(val);
				}

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		private readonly void InvokeOnChanged(T newValue)
		{
			try
			{
				OnChanged?.Invoke(newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(Queue<T> oldValue, Queue<T> newValue)
		{
			try
			{
				OnReplaced?.Invoke(oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnReplaced handler method", e);
			}
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Queue; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Queue<T>(DistributedQueue<T,S> d) => d.CurrentValue;
	}

	public struct DistributedIntDictionary<T,S> : IDistributedField where S : IDistributableValueSerializer<T>
	{
		public event Action<int,T,T> OnChanged;
		public event Action<Dictionary<int,T>,Dictionary<int,T>> OnReplaced;

		private static readonly S Serializer = default;

		Dictionary<int,T> CurrentValue;

		Dictionary<int, T> NewValue;
		Dictionary<int, T> Changes;

		IDistributedEntity Entity;
		byte FieldId;

		public void Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldId = fieldId;
		}

		public void Init()
		{
			NewValue = new Dictionary<int, T>();
			Changes = new Dictionary<int, T>();
			
			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				Dictionary<int,T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		public void Replace(IReadOnlyDictionary<int,T> initialValues)
		{
			NewValue = new Dictionary<int, T>(initialValues);
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				Dictionary<int,T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		public void Add(int key, T newValue)
		{
			if (NewValue != null)
			{
				T oldValue = NewValue.GetValueOrDefault(key);
				NewValue[key] = newValue;

				Entity.SetDirty(FieldId);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(key, oldValue, newValue);
				}

				return;
			}

			Changes[key] = newValue;

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue.GetValueOrDefault(key);
				CurrentValue[key] = newValue;

				InvokeOnChanged(key, oldValue, newValue);
			}

		}

		public T Get(int key)
		{
			if (CurrentValue == null)
			{
				return default(T);
			}
			return CurrentValue.GetValueOrDefault(key);
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			if (NewValue != null)
			{
				// Resend entire dictionary
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)NewValue.Count);
				foreach (var pair in NewValue)
				{
					w.Write(pair.Key);
					Serializer.WriteTo(pair.Value, w);
				}
				NewValue = null;
			}
			else if (Changes != null)
			{
				// Send only changes
				w.Write((byte)DistributedCollectionUpdateType.Update);
				w.Write((ushort)Changes.Count);
				foreach (var pair in Changes)
				{
					w.Write(pair.Key);
					Serializer.WriteTo(pair.Value, w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					int key = r.ReadInt32();
					T val = Serializer.ReadFrom(r);
					
					T oldVal = CurrentValue.GetValueOrDefault(key);
					CurrentValue[key] = val;

					InvokeOnChanged(key, oldVal, val);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				NewValue = new Dictionary<int, T>();
				Changes = new Dictionary<int, T>();

				int numValues = r.ReadUInt16();
				Dictionary<int, T> newValue = new Dictionary<int, T>();

				for (int index = 0; index < numValues; index++)
				{
					int key = r.ReadInt32();
					T val = Serializer.ReadFrom(r);
					newValue[key] = val;
				}

				var oldValue = CurrentValue;
				CurrentValue = newValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}

		}

		private readonly void InvokeOnChanged(int key, T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(key, oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(Dictionary<int,T> oldValue, Dictionary<int,T> newValue)
		{
			try
			{
				OnReplaced?.Invoke(oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnReplaced handler method", e);
			}
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.IntDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Dictionary<int, T>(DistributedIntDictionary<T,S> d) => d.CurrentValue;
	}

	public struct DistributedStringDictionary<T,S> : IDistributedField where S : IDistributableValueSerializer<T>
	{
		public event Action<string,T,T> OnChanged;
		public event Action<Dictionary<string,T>,Dictionary<string,T>> OnReplaced;

		private static readonly S Serializer = default;

		Dictionary<string, T> CurrentValue;

		Dictionary<string, T> NewValue;
		Dictionary<string, T> Changes;

		IDistributedEntity Entity;
		byte FieldId;

		public void Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldId = fieldId;
		}

		public void Init()
		{
			NewValue = new Dictionary<string, T>();
			Changes = new Dictionary<string, T>();

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				var oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		public void Replace(IReadOnlyDictionary<string, T> initialValues)
		{
			NewValue = new Dictionary<string, T>(initialValues);
			Changes = new Dictionary<string, T>();

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				var oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		public void Add(string key, T newValue)
		{
			if (NewValue != null)
			{
				T oldValue = NewValue.GetValueOrDefault(key);
				NewValue[key] = newValue;

				Entity.SetDirty(FieldId);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(key, oldValue, newValue);
				}

				return;
			}

			Changes[key] = newValue;

			Entity.SetDirty(FieldId);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue.GetValueOrDefault(key);
				CurrentValue[key] = newValue;

				InvokeOnChanged(key, oldValue, newValue);
			}

		}

		public T Get(string key)
		{
			if (CurrentValue == null)
			{
				return default(T);
			}
			return CurrentValue.GetValueOrDefault(key);
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			if (NewValue != null)
			{
				// Resend entire dictionary
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)NewValue.Count);
				foreach (var pair in NewValue)
				{
					w.Write(pair.Key);
					Serializer.WriteTo(pair.Value, w);
				}
				NewValue = null;
			}
			else if (Changes != null)
			{
				// Send only changes
				w.Write((byte)DistributedCollectionUpdateType.Update);
				w.Write((ushort)Changes.Count);
				foreach (var pair in Changes)
				{
					w.Write(pair.Key);
					Serializer.WriteTo(pair.Value, w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					string key = r.ReadString();
					T val = Serializer.ReadFrom(r);

					T oldVal = CurrentValue.GetValueOrDefault(key);
					CurrentValue[key] = val;

					InvokeOnChanged(key, oldVal, val);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				NewValue = new Dictionary<string, T>();
				Changes = new Dictionary<string, T>();

				int numValues = r.ReadUInt16();
				Dictionary<string, T> newValue = new Dictionary<string, T>();

				for (int index = 0; index < numValues; index++)
				{
					string key = r.ReadString();
					T val = Serializer.ReadFrom(r);
					newValue[key] = val;
				}

				var oldValue = CurrentValue;
				CurrentValue = newValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}

		}

		private readonly void InvokeOnChanged(string key, T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(key, oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(Dictionary<string,T> oldValue, Dictionary<string,T> newValue)
		{
			try
			{
				OnReplaced?.Invoke(oldValue, newValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnReplaced handler method", e);
			}
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.StringDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Dictionary<string, T>(DistributedStringDictionary<T,S> d) => d.CurrentValue;
	}

}