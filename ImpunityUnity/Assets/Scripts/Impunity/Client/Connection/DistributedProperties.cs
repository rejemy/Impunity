
using System.IO;


namespace Impunity.Connection
{

	// Single values

	public struct DBool : IDistributableValueType
	{
		private bool Value;

		public DBool(bool v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadBoolean();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Boolean; }

		public static implicit operator bool(DBool d) => d.Value;
		public static implicit operator DBool(bool d) => new DBool(d);
	}

	public struct DInt8 : IDistributableValueType
	{
		private sbyte Value;

		public DInt8(sbyte v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
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

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadByte();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt8; }

		public static implicit operator byte(DUInt8 d) => d.Value;
		public static implicit operator DUInt8(byte d) => new DUInt8(d);
	}

	public struct DInt16 : IDistributableValueType
	{
		private short Value;

		public DInt16(short v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadInt16();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int16; }

		public static implicit operator short(DInt16 d) => d.Value;
		public static implicit operator DInt16(short d) => new DInt16(d);
	}

	public struct DUInt16 : IDistributableValueType
	{
		private ushort Value;

		public DUInt16(ushort v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadUInt16();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt16; }

		public static implicit operator ushort(DUInt16 d) => d.Value;
		public static implicit operator DUInt16(ushort d) => new DUInt16(d);
	}

	public struct DInt32 : IDistributableValueType
	{
		private int Value;

		public DInt32(int v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int32; }

		public static implicit operator int(DInt32 d) => d.Value;
		public static implicit operator DInt32(int d) => new DInt32(d);
	}

	public struct DUInt32 : IDistributableValueType
	{
		private uint Value;

		public DUInt32(uint v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadUInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt32; }

		public static implicit operator uint(DUInt32 d) => d.Value;
		public static implicit operator DUInt32(uint d) => new DUInt32(d);
	}

	public struct DInt64 : IDistributableValueType
	{
		private long Value;

		public DInt64(long v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadInt64();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Int64; }

		public static implicit operator long(DInt64 d) => d.Value;
		public static implicit operator DInt64(long d) => new DInt64(d);
	}

	public struct DUInt64 : IDistributableValueType
	{
		private ulong Value;

		public DUInt64(ulong v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadUInt64();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.UInt64; }

		public static implicit operator ulong(DUInt64 d) => d.Value;
		public static implicit operator DUInt64(ulong d) => new DUInt64(d);
	}

	public struct DFloat : IDistributableValueType
	{
		private float Value;

		public DFloat(float v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadSingle();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Float; }

		public static implicit operator float(DFloat d) => d.Value;
		public static implicit operator DFloat(float d) => new DFloat(d);
	}

	public struct DDouble : IDistributableValueType
	{
		private double Value;

		public DDouble(double v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadDouble();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Double; }

		public static implicit operator double(DDouble d) => d.Value;
		public static implicit operator DDouble(double d) => new DDouble(d);
	}

	public struct DDecimal : IDistributableValueType
	{
		private decimal Value;

		public DDecimal(decimal v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadDecimal();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Decimal; }

		public static implicit operator decimal(DDecimal d) => d.Value;
		public static implicit operator DDecimal(decimal d) => new DDecimal(d);
	}

	public struct DChar : IDistributableValueType
	{
		private char Value;

		public DChar(char v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write(Value);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadChar();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.Char; }

		public static implicit operator char(DChar d) => d.Value;
		public static implicit operator DChar(char d) => new DChar(d);
	}

	public struct DString : IDistributableValueType
	{
		private string Value;

		public DString(string v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
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

		public void ReadChangesFrom(BinaryReader r)
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
	}

	

	public struct DBlob : IDistributableValueType
	{
		private byte[] Value;

		public DBlob(byte[] v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
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

		public void ReadChangesFrom(BinaryReader r)
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
	}

