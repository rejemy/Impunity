using System;
using System.IO;
using UltraLiteDB;

namespace Impunity.Connection
{

	/// <summary>
	/// Interface for binary serializers used by distributed field types. Each implementation handles
	/// reading and writing a specific value type to/from the wire protocol's binary stream.
	/// Implemented as readonly structs for zero-allocation generic specialization.
	/// </summary>
	/// <typeparam name="T">The value type this serializer handles.</typeparam>
	public interface IDistributableValueSerializer<T>
	{
		/// <summary>Writes <paramref name="value"/> to the binary stream.</summary>
		void WriteTo(T value, BinaryWriter w);
		/// <summary>Reads and returns a value from the binary stream.</summary>
		T ReadFrom(BinaryReader r);

		/// <summary>Converts value to BsonValue</summary>
		BsonValue ToBsonValue(T value);

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		T FromBsonValue(BsonValue value);

		/// <summary>The property value type tag used in the wire protocol for this serializer's type.</summary>
		GameStateEntityPropertyValueType ValueType { get; }
	}

	/// <summary>Binary serializer for <see cref="bool"/> values.</summary>
	public readonly struct BoolSerializer : IDistributableValueSerializer<bool>
	{
		public void WriteTo(bool value, BinaryWriter w)
		{
			w.Write(value);
		}

		public bool ReadFrom(BinaryReader r)
		{
			return r.ReadBoolean();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(bool value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public bool FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Boolean; }
	}

	/// <summary>Binary serializer for <see cref="sbyte"/> values.</summary>
	public readonly struct Int8Serializer : IDistributableValueSerializer<sbyte>
	{
		public void WriteTo(sbyte value, BinaryWriter w)
		{
			w.Write(value);
		}

		public sbyte ReadFrom(BinaryReader r)
		{
			return r.ReadSByte();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(sbyte value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public sbyte FromBsonValue(BsonValue value)
		{
			return (sbyte)value.AsInt32;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int8; }
	}

	/// <summary>Binary serializer for <see cref="byte"/> values.</summary>
	public readonly struct UInt8Serializer : IDistributableValueSerializer<byte>
	{
		public void WriteTo(byte value, BinaryWriter w)
		{
			w.Write(value);
		}

		public byte ReadFrom(BinaryReader r)
		{
			return r.ReadByte();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(byte value)
		{
			return (int)value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public byte FromBsonValue(BsonValue value)
		{
			return (byte)value.AsInt32;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt8; }
	}

	/// <summary>Binary serializer for <see cref="short"/> values.</summary>
	public readonly struct Int16Serializer : IDistributableValueSerializer<short>
	{
		public void WriteTo(short value, BinaryWriter w)
		{
			w.Write(value);
		}

		public short ReadFrom(BinaryReader r)
		{
			return r.ReadInt16();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(short value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public short FromBsonValue(BsonValue value)
		{
			return (short)value.AsInt32;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int16; }
	}

	/// <summary>Binary serializer for <see cref="ushort"/> values.</summary>
	public struct UInt16Serializer : IDistributableValueSerializer<ushort>
	{
		public readonly void WriteTo(ushort value, BinaryWriter w)
		{
			w.Write(value);
		}

		public readonly ushort ReadFrom(BinaryReader r)
		{
			return r.ReadUInt16();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(ushort value)
		{
			return (int)value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public ushort FromBsonValue(BsonValue value)
		{
			return (ushort)value.AsInt32;
		}

		public readonly GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt16; }
	}

	/// <summary>Binary serializer for <see cref="int"/> values.</summary>
	public readonly struct Int32Serializer : IDistributableValueSerializer<int>
	{
		public void WriteTo(int value, BinaryWriter w)
		{
			w.Write(value);
		}

		public int ReadFrom(BinaryReader r)
		{
			return r.ReadInt32();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(int value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public int FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int32; }
	}

	/// <summary>Binary serializer for <see cref="uint"/> values.</summary>
	public readonly struct UInt32Serializer : IDistributableValueSerializer<uint>
	{
		public readonly void WriteTo(uint value, BinaryWriter w)
		{
			w.Write(value);
		}

		public readonly uint ReadFrom(BinaryReader r)
		{
			return r.ReadUInt32();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(uint value)
		{
			return (int)value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public uint FromBsonValue(BsonValue value)
		{
			return (uint)value.AsInt32;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt32; }
	}

	/// <summary>Binary serializer for <see cref="long"/> values.</summary>
	public struct Int64Serializer : IDistributableValueSerializer<long>
	{
		public void WriteTo(long value, BinaryWriter w)
		{
			w.Write(value);
		}

		public long ReadFrom(BinaryReader r)
		{
			return r.ReadInt64();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(long value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public long FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int64; }
	}

	/// <summary>Binary serializer for <see cref="ulong"/> values.</summary>
	public readonly struct UInt64Serializer : IDistributableValueSerializer<ulong>
	{
		public void WriteTo(ulong value, BinaryWriter w)
		{
			w.Write(value);
		}

		public ulong ReadFrom(BinaryReader r)
		{
			return r.ReadUInt64();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(ulong value)
		{
			// Store the bit pattern as Int64 (matches the server's DUInt64). A bare `return value;`
			// would route ulong->double->BsonValue, losing precision and throwing on read-back.
			return unchecked((long)value);
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public ulong FromBsonValue(BsonValue value)
		{
			return unchecked((ulong)value.AsInt64);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt64; }
	}

	/// <summary>Binary serializer for <see cref="float"/> values.</summary>
	public readonly struct FloatSerializer : IDistributableValueSerializer<float>
	{
		public void WriteTo(float value, BinaryWriter w)
		{
			w.Write(value);
		}

		public float ReadFrom(BinaryReader r)
		{
			return r.ReadSingle();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(float value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public float FromBsonValue(BsonValue value)
		{
			// BsonValue stores floats as Double; the implicit BsonValue->float cast throws on a
			// boxed Double, so read through AsSingle.
			return value.AsSingle;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Float; }
	}

	/// <summary>Binary serializer for <see cref="double"/> values.</summary>
	public readonly struct DoubleSerializer : IDistributableValueSerializer<double>
	{
		public void WriteTo(double value, BinaryWriter w)
		{
			w.Write(value);
		}

		public double ReadFrom(BinaryReader r)
		{
			return r.ReadDouble();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(double value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public double FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Double; }
	}

	/// <summary>Binary serializer for <see cref="decimal"/> values.</summary>
	public readonly struct DecimalSerializer : IDistributableValueSerializer<decimal>
	{
		public void WriteTo(decimal value, BinaryWriter w)
		{
			w.Write(value);
		}

		public decimal ReadFrom(BinaryReader r)
		{
			return r.ReadDecimal();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(decimal value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public decimal FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Decimal; }
	}

	/// <summary>Binary serializer for <see cref="char"/> values.</summary>
	public readonly struct CharSerializer : IDistributableValueSerializer<char>
	{
		public void WriteTo(char value, BinaryWriter w)
		{
			w.Write(value);
		}

		public char ReadFrom(BinaryReader r)
		{
			return r.ReadChar();
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(char value)
		{
			return value.ToString();
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public char FromBsonValue(BsonValue value)
		{
			return value.AsString[0];
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Char; }
	}

	/// <summary>Binary serializer for nullable <see cref="string"/> values. Prefixes with a boolean null indicator.</summary>
	public readonly struct StringSerializer : IDistributableValueSerializer<string?>
	{
		public void WriteTo(string? value, BinaryWriter w)
		{
			if (value == null)
			{
				w.Write(false);
			}
			else
			{
				w.Write(true);
				w.Write(value);
			}
		}

		public string? ReadFrom(BinaryReader r)
		{
			bool hasValue = r.ReadBoolean();
			if (hasValue)
			{
				return r.ReadString();
			}
			else
			{
				return null;
			}
		}


		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(string? value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public string? FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.String; }
	}

	

	/// <summary>Binary serializer for nullable byte array blobs. Prefixed with a boolean null indicator and ushort length.</summary>
	public readonly struct BlobSerializer : IDistributableValueSerializer<ArraySegment<byte>>
	{
		public void WriteTo(ArraySegment<byte> value, BinaryWriter w)
		{
			if (value.Array != null)
			{
				w.Write(true);
				w.Write((ushort)value.Count);
				w.Write(value);
			}
			else
			{
				w.Write(false);
			}
		}

		public ArraySegment<byte> ReadFrom(BinaryReader r)
		{
			bool hasValue = r.ReadBoolean();
			if (hasValue)
			{
				int count = r.ReadUInt16();
				return r.ReadBytes(count);
			}
			else
			{
				return null;
			}
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(ArraySegment<byte> value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public ArraySegment<byte> FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Blob; }
	}

	/// <summary>Binary serializer for <see cref="DateTime"/> values, stored as binary ticks.</summary>
	public readonly struct DateTimeSerializer : IDistributableValueSerializer<DateTime>
	{
		public void WriteTo(DateTime value, BinaryWriter w)
		{
			w.Write(value.ToBinary());
		}

		public DateTime ReadFrom(BinaryReader r)
		{
			return DateTime.FromBinary(r.ReadInt64());
		}


		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(DateTime value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public DateTime FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTime; }
	}

	/// <summary>Binary serializer for <see cref="DateTimeOffset"/> values, stored as ticks plus offset in minutes.</summary>
	public readonly struct DateTimeOffsetSerializer : IDistributableValueSerializer<DateTimeOffset>
	{
		public void WriteTo(DateTimeOffset value, BinaryWriter w)
		{
			w.Write(value.Ticks);
			w.Write((short)value.Offset.TotalMinutes);
		}

		public DateTimeOffset ReadFrom(BinaryReader r)
		{
			long ticks = r.ReadInt64();
			TimeSpan offset = TimeSpan.FromMinutes(r.ReadInt16());
			return new DateTimeOffset(ticks, offset);
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(DateTimeOffset value)
		{
			BsonDocument doc = new BsonDocument();
			doc["t"] = value.Ticks;
			// TotalMinutes, not Minutes: Offset.Minutes is only the minute-of-hour component, so
			// e.g. +05:30 would otherwise round-trip as +00:30.
			doc["o"] = (int)value.Offset.TotalMinutes;
			return doc;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public DateTimeOffset FromBsonValue(BsonValue value)
		{
			BsonDocument doc = value.AsDocument!;
			long ticks = doc.GetInt64OrDefault("t", 0);
			TimeSpan offset = TimeSpan.FromMinutes(doc.GetInt32OrDefault("o", 0));
			return new DateTimeOffset(ticks, offset);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTimeOffset; }
	}

	/// <summary>Binary serializer for <see cref="TimeSpan"/> values, stored as ticks.</summary>
	public readonly struct TimeSpanSerializer : IDistributableValueSerializer<TimeSpan>
	{
		public void WriteTo(TimeSpan value, BinaryWriter w)
		{
			w.Write(value.Ticks);
		}

		public TimeSpan ReadFrom(BinaryReader r)
		{
			return new TimeSpan(r.ReadInt64());
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(TimeSpan value)
		{
			return value.Ticks;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public TimeSpan FromBsonValue(BsonValue value)
		{
			return new TimeSpan(value.AsInt64);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.TimeSpan; }
	}

	/// <summary>Binary serializer for <see cref="Guid"/> values, stored as 16 raw bytes.</summary>
	public readonly struct GuidSerializer : IDistributableValueSerializer<Guid>
	{
		public void WriteTo(Guid value, BinaryWriter w)
		{
			w.Write(value.ToByteArray());
		}

		public Guid ReadFrom(BinaryReader r)
		{
			return new Guid(r.ReadBytes(16));
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Guid value)
		{
			return value;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Guid FromBsonValue(BsonValue value)
		{
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Guid; }
	}

	/// <summary>BSON serializer for small custom objects (max 254 bytes). Uses a single-byte length prefix.</summary>
	/// <typeparam name="T">The object type to serialize via BsonMapper.</typeparam>
	public readonly struct BsonSmallSerializer<T> : IDistributableValueSerializer<T> where T : class
	{
		public void WriteTo(T value, BinaryWriter w)
		{
			if (value == null)
			{
				w.Write((byte)0);
			}
			else
			{
				byte[] bytes = BsonSerializer.Serialize(ImpunityUtil.GetBsonMapper().SerializeObject(value));
				if (bytes.Length >= 255)
				{
					throw new Exception("Value to large to serialize as a small value");
				}
				w.Write((byte)bytes.Length);
				w.Write(bytes);
			}
		}

		public T ReadFrom(BinaryReader r)
		{
			byte length = r.ReadByte();
			if (length > 0)
			{
				byte[] bytes = r.ReadBytes(length);
				return ImpunityUtil.GetBsonMapper().ToObject<T>(BsonSerializer.Deserialize(bytes));
			}
			else
			{
				return null!; // null is a valid value for this type
			}
		}


		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(T value)
		{
			return ImpunityUtil.GetBsonMapper().SerializeObject(value);
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public T FromBsonValue(BsonValue value)
		{
			return ImpunityUtil.GetBsonMapper().ToObject<T>(value.AsDocument!);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>BSON serializer for custom objects (max 65534 bytes). Uses a ushort length prefix.</summary>
	/// <typeparam name="T">The object type to serialize via BsonMapper.</typeparam>
	public readonly struct BsonSerializer<T> : IDistributableValueSerializer<T> where T : class
	{
		public void WriteTo(T value, BinaryWriter w)
		{
			if (value == null)
			{
				w.Write((ushort)0);
			}
			else
			{
				byte[] bytes = BsonSerializer.Serialize(ImpunityUtil.GetBsonMapper().SerializeObject(value));
				if (bytes.Length >= ushort.MaxValue)
				{
					throw new Exception("Value to large to serialize");
				}
				w.Write((ushort)bytes.Length);
				w.Write(bytes);
			}
		}

		public T ReadFrom(BinaryReader r)
		{
			ushort length = r.ReadUInt16();
			if (length > 0)
			{
				byte[] bytes = r.ReadBytes(length);
				return ImpunityUtil.GetBsonMapper().ToObject<T>(BsonSerializer.Deserialize(bytes));
			}
			else
			{
				return null!; // null is a valid value for this type
			}
		}

				/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(T value)
		{
			return ImpunityUtil.GetBsonMapper().SerializeObject(value);
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public T FromBsonValue(BsonValue value)
		{
			return ImpunityUtil.GetBsonMapper().ToObject<T>(value.AsDocument!);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Custom; }
	}
}