using System;
using System.IO;
using Impunity.Connection;
using UltraLiteDB;
using UnityEngine;

namespace Impunity.Unity
{
	/// <summary>Binary serializer for Unity <see cref="Vector2"/> (8 bytes: 2 floats).</summary>
	public readonly struct Vector2Serializer : IDistributableValueSerializer<Vector2>, ICustomPayloadSerializer<Vector2>
	{
		public void WriteTo(Vector2 value, BinaryWriter w) => default(CustomSmallSerializer<Vector2, Vector2Serializer>).WriteTo(value, w);
		public Vector2 ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Vector2, Vector2Serializer>).ReadFrom(r);

		public void WritePayload(Vector2 value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
		}

		public Vector2 ReadPayload(BinaryReader r, int byteCount)
		{
			Vector2 value = new Vector2();
			value.x = r.ReadSingle();
			value.y = r.ReadSingle();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Vector2 value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Vector2 FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Vector2(vect[0].AsSingle, vect[1].AsSingle);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector3"/> (12 bytes: 3 floats).</summary>
	public readonly struct Vector3Serializer : IDistributableValueSerializer<Vector3>, ICustomPayloadSerializer<Vector3>
	{
		public void WriteTo(Vector3 value, BinaryWriter w) => default(CustomSmallSerializer<Vector3, Vector3Serializer>).WriteTo(value, w);
		public Vector3 ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Vector3, Vector3Serializer>).ReadFrom(r);

		public void WritePayload(Vector3 value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
		}

		public Vector3 ReadPayload(BinaryReader r, int byteCount)
		{
			Vector3 value = new Vector3();
			value.x = r.ReadSingle();
			value.y = r.ReadSingle();
			value.z = r.ReadSingle();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Vector3 value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y,
				value.z
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Vector3 FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Vector3(vect[0].AsSingle, vect[1].AsSingle, vect[2].AsSingle);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector4"/> (16 bytes: 4 floats).</summary>
	public readonly struct DVector4Serializer : IDistributableValueSerializer<Vector4>, ICustomPayloadSerializer<Vector4>
	{
		public void WriteTo(Vector4 value, BinaryWriter w) => default(CustomSmallSerializer<Vector4, DVector4Serializer>).WriteTo(value, w);
		public Vector4 ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Vector4, DVector4Serializer>).ReadFrom(r);

		public void WritePayload(Vector4 value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
			w.Write(value.w);
		}

		public Vector4 ReadPayload(BinaryReader r, int byteCount)
		{
			Vector4 value = new Vector4();
			value.x = r.ReadSingle();
			value.y = r.ReadSingle();
			value.z = r.ReadSingle();
			value.w = r.ReadSingle();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Vector4 value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y,
				value.z,
				value.w
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Vector4 FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Vector4(vect[0].AsSingle, vect[1].AsSingle, vect[2].AsSingle, vect[3].AsSingle);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector2Int"/> (8 bytes: 2 ints).</summary>
	public readonly struct Vector2IntSerializer : IDistributableValueSerializer<Vector2Int>, ICustomPayloadSerializer<Vector2Int>
	{
		public void WriteTo(Vector2Int value, BinaryWriter w) => default(CustomSmallSerializer<Vector2Int, Vector2IntSerializer>).WriteTo(value, w);
		public Vector2Int ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Vector2Int, Vector2IntSerializer>).ReadFrom(r);

		public void WritePayload(Vector2Int value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
		}

		public Vector2Int ReadPayload(BinaryReader r, int byteCount)
		{
			Vector2Int value = new Vector2Int();
			value.x = r.ReadInt32();
			value.y = r.ReadInt32();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Vector2Int value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Vector2Int FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Vector2Int(vect[0], vect[1]);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Vector3Int"/> (12 bytes: 3 ints).</summary>
	public readonly struct Vector3IntSerializer : IDistributableValueSerializer<Vector3Int>, ICustomPayloadSerializer<Vector3Int>
	{
		public void WriteTo(Vector3Int value, BinaryWriter w) => default(CustomSmallSerializer<Vector3Int, Vector3IntSerializer>).WriteTo(value, w);
		public Vector3Int ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Vector3Int, Vector3IntSerializer>).ReadFrom(r);

		public void WritePayload(Vector3Int value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
		}

		public Vector3Int ReadPayload(BinaryReader r, int byteCount)
		{
			Vector3Int value = new Vector3Int();
			value.x = r.ReadInt32();
			value.y = r.ReadInt32();
			value.z = r.ReadInt32();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Vector3Int value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y,
				value.z
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Vector3Int FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Vector3Int(vect[0], vect[1], vect[2]);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Color"/> (16 bytes: 4 floats for RGBA).</summary>
	public readonly struct ColorSerializer : IDistributableValueSerializer<Color>, ICustomPayloadSerializer<Color>
	{
		public void WriteTo(Color value, BinaryWriter w) => default(CustomSmallSerializer<Color, ColorSerializer>).WriteTo(value, w);
		public Color ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Color, ColorSerializer>).ReadFrom(r);

		public void WritePayload(Color value, BinaryWriter w)
		{
			w.Write(value.r);
			w.Write(value.g);
			w.Write(value.b);
			w.Write(value.a);
		}

		public Color ReadPayload(BinaryReader r, int byteCount)
		{
			Color value = new Color();
			value.r = r.ReadSingle();
			value.g = r.ReadSingle();
			value.b = r.ReadSingle();
			value.a = r.ReadSingle();
			return value;
		}


		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Color value)
		{
			BsonArray vect = new BsonArray
			{
				value.r,
				value.g,
				value.b,
				value.a
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Color FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Color(vect[0].AsSingle, vect[1].AsSingle, vect[2].AsSingle, vect[3].AsSingle);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Color32"/> (4 bytes: 4 bytes for RGBA).</summary>
	public readonly struct Color32Serializer : IDistributableValueSerializer<Color32>, ICustomPayloadSerializer<Color32>
	{
		public void WriteTo(Color32 value, BinaryWriter w) => default(CustomSmallSerializer<Color32, Color32Serializer>).WriteTo(value, w);
		public Color32 ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Color32, Color32Serializer>).ReadFrom(r);

		public void WritePayload(Color32 value, BinaryWriter w)
		{
			w.Write(value.r);
			w.Write(value.g);
			w.Write(value.b);
			w.Write(value.a);
		}

		public Color32 ReadPayload(BinaryReader r, int byteCount)
		{
			Color32 value = new Color32();
			value.r = r.ReadByte();
			value.g = r.ReadByte();
			value.b = r.ReadByte();
			value.a = r.ReadByte();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Color32 value)
		{
			BsonArray vect = new BsonArray
			{
				(int)value.r,
				(int)value.g,
				(int)value.b,
				(int)value.a
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Color32 FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Color32((byte)vect[0].AsInt32, (byte)vect[1].AsInt32, (byte)vect[2].AsInt32, (byte)vect[3].AsInt32);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Matrix4x4"/> (64 bytes: 16 floats).</summary>
	public readonly struct Matrix4x4Serializer : IDistributableValueSerializer<Matrix4x4>, ICustomPayloadSerializer<Matrix4x4>
	{
		public void WriteTo(Matrix4x4 value, BinaryWriter w) => default(CustomSmallSerializer<Matrix4x4, Matrix4x4Serializer>).WriteTo(value, w);
		public Matrix4x4 ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Matrix4x4, Matrix4x4Serializer>).ReadFrom(r);

		public void WritePayload(Matrix4x4 value, BinaryWriter w)
		{
			for (int i = 0; i < 16; i++)
			{
				w.Write(value[i]);
			}
		}

		public Matrix4x4 ReadPayload(BinaryReader r, int byteCount)
		{
			Matrix4x4 value = new Matrix4x4();
			for (int i = 0; i < 16; i++)
			{
				value[i] = r.ReadSingle();
			}
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Matrix4x4 value)
		{
			BsonArray array = new BsonArray();
			for (int i = 0; i < 16; i++)
			{
				array.Add(value[i]);
			}
			return array;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Matrix4x4 FromBsonValue(BsonValue value)
		{
			BsonArray array = value.AsArray!;
			Matrix4x4 matrix = new Matrix4x4();

			for (int i = 0; i < 16; i++)
			{
				matrix[i] = array[i].AsSingle;
			}
			return matrix;
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}

	/// <summary>Binary serializer for Unity <see cref="Quaternion"/> (16 bytes: 4 floats for XYZW).</summary>
	public readonly struct QuaternionSerializer : IDistributableValueSerializer<Quaternion>, ICustomPayloadSerializer<Quaternion>
	{
		public void WriteTo(Quaternion value, BinaryWriter w) => default(CustomSmallSerializer<Quaternion, QuaternionSerializer>).WriteTo(value, w);
		public Quaternion ReadFrom(BinaryReader r) => default(CustomSmallSerializer<Quaternion, QuaternionSerializer>).ReadFrom(r);

		public void WritePayload(Quaternion value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
			w.Write(value.w);
		}

		public Quaternion ReadPayload(BinaryReader r, int byteCount)
		{
			Quaternion value = new Quaternion();
			value.x = r.ReadSingle();
			value.y = r.ReadSingle();
			value.z = r.ReadSingle();
			value.w = r.ReadSingle();
			return value;
		}

		/// <summary>Converts value to BsonValue</summary>
		public BsonValue ToBsonValue(Quaternion value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y,
				value.z,
				value.w
			};
			return vect;
		}

		/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
		public Quaternion FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray!;

			return new Quaternion(vect[0].AsSingle, vect[1].AsSingle, vect[2].AsSingle, vect[3].AsSingle);
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }
	}
}
