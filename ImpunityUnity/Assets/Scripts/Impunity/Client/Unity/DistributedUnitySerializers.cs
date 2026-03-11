using System;
using System.IO;
using Impunity.Connection;
using UnityEngine;

namespace Impunity.Unity
{
	/// <summary>Binary serializer for Unity <see cref="Vector2"/> (8 bytes: 2 floats).</summary>
	public readonly struct Vector2Serializer : IDistributableValueSerializer<Vector2>
	{

		public void WriteTo(Vector2 value, BinaryWriter w)
		{
			w.Write((byte)8);
			w.Write(value.x);
			w.Write(value.y);
		}

		public Vector2 ReadFrom(BinaryReader r)
		{
			Vector2 value = new Vector2();
			if(r.ReadByte() == 0)
            {
				return value;
            }
			
			value.x = r.ReadSingle();
			value.y = r.ReadSingle();

			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector3"/> (12 bytes: 3 floats).</summary>
	public readonly struct Vector3Serializer : IDistributableValueSerializer<Vector3>
	{

		public void WriteTo(Vector3 value, BinaryWriter w)
		{
			w.Write((byte)12);
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
		}

		public Vector3 ReadFrom(BinaryReader r)
		{
			Vector3 value = new Vector3();
			if (r.ReadByte() == 0)
			{
				return value;
			}

			value.x = r.ReadSingle();
			value.y = r.ReadSingle();
			value.z = r.ReadSingle();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector4"/> (16 bytes: 4 floats).</summary>
	public readonly struct DVector4Serializer : IDistributableValueSerializer<Vector4>
	{
		public void WriteTo(Vector4 value, BinaryWriter w)
		{
			w.Write((byte)16);
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
			w.Write(value.w);
		}

		public Vector4 ReadFrom(BinaryReader r)
		{
			Vector4 value = new Vector4();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			
			value.x = r.ReadSingle();
			value.y = r.ReadSingle();
			value.z = r.ReadSingle();
			value.w = r.ReadSingle();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector2Int"/> (8 bytes: 2 ints).</summary>
	public readonly struct Vector2IntSerializer : IDistributableValueSerializer<Vector2Int>
	{
		public void WriteTo(Vector2Int value, BinaryWriter w)
		{
			w.Write((byte)8);
			w.Write(value.x);
			w.Write(value.y);
		}

		public Vector2Int ReadFrom(BinaryReader r)
		{
			Vector2Int value = new Vector2Int();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			
			value.x = r.ReadInt32();
			value.y = r.ReadInt32();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector3Int"/> (12 bytes: 3 ints).</summary>
	public readonly struct Vector3IntSerializer : IDistributableValueSerializer<Vector3Int>
	{
		public void WriteTo(Vector3Int value, BinaryWriter w)
		{
			w.Write((byte)12);
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
		}

		public Vector3Int ReadFrom(BinaryReader r)
		{
			Vector3Int value = new Vector3Int();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			value.x = r.ReadInt32();
			value.y = r.ReadInt32();
			value.z = r.ReadInt32();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Color"/> (16 bytes: 4 floats for RGBA).</summary>
	public readonly struct ColorSerializer : IDistributableValueSerializer<Color>
	{
		public void WriteTo(Color value, BinaryWriter w)
		{
			w.Write((byte)16);
			w.Write(value.r);
			w.Write(value.g);
			w.Write(value.b);
			w.Write(value.a);
		}

		public Color ReadFrom(BinaryReader r)
		{
			Color value = new Color();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			value.r = r.ReadInt32();
			value.g = r.ReadInt32();
			value.b = r.ReadInt32();
			value.a = r.ReadInt32();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Color32"/> (4 bytes: 4 bytes for RGBA).</summary>
	public readonly struct Color32Serializer : IDistributableValueSerializer<Color32>
	{
		public void WriteTo(Color32 value, BinaryWriter w)
		{
			w.Write((byte)4);
			w.Write(value.r);
			w.Write(value.g);
			w.Write(value.b);
			w.Write(value.a);
		}

		public Color32 ReadFrom(BinaryReader r)
		{
			Color32 value = new Color32();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			value.r = r.ReadByte();
			value.g = r.ReadByte();
			value.b = r.ReadByte();
			value.a = r.ReadByte();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Matrix4x4"/> (64 bytes: 16 floats).</summary>
	public readonly struct Matrix4x4Serializer : IDistributableValueSerializer<Matrix4x4>
	{
		public void WriteTo(Matrix4x4 value, BinaryWriter w)
		{
			w.Write((byte)64);
			for(int i=0; i<16; i++)
            {
				w.Write(value[i]);
			}
		}

		public Matrix4x4 ReadFrom(BinaryReader r)
		{
			Matrix4x4 value = new Matrix4x4();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			for (int i = 0; i < 16; i++)
			{
				value[i] = r.ReadSingle();
			}
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Quaternion"/> (16 bytes: 4 floats for XYZW).</summary>
	public readonly struct QuaternionSerializer : IDistributableValueSerializer<Quaternion>
	{
		public void WriteTo(Quaternion value, BinaryWriter w)
		{
			w.Write((byte)16);
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
			w.Write(value.w);
		}

		public Quaternion ReadFrom(BinaryReader r)
		{
			Quaternion value = new Quaternion();
			if (r.ReadByte() == 0)
			{
				return value;
			}
			value.x = r.ReadInt32();
			value.y = r.ReadInt32();
			value.z = r.ReadInt32();
			value.w = r.ReadInt32();
			return value;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}
}