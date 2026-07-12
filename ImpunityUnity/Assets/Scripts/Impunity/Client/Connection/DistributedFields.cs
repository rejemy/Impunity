using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UltraLiteDB;


namespace Impunity.Connection
{

	/// <summary>Base interface for all client-side distributed field types. Provides binary serialization for wire protocol and read/write lifecycle.</summary>
	public interface IDistributedField
	{
		/// <summary>Gets field value as a BsonValue</summary>
		BsonValue GetAsBsonValue();

		/// <summary>Sets field from a BsonValue</summary>
		void SetFromBsonValue(BsonValue val);

		/// <summary>Serializes this field's pending local changes to the wire and clears the pending state. Called when the entity manager flushes dirty fields.</summary>
		void WriteChangesTo(BinaryWriter w);
		/// <summary>Reads the field's full initial state from the stream, applied when the entity is first received.</summary>
		void ReadInitialFrom(BinaryReader r);
		/// <summary>Reads an incremental update from the stream and applies it to the current value.</summary>
		void ReadChangesFrom(BinaryReader r);
		/// <summary>Reads and discards field data from the stream, advancing the reader position without applying changes. Used to skip stale out-of-order updates.</summary>
		void SkipFrom(BinaryReader r);

		/// <summary>The wire-protocol category of this field (value, array, queue, dictionary).</summary>
		GameStateEntityFieldType FieldType { get; }
		/// <summary>The serialized element value type for this field.</summary>
		GameStateEntityPropertyValueType ValueType { get; }
	}

	/// <summary>Extended distributed field that tracks the server timestamp of the last modification. Used for time-sensitive interpolation or age-based logic.</summary>
	public interface IDistributedTemporalField : IDistributedField
	{
		/// <summary>Server time (Unix milliseconds) when this field was last modified.</summary>
		long LastModifiedTime { get; set; }
	}

	/// <summary>
	/// Client-side distributed single value. Tracks local changes and syncs them to the server via a dirty-bit mechanism.
	/// Reads (<see cref="DistributedValue{T, S}.Get"/>) always return the last server-confirmed value; a locally set value is
	/// not reflected until the server echoes it back. The exception is client-authoritative mode — when the entity is flagged
	/// client-authoritative or has no connected manager — where local changes are applied to the current value immediately.
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
		T? PendingValue;

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Returns the last server-confirmed value. Pending local changes made via <see cref="Set"/> are not reflected here unless the entity is client-authoritative.</summary>
		public readonly T Get()
		{
			return CurrentValue;
		}

