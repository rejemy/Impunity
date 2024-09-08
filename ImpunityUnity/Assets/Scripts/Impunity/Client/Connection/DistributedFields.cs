using System;
using System.Collections.Generic;
using System.IO;


using Impunity.GameState;

namespace Impunity.Connection
{

	public interface IDistributedField
	{
		GameStateEntityFieldType FieldType { get; }
		GameStateEntityPropertyValueType ValueType { get; }

	}

	public struct DistributedValue<T> : IDistributedField where T : struct, IDistributableValueType
	{
		T CurrentValue;
		T NewValue;

		public T Get()
        {
			return CurrentValue;
        }

		public bool Set(T newValue, bool immediate, Action<T, T> onChangedMethod = null)
		{
			NewValue = newValue;
			if (NewValue.Equals(CurrentValue))
			{
				return false;
			}

			if (immediate)
			{
				try
				{
					onChangedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onChange method", e);
				}

				CurrentValue = NewValue;
			}

			return true;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			NewValue.WriteTo(w);
		}


		public void ReadChangesFrom(BinaryReader r, Action<T,T> onChangedMethod = null)
		{
			T oldValue = CurrentValue;
			CurrentValue.ReadFrom(r);
			try
			{
				onChangedMethod?.Invoke(oldValue, CurrentValue);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in onChange method", e);
			}
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Value; }
		public GameStateEntityPropertyValueType ValueType { get => CurrentValue.ValueType; }

		public static implicit operator T(DistributedValue<T> d) => d.CurrentValue;
	}


	public struct DistributedArray<T> : IDistributedField where T : struct, IDistributableValueType
	{
		T[] CurrentValue;

		T[] NewValue;
		Dictionary<int, T> Changes;


		public void Init(int size, bool immediate, Action<T[], T[]> onReplacedMethod = null)
		{
			NewValue = new T[size];
			Changes = new Dictionary<int, T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch(Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}
				CurrentValue = NewValue;
			}
		}


