using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;


namespace Impunity.Connection
{
	
	/// <summary>Base interface for all client-side distributed field types. Provides binary serialization for wire protocol and read/write lifecycle.</summary>
	public interface IDistributedField
	{
		void WriteChangesTo(BinaryWriter w);
		void ReadInitialFrom(BinaryReader r);
		void ReadChangesFrom(BinaryReader r);
		/// <summary>Reads and discards field data from the stream, advancing the reader position without applying changes. Used to skip stale out-of-order updates.</summary>
		void SkipFrom(BinaryReader r);

		GameStateEntityFieldType FieldType { get; }
		GameStateEntityPropertyValueType ValueType { get; }
	}

	/// <summary>Extended distributed field that tracks the server timestamp of the last modification. Used for time-sensitive interpolation or age-based logic.</summary>
	public interface IDistributedTemporalField : IDistributedField
	{
		/// <summary>Server time (Unix milliseconds) when this field was last modified.</summary>
		long LastModifiedTime { get; set; }
	}

	/// <summary>
	/// Client-side distributed single value. Tracks local changes and syncs them to the server via dirty-bit mechanism.
	/// Supports client-authoritative mode where local changes are applied immediately before server confirmation.
	/// </summary>
	/// <typeparam name="T">The value type (must be equatable for dirty detection).</typeparam>
	/// <typeparam name="S">The serializer struct used for binary read/write.</typeparam>
	public struct DistributedValue<T, S> : IDistributedField where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when the value changes, providing old and new values.</summary>
		public event Action<T, T> OnChanged;
		private static readonly S Serializer = default!;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		T CurrentValue;
		T NewValue;

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Returns the current replicated value.</summary>
		public readonly T Get()
		{
			return CurrentValue;
		}

		/// <summary>Sets the value and marks the field dirty. Returns false if the value is unchanged (unless <paramref name="force"/> is true).</summary>
		public bool Set(T newValue, bool force = false)
		{
			NewValue = newValue;
			if (!force && Equals(NewValue, CurrentValue))
			{
				return false;
			}

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		/// <summary>Sets the value and marks the field dirty. Will be sent as an unguaranteed update if possible.</summary>
		public bool SetUnguaranteed(T newValue, bool force = false)
		{
			NewValue = newValue;
			if (!force && Equals(NewValue, CurrentValue))
			{
				return false;
			}

			Entity.SetDirty(FieldBitmask, false);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		/// <summary>Updates the value locally without sending to the server. Useful for client-side prediction or cosmetic state.</summary>
		public bool SetLocalOnly(T newValue, bool force = false)
		{
			NewValue = newValue;
			if (!force && Equals(NewValue, CurrentValue))
			{
				return false;
			}

			T oldValue = CurrentValue;
			CurrentValue = NewValue;

			InvokeOnChanged(oldValue, CurrentValue);

			return true;
		}

		/// <inheritdoc/>
		public readonly void WriteChangesTo(BinaryWriter w)
		{
			Serializer.WriteTo(NewValue, w);
		}

		/// <inheritdoc/>
		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
		}

		/// <inheritdoc/>
		public void ReadChangesFrom(BinaryReader r)
		{
			T oldValue = CurrentValue;
			CurrentValue = Serializer.ReadFrom(r);

			InvokeOnChanged(oldValue, CurrentValue);
		}

		/// <inheritdoc/>
		public void SkipFrom(BinaryReader r)
		{
			Serializer.ReadFrom(r);
		}

		private readonly void InvokeOnChanged(T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(oldValue, newValue);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in onChange method", e);
			}
		}

		public static bool Equals(T obj1, T obj2)
		{
			if (obj1 == null)
			{
				return obj2 == null;
			}
			return obj1.Equals(obj2);
		}

		public bool Equals(T other)
		{
			return Equals(CurrentValue, other);
		}

