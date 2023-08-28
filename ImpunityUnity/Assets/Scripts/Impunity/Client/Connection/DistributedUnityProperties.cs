
using System.IO;


using UnityEngine;

namespace Impunity.GameState
{
	public struct DVector2 : IDistributableValueType
	{
		private Vector2 Value;

		public DVector2(Vector2 v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)8);
			w.Write(Value.x);
			w.Write(Value.y);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.x = r.ReadSingle();
			Value.y = r.ReadSingle();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Vector2(DVector2 d) => d.Value;
		public static implicit operator DVector2(Vector2 d) => new DVector2(d);
	}

	public struct DVector3 : IDistributableValueType
	{
		private Vector3 Value;

		public DVector3(Vector3 v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)12);
			w.Write(Value.x);
			w.Write(Value.y);
			w.Write(Value.z);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.x = r.ReadSingle();
			Value.y = r.ReadSingle();
			Value.z = r.ReadSingle();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Vector3(DVector3 d) => d.Value;
		public static implicit operator DVector3(Vector3 d) => new DVector3(d);
	}

	public struct DVector4 : IDistributableValueType
	{
		private Vector4 Value;

		public DVector4(Vector4 v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)16);
			w.Write(Value.x);
			w.Write(Value.y);
			w.Write(Value.z);
			w.Write(Value.w);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.x = r.ReadSingle();
			Value.y = r.ReadSingle();
			Value.z = r.ReadSingle();
			Value.w = r.ReadSingle();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Vector4(DVector4 d) => d.Value;
		public static implicit operator DVector4(Vector4 d) => new DVector4(d);
	}

	public struct DVector2Int : IDistributableValueType
	{
		private Vector2Int Value;

		public DVector2Int(Vector2Int v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)8);
			w.Write(Value.x);
			w.Write(Value.y);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.x = r.ReadInt32();
			Value.y = r.ReadInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Vector2Int(DVector2Int d) => d.Value;
		public static implicit operator DVector2Int(Vector2Int d) => new DVector2Int(d);
	}

	public struct DVector3Int : IDistributableValueType
	{
		private Vector3Int Value;

		public DVector3Int(Vector3Int v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)12);
			w.Write(Value.x);
			w.Write(Value.y);
			w.Write(Value.z);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.x = r.ReadInt32();
			Value.y = r.ReadInt32();
			Value.z = r.ReadInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Vector3Int(DVector3Int d) => d.Value;
		public static implicit operator DVector3Int(Vector3Int d) => new DVector3Int(d);
	}

	public struct DColor : IDistributableValueType
	{
		private Color Value;

		public DColor(Color v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)16);
			w.Write(Value.r);
			w.Write(Value.g);
			w.Write(Value.b);
			w.Write(Value.a);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.r = r.ReadInt32();
			Value.g = r.ReadInt32();
			Value.b = r.ReadInt32();
			Value.a = r.ReadInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Color(DColor d) => d.Value;
		public static implicit operator DColor(Color d) => new DColor(d);
	}

	public struct DColor32 : IDistributableValueType
	{
		private Color32 Value;

		public DColor32(Color32 v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)4);
			w.Write(Value.r);
			w.Write(Value.g);
			w.Write(Value.b);
			w.Write(Value.a);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.r = r.ReadByte();
			Value.g = r.ReadByte();
			Value.b = r.ReadByte();
			Value.a = r.ReadByte();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Color32(DColor32 d) => d.Value;
		public static implicit operator DColor32(Color32 d) => new DColor32(d);
	}

	public struct DMatrix4x4 : IDistributableValueType
	{
		private Matrix4x4 Value;

		public DMatrix4x4(Matrix4x4 v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)64);
			for(int i=0; i<16; i++)
            {
				w.Write(Value[i]);
			}
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			for (int i = 0; i < 16; i++)
			{
				Value[i] = r.ReadSingle();
			}
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Matrix4x4(DMatrix4x4 d) => d.Value;
		public static implicit operator DMatrix4x4(Matrix4x4 d) => new DMatrix4x4(d);
	}

	public struct DQuaternion : IDistributableValueType
	{
		private Quaternion Value;

		public DQuaternion(Quaternion v)
		{
			Value = v;
		}

		public void WriteChangesTo(BinaryWriter w)
		{
			w.Write((byte)16);
			w.Write(Value.x);
			w.Write(Value.y);
			w.Write(Value.z);
			w.Write(Value.w);
		}

		public void ReadChangesFrom(BinaryReader r)
		{
			r.ReadByte();
			Value.x = r.ReadInt32();
			Value.y = r.ReadInt32();
			Value.z = r.ReadInt32();
			Value.w = r.ReadInt32();
		}

		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public static implicit operator Quaternion(DQuaternion d) => d.Value;
		public static implicit operator DQuaternion(Quaternion d) => new DQuaternion(d);
	}
}