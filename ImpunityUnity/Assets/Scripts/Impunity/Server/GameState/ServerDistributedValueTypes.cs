
using System;
using System.IO;
using System.Collections.Generic;

namespace Impunity.GameState
{

	public struct DSmallCustom : IDistributableValueType
	{
		private byte[] Value;

		public DSmallCustom(byte[] v)
		{
			Value = v;
		}

		public void WriteTo(BinaryWriter w)
		{
			// For non-nullable custom, null actually means "uninitialized default value,
			// whatever that is for that type. Send 0 length indicates default.
			if (Value == null)
            {
				w.Write((byte)0);
				return;
			}
			w.Write((byte)Value.Length);
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			int count = r.ReadByte();
			Value = r.ReadBytes(count);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator byte[](DSmallCustom d) => d.Value;
		public static implicit operator DSmallCustom(byte[] d) => new DSmallCustom(d);
		public bool Equals(DSmallCustom v) => Array.Equals(Value, v.Value);
	}

	public struct DSmallNullableCustom : IDistributableValueType
	{
		private byte[] Value;

		public DSmallNullableCustom(byte[] v)
		{
			Value = v;
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value == null)
			{
				w.Write(false);
			}
			else
			{
				w.Write(true);
				w.Write((byte)Value.Length);
				w.Write(Value);
			}
		}

		public void ReadFrom(BinaryReader r)
		{
			bool hasValue = r.ReadBoolean();
			if (hasValue)
			{
				int count = r.ReadByte();
				Value = r.ReadBytes(count);
			}
			else
			{
				Value = null;
			}
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmallNullable; }

		public static implicit operator byte[](DSmallNullableCustom d) => d.Value;
		public static implicit operator DSmallNullableCustom(byte[] d) => new DSmallNullableCustom(d);
		public bool Equals(DSmallNullableCustom v) => Array.Equals(Value, v.Value);
	}

	public struct DCustom : IDistributableValueType
	{
		private byte[] Value;

		public DCustom(byte[] v)
		{
			Value = v;
		}

		public void WriteTo(BinaryWriter w)
		{
			// For non-nullable custom, null actually means "uninitialized default value,
			// whatever that is for that type. Send 0 length indicates default.
			if (Value == null)
			{
				w.Write((ushort)0);
				return;
			}
			w.Write((ushort)Value.Length);
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			int count = r.ReadUInt16();
			Value = r.ReadBytes(count);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Custom; }

		public static implicit operator byte[](DCustom d) => d.Value;
		public static implicit operator DCustom(byte[] d) => new DCustom(d);
		public bool Equals(DCustom v) => Array.Equals(Value, v.Value);
	}

	public struct DNullableCustom : IDistributableValueType
	{
		private byte[] Value;

		public DNullableCustom(byte[] v)
		{
			Value = v;
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value == null)
			{
				w.Write(false);
			}
			else
			{
				w.Write(true);
				w.Write((ushort)Value.Length);
				w.Write(Value);
			}
		}

		public void ReadFrom(BinaryReader r)
		{
			bool hasValue = r.ReadBoolean();
			if (hasValue)
			{
				int count = r.ReadUInt16();
				Value = r.ReadBytes(count);
			}
			else
			{
				Value = null;
			}
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomNullable; }