	/*
	public struct DistributedBool : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Boolean; }

		private bool Value;
		private bool Dirty;

		public bool Set(bool val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
        {
			Value = r.ReadBoolean();
			Dirty = false;
        }

        public static implicit operator bool(DistributedBool d) => d.Value;
		public static implicit operator DistributedBool(bool d) => new DistributedBool { Value = d, Dirty = true };
	}

	public struct DistributedInt8 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Int8; }

		private sbyte Value;
		private bool Dirty;

		public bool Set(sbyte val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadSByte();
			Dirty = false;
		}

		public static implicit operator sbyte(DistributedInt8 d) => d.Value;
	}

	public struct DistributedUInt8 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.UInt8; }

		private byte Value;
		private bool Dirty;

		public bool Set(byte val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadByte();
			Dirty = false;
		}

		public static implicit operator byte(DistributedUInt8 d) => d.Value;
	}

	public struct DistributedInt16 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Int16; }

		private short Value;
		private bool Dirty;

		public bool Set(short val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadInt16();
			Dirty = false;
		}

		public static implicit operator short(DistributedInt16 d) => d.Value;
	}

	public struct DistributedUInt16 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Uint16; }

		private ushort Value;
		private bool Dirty;

		public bool Set(ushort val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadUInt16();
			Dirty = false;
		}

		public static implicit operator ushort(DistributedUInt16 d) => d.Value;
	}

	public struct DistributedInt32 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Int32; }

		private int Value;
		private bool Dirty;

		public bool Set(int val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadInt32();
			Dirty = false;
		}

		public static implicit operator int(DistributedInt32 d) => d.Value;
	}

	public struct DistributedUInt32 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Uint32; }

		private uint Value;
		private bool Dirty;

		public bool Set(uint val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadUInt32();
			Dirty = false;
		}

		public static implicit operator uint(DistributedUInt32 d) => d.Value;
	}

	public struct DistributedInt64 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Int64; }

		private long Value;
		private bool Dirty;

		public bool Set(long val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadInt32();
			Dirty = false;
		}

		public static implicit operator long(DistributedInt64 d) => d.Value;
	}

	public struct DistributedUInt64 : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Uint64; }

		private ulong Value;
		private bool Dirty;

		public bool Set(ulong val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadUInt32();
			Dirty = false;
		}

		public static implicit operator ulong(DistributedUInt64 d) => d.Value;
	}

	public struct DistributedFloat : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Float; }

		private float Value;
		private bool Dirty;

		public bool Set(float val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadSingle();
			Dirty = false;
		}

		public static implicit operator float(DistributedFloat d) => d.Value;
	}

	public struct DistributedDouble : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Double; }

		private double Value;
		private bool Dirty;

		public bool Set(double val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadDouble();
			Dirty = false;
		}

		public static implicit operator double(DistributedDouble d) => d.Value;
	}

	public struct DistributedDecimal : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Decimal; }

		private decimal Value;
		private bool Dirty;

		public bool Set(decimal val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadDecimal();
			Dirty = false;
		}

		public static implicit operator decimal(DistributedDecimal d) => d.Value;
	}

	public struct DistributedString : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.String; }

		private string Value;
		private bool Dirty;

		public bool Set(string val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
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

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
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
			
			Dirty = false;
		}

		public static implicit operator string(DistributedString d) => d.Value;
	}

	public struct DistributedChar : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Char; }

		private char Value;
		private bool Dirty;

		public bool Set(char val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
			w.Write(Value);
		}

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			Value = r.ReadChar();
			Dirty = false;
		}

		public static implicit operator char(DistributedChar d) => d.Value;
	}

	public struct DistributedBlob : IDistributedProperty
	{
		GameStateEntityPropertyValueType IDistributedProperty.ValueType { get => GameStateEntityPropertyValueType.Blob; }

		private byte[] Value;
		private bool Dirty;

		public bool Set(byte[] val)
		{
			if (Value == val)
			{
				return Dirty;
			}

			Value = val;
			Dirty = true;
			return true;
		}

		uint IDistributedProperty.IndexInObject { get; set; }

		void IDistributedProperty.WriteChangesTo(BinaryWriter w)
		{
			Dirty = false;
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

		void IDistributedProperty.ReadChangesFrom(BinaryReader r)
		{
			bool hasValue = r.ReadBoolean();
			if(hasValue)
            {
				int count = r.ReadUInt16();
				Value = r.ReadBytes(count);
			}
			else
            {
				Value = null;
            }
			
			Dirty = false;
		}

		public static implicit operator byte[](DistributedBlob d) => d.Value;
	}
	*/

	// ---- Arrays

}