		/// <summary>Queues the value to be sent to the server and marks the field dirty. Applied to the local current value immediately only when the entity is client-authoritative. Returns false if the value is unchanged (unless <paramref name="force"/> is true).</summary>
		public bool Set(T newValue, bool force = false)
		{
			if (!force && Equals(newValue, CurrentValue))
			{
				return false;
			}

			PendingValue = newValue;
			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				T oldValue = CurrentValue;
				CurrentValue = PendingValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		/// <summary>Same as <see cref="Set(T, bool)"/> but flushed as an unguaranteed (best-effort) update when possible.</summary>
		public bool SetUnguaranteed(T newValue, bool force = false)
		{
			if (!force && Equals(newValue, CurrentValue))
			{
				return false;
			}

			PendingValue = newValue;
			Entity.SetDirty(FieldBitmask, false);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				T oldValue = CurrentValue;
				CurrentValue = PendingValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		/// <summary>Updates the value locally without sending to the server. Useful for client-side prediction or cosmetic state.</summary>
		public bool SetLocalOnly(T newValue, bool force = false)
		{
			if (!force && Equals(newValue, CurrentValue))
			{
				return false;
			}

			T oldValue = CurrentValue;
			CurrentValue = newValue;

			InvokeOnChanged(oldValue, CurrentValue);

			return true;
		}

		/// <inheritdoc/>
		public void WriteChangesTo(BinaryWriter w)
		{
			// Only called if there's a pending value
			Serializer.WriteTo(PendingValue!, w);
			PendingValue = default;
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

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			return Serializer.ToBsonValue(CurrentValue);
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			Set(Serializer.FromBsonValue(val));
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
	public struct DistributedTemporalValue<T, S> : IDistributedTemporalField where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised on initial load with the current value and its age (time since last server modification).</summary>
		public event Action<T, TimeSpan> OnInitialized;
		/// <summary>Raised when the value changes, providing old and new values.</summary>
		public event Action<T, T> OnChanged;

		private static readonly S Serializer = default!;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		T CurrentValue;
		T? PendingValue;

		public object RawCurrentValue => CurrentValue;

		/// <inheritdoc/>
		public long LastModifiedTime { get; set; }

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Returns the last server-confirmed value. Pending local changes are not reflected here unless the entity is client-authoritative.</summary>
		public readonly T Get()
		{
			return CurrentValue;
		}

		/// <summary>Queues the value to be sent to the server and marks the field dirty.
		/// Applied to the local current value immediately only when the entity is client-authoritative.
		/// Returns false if the value is unchanged (unless <paramref name="force"/> is true).</summary>
		public bool Set(T newValue, bool force = false)
		{
			if (!force && Equals(newValue, CurrentValue))
			{
				return false;
			}

			PendingValue = newValue;
			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				LastModifiedTime = Entity.Manager?.Connection?.GetServerTime() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

				T oldValue = CurrentValue;
				CurrentValue = PendingValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		/// <summary>Queues the value to be sent to the server unguaranteed and marks the field dirty.
		/// Applied to the local current value immediately only when the entity is client-authoritative.
		/// Returns false if the value is unchanged (unless <paramref name="force"/> is true).</summary>
		public bool SetUnguaranteed(T newValue, bool force = false)
		{
			if (!force && Equals(newValue, CurrentValue))
			{
				return false;
			}

			PendingValue = newValue;
			Entity.SetDirty(FieldBitmask, false);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				LastModifiedTime = Entity.Manager?.Connection?.GetServerTime() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

				T oldValue = CurrentValue;
				CurrentValue = PendingValue;

				InvokeOnChanged(oldValue, CurrentValue);
			}

			return true;
		}

		/// <summary>Updates the value locally without sending to the server, refreshing <see cref="LastModifiedTime"/>. Useful for client-side prediction or cosmetic state.</summary>
		public bool SetLocalOnly(T newValue)
		{
			LastModifiedTime = Entity.Manager?.Connection?.GetServerTime() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			T oldValue = CurrentValue;
			CurrentValue = newValue;

			InvokeOnChanged(oldValue, CurrentValue);

			return true;
		}

		/// <inheritdoc/>
		public void WriteChangesTo(BinaryWriter w)
		{
			Serializer.WriteTo(PendingValue!, w);
			PendingValue = default;
		}

		/// <inheritdoc/>
		public void ReadInitialFrom(BinaryReader r)
		{
			long now = Entity.Manager?.Connection?.GetServerTime() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			long ageMilliseconds = r.ReadUInt32();
			LastModifiedTime = now - ageMilliseconds;

			CurrentValue = Serializer.ReadFrom(r);

			InvokeOnInitialized(CurrentValue, TimeSpan.FromMilliseconds(ageMilliseconds));
		}

		/// <inheritdoc/>
		public void ReadChangesFrom(BinaryReader r)
		{
			LastModifiedTime = Entity.Manager?.Connection?.GetServerTime() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
			catch (Exception e)
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

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			return Serializer.ToBsonValue(CurrentValue);
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			Set(Serializer.FromBsonValue(val));
		}

		public readonly GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Value; }
		public readonly GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator T(DistributedTemporalValue<T, S> d) => d.CurrentValue;
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

		public object RawCurrentValue => CurrentValue;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		/// <summary>The length of the array, or 0 if it has not been initialized.</summary>
		public readonly int Length => CurrentValue?.Length ?? 0;
		/// <summary>The number of elements in the array, or 0 if it has not been initialized. Alias for <see cref="Length"/>.</summary>
		public readonly int Count => CurrentValue?.Length ?? 0;

		/// <summary>Gets the last server-confirmed element at the given index. See <see cref="Get"/>.</summary>
		public readonly T this[int index] => Get(index);


		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>True once the array has been initialized via <see cref="Init"/>, <see cref="Replace"/>, or an initial server load.</summary>
		public bool HasValue => CurrentValue != null;

		/// <summary>Initializes the array with default values of the given size. Marks the field dirty for full sync.</summary>
		public void Init(int size)
		{
			NewValue = new T[size];
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				T[] oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Returns the last server-confirmed element at the given index. Pending local changes are not reflected unless the entity is client-authoritative. Throws if the array has not been initialized.</summary>
		public readonly T Get(int index)
		{
			if (CurrentValue == null)
			{
				throw new Exception("Array must be initialized with a call to Init or Replace before getting a value");
			}
			return CurrentValue[index];
		}

		/// <summary>Sets a single element by index, marking the field dirty. Only the changed index is sent as a delta update. Applied to the local current value immediately only when the entity is client-authoritative.</summary>
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

				if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
				{
					// If IsClientAuthoritative, NewValue is an alias to CurrentValue so it's already set
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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				T oldValue = CurrentValue[index];
				CurrentValue[index] = newValue;

				InvokeOnChanged(index, oldValue, newValue);
			}

			return true;
		}

		/// <inheritdoc/>
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

		/// <inheritdoc/>
		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
		}


		/// <inheritdoc/>
		public void ReadChangesFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					int index = r.ReadUInt16();
					T newValue = Serializer.ReadFrom(r);

					// Always consume the value above so the stream stays aligned, but ignore an
					// out-of-range index rather than throwing on malformed/relayed hostile data.
					if (CurrentValue == null || index >= CurrentValue.Length)
					{
						continue;
					}

					T oldValue = CurrentValue[index];
					CurrentValue[index] = newValue;

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
			int count = Count;
			for (int i = 0; i < count; i++)
			{
				yield return Get(i);
			}
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

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			if (CurrentValue == null)
			{
				return BsonValue.Null;
			}

			BsonArray array = new BsonArray();
			foreach (var val in CurrentValue)
			{
				array.Add(Serializer.ToBsonValue(val));
			}

			return array;
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			if (val.IsNull || !val.IsArray)
			{
				return;
			}
			BsonArray source = val.AsArray!;
			List<T> list = new List<T>(source.Count);

			foreach (var item in source)
			{
				list.Add(Serializer.FromBsonValue(item));
			}
			Replace(list);
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
	public struct DistributedQueue<T, S> : IDistributedField, IReadOnlyCollection<T> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a new element is enqueued.</summary>
		public event Action<T> OnChanged;
		/// <summary>Raised when the entire queue is replaced, providing old and new queues.</summary>
		public event Action<Queue<T>, Queue<T>> OnReplaced;

		private static readonly S Serializer = default!;

		int CurrentCapacity;
		Queue<T> CurrentValue;

		public object RawCurrentValue => CurrentValue;

		int NewCapacity;
		Queue<T>? NewValue;

		Queue<T> Changes;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		/// <summary>The number of elements currently in the queue, or 0 if it has not been initialized.</summary>
		public readonly int Count { get => CurrentValue?.Count ?? 0; }

		/// <summary>True once the queue has been initialized via <see cref="Init"/>, <see cref="Replace"/>, or an initial server load.</summary>
		public bool HasValue => CurrentValue != null;

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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				CurrentCapacity = NewCapacity;
				Queue<T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		private void AddToCurrent(T value)
		{
			if (CurrentValue.Count == CurrentCapacity)
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


		/// <summary>Enqueues a value, evicting the oldest if at capacity, and marks the field dirty. Sent as a delta update. Applied to the local current value immediately only when the entity is client-authoritative.</summary>
		public void Add(T newValue)
		{
			if (NewValue != null)
			{
				AddToNew(newValue);

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
				{
					// If IsClientAuthoritative, NewValue is an alias to CurrentValue so it's already set
					InvokeOnChanged(newValue);
				}

				return;
			}

			AddToChanges(newValue);

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				AddToCurrent(newValue);

				InvokeOnChanged(newValue);
			}
		}

		/// <inheritdoc/>
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

		/// <inheritdoc/>
		public void ReadInitialFrom(BinaryReader r)
		{
			ReadChangesFrom(r);
		}

		/// <inheritdoc/>
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

				Queue<T> oldValue = CurrentValue;
				CurrentValue = new Queue<T>(numValues);

				for (int index = 0; index < numValues; index++)
				{
					T val = Serializer.ReadFrom(r);
					if (CurrentValue.Count == CurrentCapacity)
					{
						CurrentValue.Dequeue();
					}
					CurrentValue.Enqueue(val);
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
			catch (Exception e)
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
			catch (Exception e)
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
			return CurrentValue.GetEnumerator();
		}

		readonly IEnumerator IEnumerable.GetEnumerator()
		{
			return CurrentValue.GetEnumerator();
		}

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			if (CurrentValue == null)
			{
				return BsonValue.Null;
			}
			BsonDocument doc = new BsonDocument();
			doc["Capacity"] = CurrentCapacity;

			BsonArray array = new BsonArray();
			foreach (var val in CurrentValue)
			{
				array.Add(Serializer.ToBsonValue(val));
			}
			doc["Queue"] = array;

			return doc;
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			if (val.IsNull || !val.IsDocument)
			{
				return;
			}
			BsonDocument doc = val.AsDocument!;
			int capacity = doc["Capacity"].AsInt32;
			BsonArray source = doc["Queue"].AsArray!;

			List<T> list = new List<T>(source.Count);

			foreach (var item in source)
			{
				list.Add(Serializer.FromBsonValue(item));
			}

			Replace(capacity, list);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Queue; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Queue<T>(DistributedQueue<T, S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed stack (LIFO). Starts life as an empty stack — unlike the other collections, no
	/// Init call is required before use. Supports full replacement or incremental push/pop/set-top deltas.
	/// Enumeration yields values top-to-bottom, matching <see cref="Stack{T}"/>.
	/// </summary>
	/// <typeparam name="T">The element type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedStack<T, S> : IDistributedField, IReadOnlyCollection<T> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a value is pushed onto the stack, including a <see cref="SetTop"/> on an empty stack.</summary>
		public event Action<T> OnPushed;
		/// <summary>Raised when the top value is popped, providing the removed value.</summary>
		public event Action<T> OnPopped;
		/// <summary>Raised when the top value is replaced via <see cref="SetTop"/>, providing old and new top values.</summary>
		public event Action<T, T> OnTopChanged;
		/// <summary>Raised when the entire stack is replaced or cleared, providing old and new backing lists (bottom-to-top order; the old list is null if the stack was never populated).</summary>
		public event Action<List<T>, List<T>> OnReplaced;

		private static readonly S Serializer = default!;

		private struct StackChange
		{
			public DistributedStackUpdateType Op;
			public T Value;
		}

		// Backing lists hold the stack bottom-to-top: the last element is the top.
		List<T> CurrentValue;

		List<T>? NewValue;
		List<StackChange> Changes;

		public object RawCurrentValue => CurrentValue;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		/// <summary>The number of values currently on the stack.</summary>
		public readonly int Count { get => CurrentValue?.Count ?? 0; }

		public void _imp_Initialize(IDistributedEntity entity, byte fieldId)
		{
			Entity = entity;
			FieldBitmask = 1ul << (fieldId - 1);
		}

		/// <summary>Resets the stack back to empty. Marks the field dirty for full sync.</summary>
		public void Clear()
		{
			ReplaceInternal(new List<T>());
		}

		/// <summary>Replaces the entire stack contents. Values are pushed in enumeration order, so the last
		/// value becomes the top of the stack. Marks the field dirty for full sync.</summary>
		public void Replace(IEnumerable<T> newValues)
		{
			ReplaceInternal(new List<T>(newValues));
		}

		private void ReplaceInternal(List<T> newValue)
		{
			NewValue = newValue;
			Changes = new List<StackChange>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				List<T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Pushes a value onto the top of the stack and marks the field dirty. Sent as a delta update. Applied to the local current value immediately only when the entity is client-authoritative.</summary>
		public void Push(T newValue)
		{
			if (NewValue != null)
			{
				NewValue.Add(newValue);

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
				{
					// If IsClientAuthoritative, NewValue is an alias to CurrentValue so it's already set
					InvokeOnPushed(newValue);
				}

				return;
			}

			AddChange(new StackChange { Op = DistributedStackUpdateType.Push, Value = newValue });

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				ApplyPushToCurrent(newValue);
			}
		}

		/// <summary>Removes the top value and marks the field dirty. Sent as a delta update. Applied to the
		/// local current value immediately only when the entity is client-authoritative. A pop applied to an
		/// empty stack is ignored by every applier, so a pop racing another client's pop stays safe.</summary>
		public void Pop()
		{
			if (NewValue != null)
			{
				// A pending full replacement that is already empty has nothing to pop.
				if (NewValue.Count == 0)
				{
					return;
				}

				T oldTop = NewValue[NewValue.Count - 1];
				NewValue.RemoveAt(NewValue.Count - 1);

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
				{
					InvokeOnPopped(oldTop);
				}

				return;
			}

			AddChange(new StackChange { Op = DistributedStackUpdateType.Pop });

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				ApplyPopToCurrent();
			}
		}

		/// <summary>Replaces the top value, or pushes it if the stack is empty, and marks the field dirty. Sent as a delta update. Applied to the local current value immediately only when the entity is client-authoritative.</summary>
		public void SetTop(T newValue)
		{
			if (NewValue != null)
			{
				Entity.SetDirty(FieldBitmask, true);

				if (NewValue.Count == 0)
				{
					NewValue.Add(newValue);

					if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
					{
						InvokeOnPushed(newValue);
					}
				}
				else
				{
					T oldTop = NewValue[NewValue.Count - 1];
					NewValue[NewValue.Count - 1] = newValue;

					if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
					{
						InvokeOnTopChanged(oldTop, newValue);
					}
				}

				return;
			}

			AddChange(new StackChange { Op = DistributedStackUpdateType.SetTop, Value = newValue });

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				ApplySetTopToCurrent(newValue);
			}
		}

