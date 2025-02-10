using System;
using System.IO;

namespace Impunity.Connection
{

	public interface IDistributableValueSerializer<T>
	{
		void WriteTo(T value, BinaryWriter w);
		T ReadFrom(BinaryReader r);

		GameStateEntityPropertyValueType ValueType { get; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Boolean; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int8; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt8; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int16; }
	}

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

		public readonly GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt16; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int32; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt32; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int64; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt64; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Float; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Double; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Decimal; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Char; }
	}

	public readonly struct StringSerializer : IDistributableValueSerializer<string>
	{
		public void WriteTo(string value, BinaryWriter w)
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

		public string ReadFrom(BinaryReader r)
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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.String; }
	}

	

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Blob; }
	}

	public readonly struct DateTimeSerializer : IDistributableValueSerializer<DateTime>
	{
		public void WriteTo(DateTime value, BinaryWriter w)
		{
			w.Write(value.ToBinary());
		}

		public DateTime ReadFrom(BinaryReader r)
		{
			return new DateTime(r.ReadInt64());
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTime; }
	}

	public readonly struct DateTimeOffsetSerializer : IDistributableValueSerializer<DateTimeOffset>
	{
		public void WriteTo(DateTimeOffset value, BinaryWriter w)
		{
			w.Write(value.Ticks);
			w.Write((short)value.Offset.Minutes);
		}

		public DateTimeOffset ReadFrom(BinaryReader r)
		{
			long ticks = r.ReadInt64();
			TimeSpan offset = TimeSpan.FromMinutes(r.ReadInt16());
			return new DateTimeOffset(ticks, offset);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTime; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.TimeSpan; }
	}

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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Guid; }
	}
}