		public static implicit operator byte[](DNullableCustom d) => d.Value;
		public static implicit operator DNullableCustom(byte[] d) => new DNullableCustom(d);
		public bool Equals(DNullableCustom v) => Array.Equals(Value, v.Value);
	}

	public class ServerDistributedArray<T> : IDistributableValueType where T : struct, IDistributableValueType
	{
		public GameStateEntityPropertyValueType ValueType => new T().ValueType;

		private T[] Value;

		public void ReadFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					int index = r.ReadUInt16();
					T oldValue = Value[index];
					Value[index].ReadFrom(r);
					T newValue = Value[index];
				}
			}
			else if(updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int arraySize = r.ReadUInt16();
				if (Value == null || Value.Length != arraySize)
				{
					Value = new T[arraySize];
				}

				for (int index = 0; index < Value.Length; index++)
				{
					Value[index].ReadFrom(r);
				}
			}
			else
			{
				Value = null;
			}
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value == null)
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)Value.Length);
				for (int index = 0; index < Value.Length; index++)
				{
					Value[index].WriteTo(w);
				}
			}
		}
	}

	public class ServerDistributedQueue<T> : IDistributableValueType where T : struct, IDistributableValueType
	{
		public GameStateEntityPropertyValueType ValueType => new T().ValueType;

		private int Capacity;
		private Queue<T> Value;

		private void AddValue(T value)
		{
			if (Value.Count == Capacity)
			{
				Value.Dequeue();
			}

			Value.Enqueue(value);
		}

		public void ReadFrom(BinaryReader r)
		{
			byte updateType = r.ReadByte();
			if (updateType == (byte)DistributedCollectionUpdateType.Update)
			{
				int numChanges = r.ReadUInt16();
				for (int i = 0; i < numChanges; i++)
				{
					T val = default(T);
					val.ReadFrom(r);
					AddValue(val);
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				Capacity = r.ReadUInt16();
				int numValues = r.ReadUInt16();
				if (Value == null)
				{
					Value = new Queue<T>();
				}
				else
				{
					Value.Clear();
				}

				for (int index = 0; index < numValues; index++)
				{
					T val = default(T);
					val.ReadFrom(r);
					AddValue(val);
				}
			}
			else
			{
				Capacity = 0;
				Value = null;
			}
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value == null)
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)Capacity);
				w.Write((ushort)Value.Count);
				foreach (T value in Value)
				{
					value.WriteTo(w);
				}
			}
		}
	}

	public class ServerDistributedIntDictionary<T> : IDistributableValueType where T : struct, IDistributableValueType
	{
		public GameStateEntityPropertyValueType ValueType => new T().ValueType;

		private Dictionary<int,T> Value;

		public void ReadFrom(BinaryReader r)
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

					Value[key] = val;
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int numValues = r.ReadUInt16();

				if (Value == null)
				{
					Value = new Dictionary<int, T>();
				}
				else
				{
					Value.Clear();
				}

				for (int index = 0; index < numValues; index++)
				{
					int key = r.ReadInt32();
					T val = default(T);
					val.ReadFrom(r);
					Value[key] = val;
				}
			}
			else
			{
				Value = null;
			}
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value == null)
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)Value.Count);
				foreach (var pair in Value)
				{
					w.Write(pair.Key);
					pair.Value.WriteTo(w);
				}
			}
		}
	}

	public class ServerDistributedStringDictionary<T> : IDistributableValueType where T : struct, IDistributableValueType
	{
		public GameStateEntityPropertyValueType ValueType => new T().ValueType;

		private Dictionary<string, T> Value;

		public void ReadFrom(BinaryReader r)
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

					Value[key] = val;
				}
			}
			else if (updateType == (byte)DistributedCollectionUpdateType.Set)
			{
				int numValues = r.ReadUInt16();
				Dictionary<string, T> newValue = new Dictionary<string, T>();

				for (int index = 0; index < numValues; index++)
				{
					string key = r.ReadString();
					T val = default(T);
					val.ReadFrom(r);
					newValue[key] = val;
				}

				Value = newValue;
			}
			else
			{
				Value = null;
			}
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value == null)
			{
				w.Write((byte)DistributedCollectionUpdateType.None);
			}
			else
			{
				w.Write((byte)DistributedCollectionUpdateType.Set);
				w.Write((ushort)Value.Count);
				foreach (var pair in Value)
				{
					w.Write(pair.Key);
					pair.Value.WriteTo(w);
				}
			}
		}
	}

	public class DistributedValueFactory
    {
		
		public static Type GetDistributableType(GameStateEntityPropertyValueType type)
		{
			switch (type)
			{
				case GameStateEntityPropertyValueType.Boolean:
					return typeof(DBool);
				case GameStateEntityPropertyValueType.Int8:
					return typeof(DInt8);
				case GameStateEntityPropertyValueType.UInt8:
					return typeof(DUInt8);
				case GameStateEntityPropertyValueType.Int16:
					return typeof(DInt16);
				case GameStateEntityPropertyValueType.UInt16:
					return typeof(DUInt16);
				case GameStateEntityPropertyValueType.Int32:
					return typeof(DInt32);
				case GameStateEntityPropertyValueType.UInt32:
					return typeof(DUInt32);
				case GameStateEntityPropertyValueType.Int64:
					return typeof(DInt64);
				case GameStateEntityPropertyValueType.UInt64:
					return typeof(DUInt64);
				case GameStateEntityPropertyValueType.Float:
					return typeof(DFloat);
				case GameStateEntityPropertyValueType.Double:
					return typeof(DDouble);
				case GameStateEntityPropertyValueType.Decimal:
					return typeof(DDecimal);
				case GameStateEntityPropertyValueType.Char:
					return typeof(DChar);
				case GameStateEntityPropertyValueType.String:
					return typeof(DString);
				case GameStateEntityPropertyValueType.Blob:
					return typeof(DBlob);
				case GameStateEntityPropertyValueType.DateTime:
					return typeof(DDateTime);
				case GameStateEntityPropertyValueType.TimeSpan:
					return typeof(DTimeSpan);
				case GameStateEntityPropertyValueType.Guid:
					return typeof(DGuid);
				case GameStateEntityPropertyValueType.CustomSmall:
					return typeof(DSmallCustom);
				case GameStateEntityPropertyValueType.CustomSmallNullable:
					return typeof(DSmallNullableCustom);
				case GameStateEntityPropertyValueType.Custom:
					return typeof(DCustom);
				case GameStateEntityPropertyValueType.CustomNullable:
					return typeof(DNullableCustom);
			}

			throw new Exception("Unknown propery type " + type);
		}

		public static IDistributableValueType MakeValue(byte type)
		{
			Type dtype = GetDistributableType((GameStateEntityPropertyValueType)type);
			return (IDistributableValueType)Activator.CreateInstance(dtype);
		}


		public static IDistributableValueType MakeArray(byte type)
		{
			Type dtype = GetDistributableType((GameStateEntityPropertyValueType)type);
			Type arrayType = typeof(ServerDistributedArray<>).MakeGenericType(dtype);

			return (IDistributableValueType)Activator.CreateInstance(arrayType);
		}

		public static IDistributableValueType MakeQueue(byte type)
		{
			Type dtype = GetDistributableType((GameStateEntityPropertyValueType)type);
			Type arrayType = typeof(ServerDistributedQueue<>).MakeGenericType(dtype);

			return (IDistributableValueType)Activator.CreateInstance(arrayType);
		}

		public static IDistributableValueType MakeIntDictionary(byte type)
		{
			Type dtype = GetDistributableType((GameStateEntityPropertyValueType)type);
			Type arrayType = typeof(ServerDistributedIntDictionary<>).MakeGenericType(dtype);

			return (IDistributableValueType)Activator.CreateInstance(arrayType);
		}

		public static IDistributableValueType MakeStringDictionary(byte type)
		{
			Type dtype = GetDistributableType((GameStateEntityPropertyValueType)type);
			Type arrayType = typeof(ServerDistributedStringDictionary<>).MakeGenericType(dtype);

			return (IDistributableValueType)Activator.CreateInstance(arrayType);
		}


	}


}