		public void Replace(IReadOnlyCollection<T> newArray, bool immediate, Action<T[], T[]> onReplacedMethod = null)
		{
			NewValue = new T[newArray.Count];

			int i = 0;
			foreach (T value in newArray)
			{
				NewValue[i++] = value;
			}

			Changes = new Dictionary<int, T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}
				CurrentValue = NewValue;
			}
		}

		public T Get(int index)
		{
			if(Changes.TryGetValue(index, out T value))
			{
				return value;
			}
			return CurrentValue[index];
		}

		public bool Set(int index, T newValue, bool immediate, Action<int, T, T> onChangedMethod = null)
		{
			if (NewValue != null)
			{
				if (NewValue[index].Equals(newValue))
				{
					return false;
				}

				if (immediate && onChangedMethod != null)
				{
					try
					{
						onChangedMethod.Invoke(index, CurrentValue[index], newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				NewValue[index] = newValue;

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

			if (immediate)
			{
				try
				{
					onChangedMethod?.Invoke(index, CurrentValue[index], newValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onChange method", e);
				}

				CurrentValue[index] = newValue;
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
					NewValue[index].WriteTo(w);
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
					change.Value.WriteTo(w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}


		public void ReadChangesFrom(BinaryReader r, Action<int, T, T> onChangedMethod = null, Action<T[], T[]> onReplacedMethod = null)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					int index = r.ReadUInt16();
					T oldValue = CurrentValue[index];
					CurrentValue[index].ReadFrom(r);
					T newValue = CurrentValue[index];

					if (onChangedMethod != null)
					{
						try
						{
							onChangedMethod.Invoke(index, oldValue, newValue);
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError("Exception in onChange method", e);
						}
					}
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				Changes = new Dictionary<int, T>();

				int arraySize = r.ReadUInt16();

				T[] newValue = new T[arraySize];

				for (int index = 0; index < arraySize; index++)
				{
					newValue[index].ReadFrom(r);
				}

				if(onReplacedMethod != null)
				{
					try
					{
						onReplacedMethod.Invoke(CurrentValue, newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				CurrentValue = newValue;
			}
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Array; }
		public GameStateEntityPropertyValueType ValueType { get => new T().ValueType; }

		public static implicit operator T[](DistributedArray<T> d) => d.CurrentValue;
	}

	public struct DistributedQueue<T> : IDistributedField where T : struct, IDistributableValueType
	{
		int CurrentCapacity;
		Queue<T> CurrentValue;

		int NewCapacity;
		Queue<T> NewValue;
		Queue<T> Changes;
		
		public void Init(int capacity, bool immediate, Action<Queue<T>, Queue<T>> onReplacedMethod = null)
		{
			NewCapacity = capacity;
			NewValue = new Queue<T>();
			Changes = new Queue<T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}

				CurrentCapacity = NewCapacity;
				CurrentValue = NewValue;
			}
		}


		public void Replace(int capacity, IEnumerable<T> initialValues, bool immediate, Action<Queue<T>, Queue<T>> onReplacedMethod = null)
		{
			NewCapacity = capacity;
			NewValue = new Queue<T>();
			Changes = new Queue<T>();

			foreach (T val in initialValues)
			{
				AddToNew(val);
			}

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}

				CurrentCapacity = NewCapacity;
				CurrentValue = NewValue;
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


		public void Add(T newValue, bool immediate, Action<T> onChangedMethod = null)
		{
			if (NewValue != null)
			{
				AddToNew(newValue);

				if (immediate && onChangedMethod != null)
				{
					try
					{
						onChangedMethod.Invoke(newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				return;
			}

			AddToChanges(newValue);

			if (immediate)
			{
				AddToCurrent(newValue);

				try
				{
					onChangedMethod?.Invoke(newValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onChange method", e);
				}
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
					value.WriteTo(w);
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
					change.WriteTo(w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}

		public void ReadChangesFrom(BinaryReader r, Action<T> onChangedMethod = null, Action<Queue<T>, Queue<T>> onReplacedMethod = null)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					T val = default(T);
					val.ReadFrom(r);
					AddToCurrent(val);

					if (onChangedMethod != null)
					{
						try
						{
							onChangedMethod.Invoke(val);
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError("Exception in onChange method", e);
						}
					}
				}
			}
			else if(updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				NewValue = new Queue<T>();
				Changes = new Queue<T>();

				CurrentCapacity = r.ReadUInt16();
				int numValues = r.ReadUInt16();

				Queue<T> newValue = new Queue<T>(numValues);

				for (int index = 0; index < numValues; index++)
				{
					T val = default(T);
					val.ReadFrom(r);
					if (newValue.Count == CurrentCapacity)
					{
						newValue.Dequeue();
					}
					newValue.Enqueue(val);
				}

				if (onReplacedMethod != null)
				{
					try
					{
						onReplacedMethod.Invoke(CurrentValue, newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				CurrentValue = newValue;
			}
			
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Queue; }
		public GameStateEntityPropertyValueType ValueType { get => new T().ValueType; }

		public static implicit operator Queue<T>(DistributedQueue<T> d) => d.CurrentValue;
	}

	public struct DistributedIntDictionary<T> : IDistributedField where T : struct, IDistributableValueType
	{
		Dictionary<int,T> CurrentValue;

		Dictionary<int, T> NewValue;
		Dictionary<int, T> Changes;

		public void Init(bool immediate, Action<Dictionary<int,T>, Dictionary<int,T>> onReplacedMethod = null)
		{
			NewValue = new Dictionary<int, T>();
			Changes = new Dictionary<int, T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}

				CurrentValue = NewValue;
			}
		}


		public void Replace(IReadOnlyDictionary<int,T> initialValues, bool immediate, Action<Dictionary<int,T>, Dictionary<int,T>> onReplacedMethod = null)
		{
			NewValue = new Dictionary<int, T>(initialValues);
			Changes = new Dictionary<int, T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}

				CurrentValue = NewValue;
			}
		}

		public void Add(int key, T newValue, bool immediate, Action<int,T,T> onChangedMethod = null)
		{
			if (NewValue != null)
			{
				if (immediate && onChangedMethod != null)
				{
					T oldValue = NewValue.GetValueOrDefault(key);
					try
					{
						onChangedMethod.Invoke(key, oldValue, newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				NewValue[key] = newValue;

				return;
			}

			Changes[key] = newValue;

			if (immediate)
			{
				if (onChangedMethod != null)
				{
					T oldValue = CurrentValue.GetValueOrDefault(key);
					try
					{
						onChangedMethod.Invoke(key, oldValue, oldValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				CurrentValue[key] = newValue;
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
					pair.Value.WriteTo(w);
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
					pair.Value.WriteTo(w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}

		public void ReadChangesFrom(BinaryReader r, Action<int,T,T> onChangedMethod = null, Action<Dictionary<int,T>,Dictionary<int,T>> onReplacedMethod = null)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					int key = r.ReadInt32();
					T val = default(T);
					val.ReadFrom(r);
					
					if (onChangedMethod != null)
					{
						T oldVal = CurrentValue.GetValueOrDefault(key);
						try
						{
							onChangedMethod.Invoke(key, oldVal, val);
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError("Exception in onChange method", e);
						}
					}

					CurrentValue[key] = val;
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
					T val = default(T);
					val.ReadFrom(r);
					newValue[key] = val;
				}

				if (onReplacedMethod != null)
				{
					try
					{
						onReplacedMethod.Invoke(CurrentValue, newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				CurrentValue = newValue;
			}

		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.IntDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => new T().ValueType; }

		public static implicit operator Dictionary<int, T>(DistributedIntDictionary<T> d) => d.CurrentValue;
	}

	public struct DistributedStringDictionary<T> : IDistributedField where T : struct, IDistributableValueType
	{
		Dictionary<string, T> CurrentValue;

		Dictionary<string, T> NewValue;
		Dictionary<string, T> Changes;

		public void Init(bool immediate, Action<Dictionary<string, T>, Dictionary<string, T>> onReplacedMethod = null)
		{
			NewValue = new Dictionary<string, T>();
			Changes = new Dictionary<string, T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}

				CurrentValue = NewValue;
			}
		}


		public void Replace(IReadOnlyDictionary<string, T> initialValues, bool immediate, Action<Dictionary<string, T>, Dictionary<string, T>> onReplacedMethod = null)
		{
			NewValue = new Dictionary<string, T>(initialValues);
			Changes = new Dictionary<string, T>();

			if (immediate)
			{
				try
				{
					onReplacedMethod?.Invoke(CurrentValue, NewValue);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in onSetMethod method", e);
				}

				CurrentValue = NewValue;
			}
		}

		public void Add(string key, T newValue, bool immediate, Action<string, T, T> onChangedMethod = null)
		{
			if (NewValue != null)
			{
				if (immediate && onChangedMethod != null)
				{
					T oldValue = NewValue.GetValueOrDefault(key);
					try
					{
						onChangedMethod.Invoke(key, oldValue, newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				NewValue[key] = newValue;

				return;
			}

			Changes[key] = newValue;

			if (immediate)
			{
				if (onChangedMethod != null)
				{
					T oldValue = CurrentValue.GetValueOrDefault(key);
					try
					{
						onChangedMethod.Invoke(key, oldValue, oldValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				CurrentValue[key] = newValue;
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
					pair.Value.WriteTo(w);
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
					pair.Value.WriteTo(w);
				}
				Changes.Clear();
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
		}

		public void ReadChangesFrom(BinaryReader r, Action<string, T, T> onChangedMethod = null, Action<Dictionary<string, T>, Dictionary<string, T>> onReplacedMethod = null)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					string key = r.ReadString();
					T val = default(T);
					val.ReadFrom(r);

					if (onChangedMethod != null)
					{
						T oldVal = CurrentValue.GetValueOrDefault(key);
						try
						{
							onChangedMethod.Invoke(key, oldVal, val);
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError("Exception in onChange method", e);
						}
					}

					CurrentValue[key] = val;
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
					T val = default(T);
					val.ReadFrom(r);
					newValue[key] = val;
				}

				if (onReplacedMethod != null)
				{
					try
					{
						onReplacedMethod.Invoke(CurrentValue, newValue);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in onChange method", e);
					}
				}

				CurrentValue = newValue;
			}

		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.StringDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => new T().ValueType; }

		public static implicit operator Dictionary<string, T>(DistributedStringDictionary<T> d) => d.CurrentValue;
	}

}