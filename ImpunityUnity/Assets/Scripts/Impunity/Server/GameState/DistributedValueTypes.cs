
using System;
using System.IO;

using UltraLiteDB;


namespace Impunity.GameState
{

	/// <summary>Interface for value types that can be replicated over the network via binary serialization.</summary>
	public interface IDistributableValueType
	{
		/// <summary>The wire type identifier for this value.</summary>
		GameStateEntityPropertyValueType ValueType { get; }

		/// <summary>Writes this value to a binary stream.</summary>
		void WriteTo(BinaryWriter w);
		/// <summary>Reads this value from a binary stream.</summary>
		void ReadFrom(BinaryReader r);
	}

	/// <summary>Extended interface for standard value types that also support BSON persistence and temporal tracking.</summary>
	public interface IStandardDistributableValueType : IDistributableValueType
	{
		/// <summary>Converts this value to a BSON value for database storage.</summary>
		BsonValue? AsBsonValue();
		/// <summary>Populates this value from a BSON value loaded from database.</summary>
		void FromBsonValue(BsonValue value);
		/// <summary>Server timestamp (ms since epoch) of the last modification. Used for temporal conflict resolution.</summary>
		long LastModifiedTime { get; set; }
	}

	// Distributed value type wrappers (DBool, DInt8, DInt16, etc.)
	// Each is a struct implementing IStandardDistributableValueType with:
	//   - Implicit conversion operators to/from the underlying CLR type
	//   - Binary serialization (WriteTo/ReadFrom)
	//   - BSON persistence (AsBsonValue/FromBsonValue)
	//   - Equality comparison

	/// <summary>Distributed boolean value.</summary>
	public struct DBool : IStandardDistributableValueType, IEquatable<DBool>
	{
		private bool Value;

		public long LastModifiedTime { get; set; }

		public DBool(bool v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadBoolean();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Boolean; }

		public static implicit operator bool(DBool d) => d.Value;
		public static implicit operator DBool(bool d) => new DBool(d);
		public bool Equals(DBool v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsBoolean; }
	}

	public struct DInt8 : IStandardDistributableValueType, IEquatable<DInt8>
	{
		private sbyte Value;

		public long LastModifiedTime { get; set; }

		public DInt8(sbyte v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadSByte();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int8; }

		public static implicit operator sbyte(DInt8 d) => d.Value;
		public static implicit operator DInt8(sbyte d) => new DInt8(d);
		public bool Equals(DInt8 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = (sbyte)value.AsInt32; }
	}

	public struct DUInt8 : IStandardDistributableValueType, IEquatable<DUInt8>
	{
		private byte Value;

		public long LastModifiedTime { get; set; }

		public DUInt8(byte v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadByte();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt8; }

		public static implicit operator byte(DUInt8 d) => d.Value;
		public static implicit operator DUInt8(byte d) => new DUInt8(d);
		public bool Equals(DUInt8 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return (int)Value; }
		public void FromBsonValue(BsonValue value) { Value = (byte)value.AsInt32; }
	}

	public struct DInt16 : IStandardDistributableValueType, IEquatable<DInt16>
	{
		private short Value;

		public long LastModifiedTime { get; set; }

		public DInt16(short v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadInt16();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int16; }

		public static implicit operator short(DInt16 d) => d.Value;
		public static implicit operator DInt16(short d) => new DInt16(d);
		public bool Equals(DInt16 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = (short)value.AsInt32; }
	}

	public struct DUInt16 : IStandardDistributableValueType, IEquatable<DUInt16>
	{
		private ushort Value;

		public long LastModifiedTime { get; set; }

		public DUInt16(ushort v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadUInt16();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt16; }

		public static implicit operator ushort(DUInt16 d) => d.Value;
		public static implicit operator DUInt16(ushort d) => new DUInt16(d);
		public bool Equals(DUInt16 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return (int)Value; }
		public void FromBsonValue(BsonValue value) { Value = (ushort)value.AsInt32; }
	}

	public struct DInt32 : IStandardDistributableValueType, IEquatable<DInt32>
	{
		private int Value;

		public long LastModifiedTime { get; set; }

