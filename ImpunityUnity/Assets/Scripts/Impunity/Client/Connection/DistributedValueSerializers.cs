using System;
using System.IO;

using UltraLiteDB;

namespace Impunity.Connection
{

	/// <summary>
	/// Interface for binary serializers used by distributed field types. Each implementation reads and
	/// writes just the <em>payload</em> of its value type — the wire framing (length prefix for the
	/// <c>Custom*</c> types, and the null indicator for the nullable ones) is applied by
	/// <see cref="FramingSerializer"/>, driven by <see cref="ValueType"/>. This means a custom serializer only
	/// has to write its bytes; it cannot forget or mismatch the framing.
	/// Implemented as readonly structs for zero-allocation generic specialization.
	/// </summary>
	/// <typeparam name="T">The value type this serializer handles.</typeparam>
	public interface IDistributableValueSerializer<T>
	{
		/// <summary>Writes <paramref name="value"/>'s bytes to the stream.
		/// For the <c>Custom*</c> value types <see cref="FramingSerializer"/> measures what is written
		/// here and prepends the length; primitive types are written as-is.</summary>
		void WriteTo(T value, BinaryWriter w);

		/// <summary>Reads and returns a value from its payload bytes. <paramref name="byteCount"/> is the
		/// payload length <see cref="FramingSerializer"/> already consumed for a <c>Custom*</c> type (useful for
		/// variable-length payloads); it is 0 for primitive types, which read their own fixed layout.</summary>
		T ReadFrom(BinaryReader r, int byteCount);

		/// <summary>Converts value to BsonValue</summary>
		BsonValue ToBsonValue(T value);

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		T FromBsonValue(BsonValue value);

		/// <summary>The property value type tag used in the wire protocol for this serializer's type.
		/// <see cref="FramingSerializer"/> uses this to decide the framing applied around <see cref="WriteTo"/>.</summary>
		GameStateEntityPropertyValueType ValueType { get; }
	}

	/// <summary>
	/// Central wire framing for distributed values. A serializer only reads and writes its payload
	/// (<see cref="IDistributableValueSerializer{T}.WriteTo"/> /
	/// <see cref="IDistributableValueSerializer{T}.ReadFrom"/>); this class adds the length prefix and
	/// null indicator required by the <c>Custom*</c> value types, and passes primitive types straight
	/// through. The framing to apply is chosen from
	/// <see cref="IDistributableValueSerializer{T}.ValueType"/>, which is a compile-time constant for a
	/// concrete serializer struct, so the switch folds away in specialized generics — this is as cheap as
	/// calling the serializer directly.
	/// </summary>
	public static class FramingSerializer
	{
		/// <summary>Writes <paramref name="value"/> with whatever framing its serializer's
		/// <see cref="IDistributableValueSerializer{T}.ValueType"/> requires.</summary>
		public static void Write<T, S>(S serializer, T value, BinaryWriter w) where S : IDistributableValueSerializer<T>
		{
			switch (serializer.ValueType)
			{
				case GameStateEntityPropertyValueType.CustomSmall:
					WriteBytePrefixed(serializer, value, w);
					break;
				case GameStateEntityPropertyValueType.CustomSmallNullable:
					if (value is null) { w.Write(false); break; }
					w.Write(true);
					WriteBytePrefixed(serializer, value, w);
					break;
				case GameStateEntityPropertyValueType.Custom:
					WriteUShortPrefixed(serializer, value, w);
					break;
				case GameStateEntityPropertyValueType.CustomNullable:
					if (value is null) { w.Write(false); break; }
					w.Write(true);
					WriteUShortPrefixed(serializer, value, w);
					break;
				default:
					// Primitive / self-framing types (String, Blob) write their payload directly.
					serializer.WriteTo(value, w);
					break;
			}
		}

		/// <summary>Reads a value written by <see cref="Write{T,S}"/>.</summary>
		public static T Read<T, S>(S serializer, BinaryReader r) where S : IDistributableValueSerializer<T>
		{
			switch (serializer.ValueType)
			{
				case GameStateEntityPropertyValueType.CustomSmall:
				{
					int count = r.ReadByte();
					// A zero length is the uninitialized/default sentinel the server sends for an unset field.
					return count == 0 ? default! : serializer.ReadFrom(r, count);
				}
				case GameStateEntityPropertyValueType.CustomSmallNullable:
				{
					if (!r.ReadBoolean()) return default!;
					int count = r.ReadByte();
					return serializer.ReadFrom(r, count);
				}
				case GameStateEntityPropertyValueType.Custom:
				{
					int count = r.ReadUInt16();
					return count == 0 ? default! : serializer.ReadFrom(r, count);
				}
				case GameStateEntityPropertyValueType.CustomNullable:
				{
					if (!r.ReadBoolean()) return default!;
					int count = r.ReadUInt16();
					return serializer.ReadFrom(r, count);
				}
				default:
					return serializer.ReadFrom(r, 0);
			}
		}

		/// <summary>Advances past a value written by <see cref="Write{T,S}"/> without deserializing it.</summary>
		public static void Skip<T, S>(S serializer, BinaryReader r) where S : IDistributableValueSerializer<T>
		{
			switch (serializer.ValueType)
			{
				case GameStateEntityPropertyValueType.CustomSmall:
					SkipBytes(r, r.ReadByte());
					break;
				case GameStateEntityPropertyValueType.CustomSmallNullable:
					if (r.ReadBoolean()) SkipBytes(r, r.ReadByte());
					break;
				case GameStateEntityPropertyValueType.Custom:
					SkipBytes(r, r.ReadUInt16());
					break;
				case GameStateEntityPropertyValueType.CustomNullable:
					if (r.ReadBoolean()) SkipBytes(r, r.ReadUInt16());
					break;
				default:
					// Primitive / self-framing types have no length prefix; read and discard.
					serializer.ReadFrom(r, 0);
					break;
			}
		}