		/// <summary>Returns the last server-confirmed top value. Pending local changes are not reflected unless the entity is client-authoritative. Throws if the stack is empty.</summary>
		public readonly T Peek()
		{
			if (CurrentValue == null || CurrentValue.Count == 0)
			{
				throw new InvalidOperationException("Stack is empty");
			}

			return CurrentValue[CurrentValue.Count - 1];
		}

		/// <summary>Gets the last server-confirmed top value, returning false if the stack is empty.</summary>
		public readonly bool TryPeek(out T value)
		{
			if (CurrentValue == null || CurrentValue.Count == 0)
			{
				value = default!;
				return false;
			}

			value = CurrentValue[CurrentValue.Count - 1];
			return true;
		}

		private void AddChange(StackChange change)
		{
			// Unlike the other collections there is no Init to create the pending-change list, so it's made lazily.
			if (Changes == null)
			{
				Changes = new List<StackChange>();
			}

			Changes.Add(change);
		}

		private void ApplyPushToCurrent(T value)
		{
			if (CurrentValue == null)
			{
				CurrentValue = new List<T>();
			}

			CurrentValue.Add(value);

			InvokeOnPushed(value);
		}

		private void ApplyPopToCurrent()
		{
			if (CurrentValue == null || CurrentValue.Count == 0)
			{
				return;
			}

			T oldTop = CurrentValue[CurrentValue.Count - 1];
			CurrentValue.RemoveAt(CurrentValue.Count - 1);

			InvokeOnPopped(oldTop);
		}