		public DInt32(int v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int32; }

		public static implicit operator int(DInt32 d) => d.Value;
		public static implicit operator DInt32(int d) => new DInt32(d);
		public bool Equals(DInt32 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsInt32; }
	}

	public struct DUInt32 : IStandardDistributableValueType, IEquatable<DUInt32>
	{
		private uint Value;

		public long LastModifiedTime { get; set; }

		public DUInt32(uint v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadUInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt32; }

		public static implicit operator uint(DUInt32 d) => d.Value;
		public static implicit operator DUInt32(uint d) => new DUInt32(d);
		public bool Equals(DUInt32 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return (int)Value; }
		public void FromBsonValue(BsonValue value) { Value = (uint)value.AsInt32; }
	}

	public struct DInt64 : IStandardDistributableValueType, IEquatable<DInt64>
	{
		private long Value;

		public long LastModifiedTime { get; set; }

		public DInt64(long v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadInt64();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int64; }

		public static implicit operator long(DInt64 d) => d.Value;
		public static implicit operator DInt64(long d) => new DInt64(d);
		public bool Equals(DInt64 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsInt64; }
	}

	public struct DUInt64 : IStandardDistributableValueType, IEquatable<DUInt64>
	{
		private ulong Value;

		public long LastModifiedTime { get; set; }

		public DUInt64(ulong v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadUInt64();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt64; }

		public static implicit operator ulong(DUInt64 d) => d.Value;
		public static implicit operator DUInt64(ulong d) => new DUInt64(d);
		public bool Equals(DUInt64 v) => Value == v.Value;

		public BsonValue AsBsonValue() { return (long)Value; }
		public void FromBsonValue(BsonValue value) { Value = (ulong)value.AsInt64; }
	}

	public struct DFloat : IStandardDistributableValueType, IEquatable<DFloat>
	{
		private float Value;

		public long LastModifiedTime { get; set; }

		public DFloat(float v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadSingle();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Float; }

		public static implicit operator float(DFloat d) => d.Value;
		public static implicit operator DFloat(float d) => new DFloat(d);
		public bool Equals(DFloat v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsSingle; }
	}

	public struct DDouble : IStandardDistributableValueType, IEquatable<DDouble>
	{
		private double Value;

		public long LastModifiedTime { get; set; }

		public DDouble(double v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadDouble();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Double; }

		public static implicit operator double(DDouble d) => d.Value;
		public static implicit operator DDouble(double d) => new DDouble(d);
		public bool Equals(DDouble v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsDouble; }
	}

	public struct DDecimal : IStandardDistributableValueType, IEquatable<DDecimal>
	{
		private decimal Value;

		public long LastModifiedTime { get; set; }

		public DDecimal(decimal v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadDecimal();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Decimal; }

		public static implicit operator decimal(DDecimal d) => d.Value;
		public static implicit operator DDecimal(decimal d) => new DDecimal(d);
		public bool Equals(DDecimal v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsDecimal; }
	}

	public struct DChar : IStandardDistributableValueType, IEquatable<DChar>
	{
		private char Value;

		public long LastModifiedTime { get; set; }

		public DChar(char v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = r.ReadChar();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Char; }

		public static implicit operator char(DChar d) => d.Value;
		public static implicit operator DChar(char d) => new DChar(d);
		public bool Equals(DChar v) => Value == v.Value;

		public BsonValue AsBsonValue() { return (int)Value; }
		public void FromBsonValue(BsonValue value) { Value = (char)value.AsInt32; }
	}

	public struct DString : IStandardDistributableValueType, IEquatable<DString>
	{
		private string? Value;

		public long LastModifiedTime { get; set; }

		public DString(string v)
		{
			Value = v;
			LastModifiedTime = 0;
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
				w.Write(Value);
			}
		}

		public void ReadFrom(BinaryReader r)
		{
			bool hasValue = r.ReadBoolean();
			if (hasValue)
			{
				Value = r.ReadString();
			}
			else
			{
				Value = null;
			}
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.String; }