		// Reserve the length prefix, write the payload, then seek back and patch in the real length. The
		// outgoing BinaryWriter is always backed by a seekable MemoryStream, so this stays allocation-free
		// and works whether the payload is fixed- or variable-length.
		private static void WriteBytePrefixed<T, S>(S serializer, T value, BinaryWriter w) where S : IDistributableValueSerializer<T>
		{
			Stream s = w.BaseStream;
			long lenPos = s.Position;
			w.Write((byte)0);
			long start = s.Position;
			serializer.WriteTo(value, w);
			long end = s.Position;
			long count = end - start;
			if (count > byte.MaxValue)
			{
				throw new Exception($"Custom value of {count} bytes is too large for a CustomSmall field (max {byte.MaxValue}); use a Custom (ushort-prefixed) value type instead.");
			}
			s.Position = lenPos;
			w.Write((byte)count);
			s.Position = end;
		}

		private static void WriteUShortPrefixed<T, S>(S serializer, T value, BinaryWriter w) where S : IDistributableValueSerializer<T>
		{
			Stream s = w.BaseStream;
			long lenPos = s.Position;
			w.Write((ushort)0);
			long start = s.Position;
			serializer.WriteTo(value, w);
			long end = s.Position;
			long count = end - start;
			if (count > ushort.MaxValue)
			{
				throw new Exception($"Custom value of {count} bytes is too large to serialize (max {ushort.MaxValue}).");
			}
			s.Position = lenPos;
			w.Write((ushort)count);
			s.Position = end;
		}

		private static void SkipBytes(BinaryReader r, int count)
		{
			if (count > 0)
			{
				r.BaseStream.Seek(count, SeekOrigin.Current);
			}
		}
	}

	/// <summary>Binary serializer for <see cref="bool"/> values.</summary>
	public readonly struct BoolSerializer : IDistributableValueSerializer<bool>
	{
		public void WriteTo(bool value, BinaryWriter w)
		{
			w.Write(value);
		}

		public bool ReadFrom(BinaryReader r, int byteCount)
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

		public sbyte ReadFrom(BinaryReader r, int byteCount)
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

		public byte ReadFrom(BinaryReader r, int byteCount)
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

		public short ReadFrom(BinaryReader r, int byteCount)
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

		public readonly ushort ReadFrom(BinaryReader r, int byteCount)
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

		public int ReadFrom(BinaryReader r, int byteCount)
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

		public readonly uint ReadFrom(BinaryReader r, int byteCount)
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

		public long ReadFrom(BinaryReader r, int byteCount)
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

		public ulong ReadFrom(BinaryReader r, int byteCount)
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

		public float ReadFrom(BinaryReader r, int byteCount)
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

		public double ReadFrom(BinaryReader r, int byteCount)
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

		public decimal ReadFrom(BinaryReader r, int byteCount)
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

		public char ReadFrom(BinaryReader r, int byteCount)
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

		public string? ReadFrom(BinaryReader r, int byteCount)
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

		public ArraySegment<byte> ReadFrom(BinaryReader r, int byteCount)
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

		public DateTime ReadFrom(BinaryReader r, int byteCount)
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

		public DateTimeOffset ReadFrom(BinaryReader r, int byteCount)
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

		public TimeSpan ReadFrom(BinaryReader r, int byteCount)
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

		public Guid ReadFrom(BinaryReader r, int byteCount)
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

	/// <summary>BSON serializer for small custom objects (max 255 bytes). CustomSmall value type — framing
	/// (single-byte length prefix) supplied by <see cref="FramingSerializer"/>.</summary>
	/// <typeparam name="T">The object type to serialize via BsonMapper.</typeparam>
	public readonly struct BsonSmallSerializer<T> : IDistributableValueSerializer<T> where T : class
	{
		public void WriteTo(T value, BinaryWriter w)
		{
			w.Write(BsonSerializer.Serialize(ImpunityUtil.GetBsonMapper().SerializeObject(value)));
		}

		public T ReadFrom(BinaryReader r, int byteCount)
		{
			byte[] bytes = r.ReadBytes(byteCount);
			return ImpunityUtil.GetBsonMapper().ToObject<T>(BsonSerializer.Deserialize(bytes));
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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmallNullable; }
	}


	/// <summary>BSON serializer for custom objects (max 65535 bytes). Custom value type — framing
	/// (ushort length prefix) supplied by <see cref="FramingSerializer"/>.</summary>
	/// <typeparam name="T">The object type to serialize via BsonMapper.</typeparam>
	public readonly struct BsonSerializer<T> : IDistributableValueSerializer<T> where T : class
	{
		public void WriteTo(T value, BinaryWriter w)
		{
			w.Write(BsonSerializer.Serialize(ImpunityUtil.GetBsonMapper().SerializeObject(value)));
		}

		public T ReadFrom(BinaryReader r, int byteCount)
		{
			byte[] bytes = r.ReadBytes(byteCount);
			return ImpunityUtil.GetBsonMapper().ToObject<T>(BsonSerializer.Deserialize(bytes));
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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomNullable; }
	}
}
