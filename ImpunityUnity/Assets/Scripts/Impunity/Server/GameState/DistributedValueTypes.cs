
using System;
using System.IO;

namespace Impunity.GameState
{

	public interface IDistributableValueType
	{
		GameStateEntityPropertyValueType ValueType { get; }

		void WriteTo(BinaryWriter w);
		void ReadFrom(BinaryReader r);
	}

	// Single values

	public struct DBool : IDistributableValueType
	{
		private bool Value;

		public DBool(bool v)
		{
			Value = v;
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

	}

	public struct DInt8 : IDistributableValueType
	{
		private sbyte Value;

		public DInt8(sbyte v)
		{
			Value = v;
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
	}

	public struct DUInt8 : IDistributableValueType
	{
		private byte Value;

		public DUInt8(byte v)
		{
			Value = v;
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
	}

	public struct DInt16 : IDistributableValueType
	{
		private short Value;

		public DInt16(short v)
		{
			Value = v;
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
	}

	public struct DUInt16 : IDistributableValueType
	{
		private ushort Value;

		public DUInt16(ushort v)
		{
			Value = v;
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
	}

	public struct DInt32 : IDistributableValueType
	{
		private int Value;

		public DInt32(int v)
		{
			Value = v;
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
	}

	public struct DUInt32 : IDistributableValueType
	{
		private uint Value;

		public DUInt32(uint v)
		{
			Value = v;
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
	}

	public struct DInt64 : IDistributableValueType
	{
		private long Value;

		public DInt64(long v)
		{
			Value = v;
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
	}

	public struct DUInt64 : IDistributableValueType
	{
		private ulong Value;

		public DUInt64(ulong v)
		{
			Value = v;
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
	}

	public struct DFloat : IDistributableValueType
	{
		private float Value;

		public DFloat(float v)
		{
			Value = v;
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
	}

	public struct DDouble : IDistributableValueType
	{
		private double Value;

		public DDouble(double v)
		{
			Value = v;
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
	}

	public struct DDecimal : IDistributableValueType
	{
		private decimal Value;

		public DDecimal(decimal v)
		{
			Value = v;
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
	}

	public struct DChar : IDistributableValueType
	{
		private char Value;

		public DChar(char v)
		{
			Value = v;
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
	}

	public struct DString : IDistributableValueType
	{
		private string Value;

		public DString(string v)
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

		public static implicit operator string(DString d) => d.Value;
		public static implicit operator DString(string d) => new DString(d);
		public bool Equals(DString v) => String.Equals(Value, v.ValueType);
	}

	

	public struct DBlob : IDistributableValueType
	{
		private byte[] Value;

		public DBlob(byte[] v)
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

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Blob; }

		public static implicit operator byte[](DBlob d) => d.Value;
		public static implicit operator DBlob(byte[] d) => new DBlob(d);
		public bool Equals(DBlob v) => Array.Equals(Value, v.Value);
	}

	public struct DDateTime : IDistributableValueType
	{
		private DateTime Value;

		public DDateTime(DateTime v)
		{
			Value = v;
		}

		public void WriteTo(BinaryWriter w)
		{
			w.Write(Value.ToBinary());
		}

		public void ReadFrom(BinaryReader r)
		{
			Value = new DateTime(r.ReadInt64());
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.DateTime; }

		public static implicit operator DateTime(DDateTime d) => d.Value;
		public static implicit operator DDateTime(DateTime d) => new DDateTime(d);
		public bool Equals(DDateTime v) => Value == v.Value;
	}

	public struct DTimeSpan : IDistributableValueType
	{
		private TimeSpan Value;

		public DTimeSpan(TimeSpan v)
		{
			Value = v;
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
	}

	public struct DGuid : IDistributableValueType
	{
		private Guid Value;

		public DGuid(Guid v)
		{
			Value = v;
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
	}


}