		public static implicit operator string?(DString d) => d.Value;
		public static implicit operator DString(string d) => new DString(d);
		public bool Equals(DString v) => String.Equals(Value, v.Value);

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsString; }
	}

	

	public struct DBlob : IStandardDistributableValueType, IEquatable<DBlob>
	{
		private ArraySegment<byte> Value;

		public long LastModifiedTime { get; set; }

		public DBlob(ArraySegment<byte> v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			if (Value.Array != null)
			{
				w.Write(true);
				w.Write((ushort)Value.Count);
				w.Write(Value);
			}
			else
			{
				w.Write(false);
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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Blob; }

		public static implicit operator ArraySegment<byte>(DBlob d) => d.Value;
		public static implicit operator DBlob(ArraySegment<byte> d) => new DBlob(d);
		public bool Equals(DBlob v) => ImpunityUtil.CompareArraySegments(Value, v.Value) == 0;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsBinary; }
	}

	public struct DDateTime : IStandardDistributableValueType, IEquatable<DDateTime>
	{
		private DateTime Value;

		public long LastModifiedTime { get; set; }

		public DDateTime(DateTime v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value.ToBinary());
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = DateTime.FromBinary(r.ReadInt64());
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTime; }

		public static implicit operator DateTime(DDateTime d) => d.Value;
		public static implicit operator DDateTime(DateTime d) => new DDateTime(d);
		public bool Equals(DDateTime v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsDateTime; }
	}

	public struct DDateTimeOffset : IStandardDistributableValueType, IEquatable<DDateTimeOffset>
	{
		private DateTimeOffset Value;

		public long LastModifiedTime { get; set; }

		public DDateTimeOffset(DateTimeOffset v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value.Ticks);
			w.Write((short)Value.Offset.Minutes);
		}

		public void ReadFrom(BinaryReader r)
		{
			long ticks = r.ReadInt64();
			TimeSpan offset = TimeSpan.FromMinutes(r.ReadInt16());
			Value = new DateTimeOffset(ticks, offset);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTimeOffset; }

		public static implicit operator DateTimeOffset(DDateTimeOffset d) => d.Value;
		public static implicit operator DDateTimeOffset(DateTimeOffset d) => new DDateTimeOffset(d);
		public bool Equals(DDateTimeOffset v) => Value == v.Value;

		public BsonValue AsBsonValue()
		{
			BsonDocument doc = new BsonDocument();
			doc["t"] = Value.Ticks;
			doc["o"] = Value.Offset.Minutes;
			return doc;
		}

		public void FromBsonValue(BsonValue value)
		{
			BsonDocument doc = value.AsDocument!;
			long ticks = doc.GetInt64OrDefault("t", 0);
			TimeSpan offset = TimeSpan.FromMinutes(doc.GetInt32OrDefault("o", 0));
			Value = new DateTimeOffset(ticks, offset);
		}
	}

	public struct DTimeSpan : IStandardDistributableValueType, IEquatable<DTimeSpan>
	{
		private TimeSpan Value;

		public long LastModifiedTime { get; set; }

		public DTimeSpan(TimeSpan v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value.Ticks);
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = new TimeSpan(r.ReadInt64());
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.TimeSpan; }

		public static implicit operator TimeSpan(DTimeSpan d) => d.Value;
		public static implicit operator DTimeSpan(TimeSpan d) => new DTimeSpan(d);
		public bool Equals(DTimeSpan v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value.Ticks; }
		public void FromBsonValue(BsonValue value) { Value = new TimeSpan(value.AsInt64); }
	}

	public struct DGuid : IStandardDistributableValueType, IEquatable<DGuid>
	{
		private Guid Value;

		public long LastModifiedTime { get; set; }

		public DGuid(Guid v)
		{
			Value = v;
			LastModifiedTime = 0;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value.ToByteArray());
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = new Guid(r.ReadBytes(16));
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Guid; }

		public static implicit operator Guid(DGuid d) => d.Value;
		public static implicit operator DGuid(Guid d) => new DGuid(d);
		public bool Equals(DGuid v) => Value == v.Value;

		public BsonValue AsBsonValue() { return Value; }
		public void FromBsonValue(BsonValue value) { Value = value.AsGuid; }
	}


}