		public readonly GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Value; }
		public readonly GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator T(DistributedValue<T, S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed value that also tracks the server-time of the last modification.
	/// On initial load, provides the age of the value so clients can interpolate or compensate for staleness.
	/// </summary>
	/// <typeparam name="T">The value type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedTemporalValue<T,S> : IDistributedTemporalField where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised on initial load with the current value and its age (time since last server modification).</summary>
		public event Action<T,TimeSpan> OnInitialized;
		/// <summary>Raised when the value changes, providing old and new values.</summary>
		public event Action<T,T> OnChanged;

		private static readonly S Serializer = default!;
		
		IDistributedEntity Entity;
		ulong FieldBitmask;

		T CurrentValue;
		T NewValue;

		public long LastModifiedTime { get; set; }

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		public readonly T Get()
        {
			return CurrentValue;
        }

		public bool Set(T newValue, bool force = false)
		{
			NewValue = newValue;
			if (!force && Equals(NewValue, CurrentValue))
			{
				return false;
			}

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				LastModifiedTime = Entity.Manager.Connection.GetServerTime();

				T oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		public bool SetUnguaranteed(T newValue, bool force = false)
		{
			NewValue = newValue;
			if (!force && Equals(NewValue, CurrentValue))
			{
				return false;
			}

			Entity.SetDirty(FieldBitmask, false);

			if (Entity.IsClientAuthoritative)
			{
				LastModifiedTime = Entity.Manager.Connection.GetServerTime();

				T oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		public bool SetLocalOnly(T newValue, bool force = false)
		{
			NewValue = newValue;
			if (!force && Equals(NewValue, CurrentValue))
			{
				return false;
			}

			LastModifiedTime = Entity.Manager.Connection.GetServerTime();

			T oldValue = CurrentValue;
			CurrentValue = NewValue;

			InvokeOnChanged(oldValue, CurrentValue);
			
			return true;
		}

		public readonly void WriteChangesTo(BinaryWriter w)
		{
			Serializer.WriteTo(NewValue, w);
		}

		public void ReadInitialFrom(BinaryReader r)
		{
			long ageMilliseconds = r.ReadUInt32();
			LastModifiedTime = Entity.Manager.Connection.GetServerTime() - ageMilliseconds;

			CurrentValue = Serializer.ReadFrom(r);

			InvokeOnInitialized(CurrentValue, TimeSpan.FromMilliseconds(ageMilliseconds));
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			LastModifiedTime = Entity.Manager.Connection.GetServerTime();

			T oldValue = CurrentValue;
			CurrentValue = Serializer.ReadFrom(r);

			InvokeOnChanged(oldValue, CurrentValue);
		}

		/// <inheritdoc/>
		public void SkipFrom(BinaryReader r)
		{
			Serializer.ReadFrom(r);
		}

		private readonly void InvokeOnInitialized(T newValue, TimeSpan age)
		{
			try
			{
				OnInitialized?.Invoke(newValue, age);
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception in OnInitialized method", e);
			}
		}

		private readonly void InvokeOnChanged(T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(oldValue, newValue);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged method", e);
			}
		}

		public static bool Equals(T obj1, T obj2)
		{
			if (obj1 == null)
			{
				return obj2 == null;
			}
			return obj1.Equals(obj2);
		}

		public bool Equals(T other)
		{
			return Equals(CurrentValue, other);
		}

		public readonly GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Value; }
		public readonly GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator T(DistributedTemporalValue<T,S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed fixed-size array. Supports both full replacement and per-index delta updates.
	/// Only changed indices are serialized when using <see cref="Set"/> for efficient bandwidth usage.
	/// </summary>
	/// <typeparam name="T">The element type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedArray<T, S> : IDistributedField, IReadOnlyList<T> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when the entire array is replaced, providing old and new arrays.</summary>
		public event Action<T[], T[]> OnReplaced;
		/// <summary>Raised when a single element changes, providing the index, old value, and new value.</summary>
		public event Action<int, T, T> OnChanged;
		private static readonly S Serializer = default!;

		T[] CurrentValue;
		T[]? NewValue;
		Dictionary<int, T> Changes;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		public readonly int Length => CurrentValue.Length;
		public readonly int Count => CurrentValue.Length;

		public readonly T this[int index] => Get(index);


		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Initializes the array with default values of the given size. Marks the field dirty for full sync.</summary>
		public void Init(int size)
		{
			NewValue = new T[size];
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				T[] oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		/// <summary>Replaces the entire array contents. Marks the field dirty for full sync.</summary>
		public void Replace(IReadOnlyCollection<T> newArray)
		{
			NewValue = new T[newArray.Count];

			int i = 0;
			foreach (T value in newArray)
			{
				NewValue[i++] = value;
			}

			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				T[] oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Returns the element at the given index, reflecting any pending local changes.</summary>
		public readonly T Get(int index)
		{
			if (Changes.TryGetValue(index, out T value))
			{
				return value;
			}
			return CurrentValue[index];
		}

		/// <summary>Sets a single element by index. Only the changed index is sent as a delta update.</summary>
		public bool Set(int index, T newValue, bool force = false)
		{
			if (NewValue != null)
			{
				if (!force && NewValue[index].Equals(newValue))
				{
					return false;
				}

				T oldValue = NewValue[index];
				NewValue[index] = newValue;

				Entity.SetDirty(FieldBitmask, true);

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

			Entity.SetDirty(FieldBitmask, true);

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
				for (int index = 0; index < NewValue.Length; index++)
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

		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
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

		/// <inheritdoc/>
		public void SkipFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					r.ReadUInt16(); // index
					Serializer.ReadFrom(r);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int arraySize = r.ReadUInt16();
				for (int index = 0; index < arraySize; index++)
				{
					Serializer.ReadFrom(r);
				}
			}
		}

		private readonly void InvokeOnChanged(int index, T oldValue, T newValue)
		{
			try
			{
				OnChanged?.Invoke(index, oldValue, newValue);
			}
			catch (Exception e)
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
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnReplaced handler method", e);
			}
		}

		public IEnumerator<T> GetEnumerator()
		{
			throw new NotImplementedException();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public bool Equals(T other)
		{
			if (CurrentValue == null)
			{
				return other == null;
			}
			return CurrentValue.Equals(other);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Array; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }
		public static implicit operator T[](DistributedArray<T, S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed bounded queue. Automatically evicts the oldest element when capacity is reached.
	/// Supports full replacement or incremental enqueue deltas.
	/// </summary>
	/// <typeparam name="T">The element type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedQueue<T,S> : IDistributedField, IReadOnlyCollection<T> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a new element is enqueued.</summary>
		public event Action<T> OnChanged;
		/// <summary>Raised when the entire queue is replaced, providing old and new queues.</summary>
		public event Action<Queue<T>, Queue<T>> OnReplaced;

		private static readonly S Serializer = default!;

		int CurrentCapacity;
		Queue<T> CurrentValue;

		int NewCapacity;
		Queue<T>? NewValue;
		Queue<T> Changes;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		public readonly int Count { get => CurrentValue.Count; }

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Initializes the queue with the given maximum capacity. Marks the field dirty for full sync.</summary>
		public void Init(int capacity)
		{
			NewCapacity = capacity;
			NewValue = new Queue<T>();
			Changes = new Queue<T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				CurrentCapacity = NewCapacity;
				Queue<T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		/// <summary>Replaces the queue with a new capacity and initial values. Marks the field dirty for full sync.</summary>
		public void Replace(int capacity, IEnumerable<T> initialValues)
		{
			NewCapacity = capacity;
			NewValue = new Queue<T>();
			Changes = new Queue<T>();
			
			foreach (T val in initialValues)
			{
				AddToNew(val);
			}

			Entity.SetDirty(FieldBitmask, true);

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
			if (NewValue!.Count == NewCapacity)
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


		/// <summary>Enqueues a value, evicting the oldest if at capacity. Sends as a delta update.</summary>
		public void Add(T newValue)
		{
			if (NewValue != null)
			{
				AddToNew(newValue);

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(newValue);
				}

				return;
			}

			AddToChanges(newValue);

			Entity.SetDirty(FieldBitmask, true);

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

		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
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
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
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

		/// <inheritdoc/>
		public void SkipFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					Serializer.ReadFrom(r);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				r.ReadUInt16(); // capacity
				int numValues = r.ReadUInt16();
				for (int index = 0; index < numValues; index++)
				{
					Serializer.ReadFrom(r);
				}
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

		public bool Equals(T other)
		{
			if (CurrentValue == null)
			{
				return other == null;
			}
			return CurrentValue.Equals(other);
		}

		public readonly IEnumerator<T> GetEnumerator()
		{
			return GetEnumerator();
		}

		readonly IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Queue; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Queue<T>(DistributedQueue<T,S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed dictionary with integer keys. Supports full replacement or per-key delta updates.
	/// </summary>
	/// <typeparam name="T">The value type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedIntDictionary<T,S> : IDistributedField, IReadOnlyDictionary<int,T?> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a single entry changes, providing key, old value, and new value.</summary>
		public event Action<int,T,T> OnChanged;
		/// <summary>Raised when the entire dictionary is replaced.</summary>
		public event Action<Dictionary<int,T>,Dictionary<int,T>> OnReplaced;

		private static readonly S Serializer = default!;

		Dictionary<int,T> CurrentValue;

		Dictionary<int, T>? NewValue;
		Dictionary<int, T> Changes;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		public readonly int Count { get => CurrentValue.Count; }
		public readonly IEnumerable<int> Keys => CurrentValue.Keys;

		public readonly IEnumerable<T> Values => CurrentValue.Values;

		public readonly T? this[int key] => Get(key);

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Initializes the dictionary as empty. Marks the field dirty for full sync.</summary>
		public void Init()
		{
			NewValue = new Dictionary<int, T>();
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				Dictionary<int,T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		/// <summary>Replaces the entire dictionary contents. Marks the field dirty for full sync.</summary>
		public void Replace(IReadOnlyDictionary<int,T> initialValues)
		{
			NewValue = new Dictionary<int, T>(initialValues);
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				Dictionary<int,T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Adds or updates an entry by key. Sends as a per-key delta update.</summary>
		public void Add(int key, T newValue)
		{
			if (NewValue != null)
			{
				T oldValue = NewValue.GetValueOrDefault(key);
				NewValue[key] = newValue;

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(key, oldValue, newValue);
				}

				return;
			}

			Changes[key] = newValue;

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue.GetValueOrDefault(key);
				CurrentValue[key] = newValue;

				InvokeOnChanged(key, oldValue, newValue);
			}

		}

		/// <summary>Returns the value for the given key, or default if not found or uninitialized.</summary>
		public readonly T? Get(int key)
		{
			if (CurrentValue == null)
			{
				return default;
			}
			return CurrentValue.GetValueOrDefault(key);
		}

		/// <inheritdoc/>
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

		/// <inheritdoc/>
		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
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

		/// <inheritdoc/>
		public void SkipFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					r.ReadInt32(); // key
					Serializer.ReadFrom(r);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int numValues = r.ReadUInt16();
				for (int index = 0; index < numValues; index++)
				{
					r.ReadInt32(); // key
					Serializer.ReadFrom(r);
				}
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

		public readonly bool ContainsKey(int key)
		{
			return CurrentValue.ContainsKey(key);
		}

		public readonly bool TryGetValue(int key, out T value)
		{
			return CurrentValue.TryGetValue(key, out value);
		}

		public readonly IEnumerator<KeyValuePair<int, T?>> GetEnumerator()
		{
			return CurrentValue.GetEnumerator();
		}

		readonly IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public bool Equals(T other)
		{
			if (CurrentValue == null)
			{
				return other == null;
			}
			return CurrentValue.Equals(other);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.IntDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }
		public static implicit operator Dictionary<int, T>(DistributedIntDictionary<T,S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed dictionary with string keys. Supports full replacement or per-key delta updates.
	/// </summary>
	/// <typeparam name="T">The value type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedStringDictionary<T,S> : IDistributedField, IReadOnlyDictionary<string,T?> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a single entry changes, providing key, old value, and new value.</summary>
		public event Action<string,T,T> OnChanged;
		/// <summary>Raised when the entire dictionary is replaced.</summary>
		public event Action<Dictionary<string,T>,Dictionary<string,T>> OnReplaced;

		private static readonly S Serializer = default!;

		Dictionary<string, T> CurrentValue;

		Dictionary<string, T>? NewValue;
		Dictionary<string, T> Changes;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		public readonly int Count { get => CurrentValue.Count; }
		public readonly IEnumerable<string> Keys => CurrentValue.Keys;

		public readonly IEnumerable<T> Values => CurrentValue.Values;

		public readonly T? this[string key] => Get(key);

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Initializes the dictionary as empty. Marks the field dirty for full sync.</summary>
		public void Init()
		{
			NewValue = new Dictionary<string, T>();
			Changes = new Dictionary<string, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				var oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		/// <summary>Replaces the entire dictionary contents. Marks the field dirty for full sync.</summary>
		public void Replace(IReadOnlyDictionary<string, T> initialValues)
		{
			NewValue = new Dictionary<string, T>(initialValues);
			Changes = new Dictionary<string, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				var oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Adds or updates an entry by key. Sends as a per-key delta update.</summary>
		public void Add(string key, T newValue)
		{
			if (NewValue != null)
			{
				T oldValue = NewValue.GetValueOrDefault(key);
				NewValue[key] = newValue;

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative)
				{
					InvokeOnChanged(key, oldValue, newValue);
				}

				return;
			}

			Changes[key] = newValue;

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative)
			{
				T oldValue = CurrentValue.GetValueOrDefault(key);
				CurrentValue[key] = newValue;

				InvokeOnChanged(key, oldValue, newValue);
			}

		}

		/// <summary>Returns the value for the given key, or default if not found or uninitialized.</summary>
		public readonly T? Get(string key)
		{
			if (CurrentValue == null)
			{
				return default(T);
			}
			return CurrentValue.GetValueOrDefault(key);
		}

		/// <inheritdoc/>
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

		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
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

		/// <inheritdoc/>
		public void SkipFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					r.ReadString(); // key
					Serializer.ReadFrom(r);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int numValues = r.ReadUInt16();
				for (int index = 0; index < numValues; index++)
				{
					r.ReadString(); // key
					Serializer.ReadFrom(r);
				}
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

		public readonly bool ContainsKey(string key)
		{
			return CurrentValue.ContainsKey(key);
		}

		public readonly bool TryGetValue(string key, out T value)
		{
			return CurrentValue.TryGetValue(key, out value);
		}

		public readonly IEnumerator<KeyValuePair<string, T?>> GetEnumerator()
		{
			return CurrentValue.GetEnumerator();
		}

		readonly IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		public bool Equals(T other)
		{
			if (CurrentValue == null)
			{
				return other == null;
			}
			return CurrentValue.Equals(other);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.StringDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Dictionary<string, T>(DistributedStringDictionary<T,S> d) => d.CurrentValue;
	}

}