		private void ApplySetTopToCurrent(T value)
		{
			if (CurrentValue == null)
			{
				CurrentValue = new List<T>();
			}

			if (CurrentValue.Count == 0)
			{
				CurrentValue.Add(value);

				InvokeOnPushed(value);
			}
			else
			{
				T oldTop = CurrentValue[CurrentValue.Count - 1];
				CurrentValue[CurrentValue.Count - 1] = value;

				InvokeOnTopChanged(oldTop, value);
			}
		}

		/// <inheritdoc/>
		public void WriteChangesTo(BinaryWriter w)
		{
			if (NewValue != null)
			{
				// Resend entire stack, bottom to top
				w.Write((byte)DistributedCollectionUpdateType.Set);
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
				foreach (StackChange change in Changes)
				{
					w.Write((byte)change.Op);
					if (change.Op != DistributedStackUpdateType.Pop)
					{
						Serializer.WriteTo(change.Value, w);
					}
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

		/// <inheritdoc/>
		public void ReadChangesFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					DistributedStackUpdateType op = (DistributedStackUpdateType)r.ReadByte();
					switch (op)
					{
						case DistributedStackUpdateType.Push:
							ApplyPushToCurrent(Serializer.ReadFrom(r));
							break;
						case DistributedStackUpdateType.Pop:
							ApplyPopToCurrent();
							break;
						case DistributedStackUpdateType.SetTop:
							ApplySetTopToCurrent(Serializer.ReadFrom(r));
							break;
						default:
							// An unknown op leaves the rest of the stream unparseable; the server rejects
							// these before relaying, so this only fires on a corrupt stream.
							throw new Exception("Unknown stack update op: " + (byte)op);
					}
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				Changes = new List<StackChange>();

				int numValues = r.ReadUInt16();
				List<T> newValue = new List<T>(numValues);

				for (int index = 0; index < numValues; index++)
				{
					newValue.Add(Serializer.ReadFrom(r));
				}

				List<T> oldValue = CurrentValue;
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
					DistributedStackUpdateType op = (DistributedStackUpdateType)r.ReadByte();
					if (op != DistributedStackUpdateType.Pop)
					{
						Serializer.ReadFrom(r);
					}
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int numValues = r.ReadUInt16();
				for (int index = 0; index < numValues; index++)
				{
					Serializer.ReadFrom(r);
				}
			}
		}

		private readonly void InvokeOnPushed(T newValue)
		{
			try
			{
				OnPushed?.Invoke(newValue);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnPushed handler method", e);
			}
		}

		private readonly void InvokeOnPopped(T oldValue)
		{
			try
			{
				OnPopped?.Invoke(oldValue);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnPopped handler method", e);
			}
		}

		private readonly void InvokeOnTopChanged(T oldValue, T newValue)
		{
			try
			{
				OnTopChanged?.Invoke(oldValue, newValue);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnTopChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(List<T> oldValue, List<T> newValue)
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

		/// <summary>Enumerates the last server-confirmed stack from top to bottom.</summary>
		public readonly IEnumerator<T> GetEnumerator()
		{
			if (CurrentValue == null)
			{
				yield break;
			}

			for (int i = CurrentValue.Count - 1; i >= 0; i--)
			{
				yield return CurrentValue[i];
			}
		}

		readonly IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			if (CurrentValue == null)
			{
				return BsonValue.Null;
			}

			BsonArray array = new BsonArray();
			foreach (var val in CurrentValue)
			{
				array.Add(Serializer.ToBsonValue(val));
			}

			return array;
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			if (val.IsNull || !val.IsArray)
			{
				return;
			}
			BsonArray source = val.AsArray!;
			List<T> list = new List<T>(source.Count);

			foreach (var item in source)
			{
				list.Add(Serializer.FromBsonValue(item));
			}

			ReplaceInternal(list);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.Stack; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator List<T>(DistributedStack<T, S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed dictionary with integer keys. Supports full replacement or per-key delta updates.
	/// </summary>
	/// <typeparam name="T">The value type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedIntDictionary<T, S> : IDistributedField, IReadOnlyDictionary<int, T?> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a single entry changes, providing key, old value, and new value.</summary>
		public event Action<int, T, T> OnChanged;
		/// <summary>Raised when the entire dictionary is replaced.</summary>
		public event Action<Dictionary<int, T>, Dictionary<int, T>> OnReplaced;

		private static readonly S Serializer = default!;

		Dictionary<int, T> CurrentValue;

		Dictionary<int, T>? NewValue;
		Dictionary<int, T> Changes;

		public object RawCurrentValue => CurrentValue;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		/// <summary>The number of entries currently in the dictionary, or 0 if it has not been initialized.</summary>
		public readonly int Count { get => CurrentValue?.Count ?? 0; }
		/// <summary>The keys in the last server-confirmed dictionary.</summary>
		public readonly IEnumerable<int> Keys => CurrentValue.Keys;

		/// <summary>The values in the last server-confirmed dictionary.</summary>
		public readonly IEnumerable<T> Values => CurrentValue.Values;

		/// <summary>Gets the last server-confirmed value for the given key. See <see cref="Get"/>.</summary>
		public readonly T this[int key] => Get(key);

		/// <summary>True once the dictionary has been initialized via <see cref="Init"/>, <see cref="Replace"/>, or an initial server load.</summary>
		public bool HasValue => CurrentValue != null;

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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				Dictionary<int, T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}


		/// <summary>Replaces the entire dictionary contents. Marks the field dirty for full sync.</summary>
		public void Replace(IReadOnlyDictionary<int, T> initialValues)
		{
			NewValue = new Dictionary<int, T>(initialValues);
			Changes = new Dictionary<int, T>();

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				Dictionary<int, T> oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Adds or updates an entry by key, marking the field dirty. Sent as a per-key delta update. Applied to the local current value immediately only when the entity is client-authoritative.</summary>
		public void Add(int key, T newValue)
		{
			if (NewValue != null)
			{
				T oldValue = NewValue.GetValueOrDefault(key);
				NewValue[key] = newValue;

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
				{
					InvokeOnChanged(key, oldValue, newValue);
				}

				return;
			}

			Changes[key] = newValue;

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				T oldValue = CurrentValue.GetValueOrDefault(key);
				CurrentValue[key] = newValue;

				InvokeOnChanged(key, oldValue, newValue);
			}

		}

		/// <summary>Returns the last server-confirmed value for the given key, or default if the key is not present. Pending local changes are not reflected unless the entity is client-authoritative. Throws if the dictionary has not been initialized.</summary>
		public readonly T Get(int key)
		{
			if (CurrentValue == null)
			{
				throw new Exception("Dictionary must be initialized with a call to Init or Replace before getting a value");
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

		/// <inheritdoc/>
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
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(Dictionary<int, T> oldValue, Dictionary<int, T> newValue)
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

		/// <summary>Returns true if the last server-confirmed dictionary contains the given key.</summary>
		public readonly bool ContainsKey(int key)
		{
			return CurrentValue.ContainsKey(key);
		}

		/// <summary>Gets the last server-confirmed value for the given key, returning false if the key is not present.</summary>
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

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			if (CurrentValue == null)
			{
				return BsonValue.Null;
			}

			BsonDocument dict = new BsonDocument();
			foreach (var val in CurrentValue)
			{
				dict[val.Key.ToString()] = Serializer.ToBsonValue(val.Value);
			}

			return dict;
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			if (val.IsNull || !val.IsDocument)
			{
				return;
			}
			BsonDocument source = val.AsDocument!;
			Dictionary<int, T> dict = new Dictionary<int, T>();

			foreach (var item in source)
			{
				dict[int.Parse(item.Key)] = Serializer.FromBsonValue(item.Value);
			}

			Replace(dict);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.IntDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }
		public static implicit operator Dictionary<int, T>(DistributedIntDictionary<T, S> d) => d.CurrentValue;
	}

	/// <summary>
	/// Client-side distributed dictionary with string keys. Supports full replacement or per-key delta updates.
	/// </summary>
	/// <typeparam name="T">The value type.</typeparam>
	/// <typeparam name="S">The serializer struct.</typeparam>
	public struct DistributedStringDictionary<T, S> : IDistributedField, IReadOnlyDictionary<string, T?> where T : IEquatable<T> where S : IDistributableValueSerializer<T>
	{
		/// <summary>Raised when a single entry changes, providing key, old value, and new value.</summary>
		public event Action<string, T, T> OnChanged;
		/// <summary>Raised when the entire dictionary is replaced.</summary>
		public event Action<Dictionary<string, T>, Dictionary<string, T>> OnReplaced;

		private static readonly S Serializer = default!;

		Dictionary<string, T> CurrentValue;

		Dictionary<string, T>? NewValue;
		Dictionary<string, T> Changes;

		public object RawCurrentValue => CurrentValue;

		IDistributedEntity Entity;
		ulong FieldBitmask;

		/// <summary>The number of entries currently in the dictionary, or 0 if it has not been initialized.</summary>
		public readonly int Count { get => CurrentValue?.Count ?? 0; }
		/// <summary>The keys in the last server-confirmed dictionary.</summary>
		public readonly IEnumerable<string> Keys => CurrentValue.Keys;

		/// <summary>The values in the last server-confirmed dictionary.</summary>
		public readonly IEnumerable<T> Values => CurrentValue.Values;

		/// <summary>Gets the last server-confirmed value for the given key. See <see cref="Get"/>.</summary>
		public readonly T this[string key] => Get(key);

		/// <summary>True once the dictionary has been initialized via <see cref="Init"/>, <see cref="Replace"/>, or an initial server load.</summary>
		public bool HasValue => CurrentValue != null;

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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
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

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				var oldValue = CurrentValue;
				CurrentValue = NewValue;

				InvokeOnReplaced(oldValue, CurrentValue);
			}
		}

		/// <summary>Adds or updates an entry by key, marking the field dirty. Sent as a per-key delta update. Applied to the local current value immediately only when the entity is client-authoritative.</summary>
		public void Add(string key, T newValue)
		{
			if (NewValue != null)
			{
				T oldValue = NewValue.GetValueOrDefault(key);
				NewValue[key] = newValue;

				Entity.SetDirty(FieldBitmask, true);

				if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
				{
					InvokeOnChanged(key, oldValue, newValue);
				}

				return;
			}

			Changes[key] = newValue;

			Entity.SetDirty(FieldBitmask, true);

			if (Entity.IsClientAuthoritative || Entity.Manager?.Connection == null)
			{
				T oldValue = CurrentValue.GetValueOrDefault(key);
				CurrentValue[key] = newValue;

				InvokeOnChanged(key, oldValue, newValue);
			}

		}

		/// <summary>Returns the last server-confirmed value for the given key, or default if the key is not present. Pending local changes are not reflected unless the entity is client-authoritative. Throws if the dictionary has not been initialized.</summary>
		public readonly T Get(string key)
		{
			if (CurrentValue == null)
			{
				throw new Exception("Dictionary must be initialized with a call to Init or Replace before getting a value");
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

		/// <inheritdoc/>
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
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in OnChanged handler method", e);
			}
		}

		private readonly void InvokeOnReplaced(Dictionary<string, T> oldValue, Dictionary<string, T> newValue)
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

		/// <summary>Returns true if the last server-confirmed dictionary contains the given key.</summary>
		public readonly bool ContainsKey(string key)
		{
			return CurrentValue.ContainsKey(key);
		}

		/// <summary>Gets the last server-confirmed value for the given key, returning false if the key is not present.</summary>
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

		/// <summary>Gets field value as a BsonValue</summary>
		public BsonValue GetAsBsonValue()
		{
			if (CurrentValue == null)
			{
				return BsonValue.Null;
			}

			BsonDocument dict = new BsonDocument();
			foreach (var val in CurrentValue)
			{
				dict[val.Key] = Serializer.ToBsonValue(val.Value);
			}

			return dict;
		}

		/// <summary>Sets field from a BsonValue</summary>
		public void SetFromBsonValue(BsonValue val)
		{
			if (val.IsNull || !val.IsDocument)
			{
				return;
			}
			BsonDocument source = val.AsDocument!;
			Dictionary<string, T> dict = new Dictionary<string, T>();

			foreach (var item in source)
			{
				dict[item.Key] = Serializer.FromBsonValue(item.Value);
			}

			Replace(dict);
		}

		public GameStateEntityFieldType FieldType { get => GameStateEntityFieldType.StringDictionary; }
		public GameStateEntityPropertyValueType ValueType { get => Serializer.ValueType; }

		public static implicit operator Dictionary<string, T>(DistributedStringDictionary<T, S> d) => d.CurrentValue;
	}

}
