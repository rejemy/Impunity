// ───────── Unity Type Serializer Tests (Edit Mode) ─────────
//
// Round-trips the Unity-typed serializers from Impunity.Unity (Client/Unity/DistributedUnitySerializers.cs)
// through both their binary (payload + FramingSerializer framing) and BSON (ToBsonValue/FromBsonValue) paths. These types
// require UnityEngine, so they can only be covered here — the shared dotnet suite covers the same
// serializer machinery via the portable TestVec3 stand-in.
//
// The two Bson tests at the bottom were moved from the old ImpunityBsonSerializationTests.cs.

using System.IO;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.Unity;

using UnityEngine;

public class UnitySerializerTests
{
	// ───────── Helpers ─────────

	static T BinaryRoundTrip<T, S>(S ser, T value) where S : IDistributableValueSerializer<T>
	{
		using var ms = new MemoryStream();
		var w = new BinaryWriter(ms);
		FramingSerializer.Write(ser, value, w);
		ms.Position = 0;
		return FramingSerializer.Read<T, S>(ser, new BinaryReader(ms));
	}

	static T BsonRoundTrip<T, S>(S ser, T value) where S : IDistributableValueSerializer<T>
		=> ser.FromBsonValue(ser.ToBsonValue(value));

	// ───────── Binary round-trips ─────────

	[Test, Category("UnitySerializers")]
	public void UnityVector_Binary_RoundTrips()
	{
		Assert.AreEqual(new Vector2(1.5f, 2.25f), BinaryRoundTrip<Vector2, Vector2Serializer>(new Vector2Serializer(), new Vector2(1.5f, 2.25f)));
		Assert.AreEqual(new Vector3(1.5f, 2.25f, -3.75f), BinaryRoundTrip<Vector3, Vector3Serializer>(new Vector3Serializer(), new Vector3(1.5f, 2.25f, -3.75f)));
		Assert.AreEqual(new Vector4(1f, 2f, 3f, 4f), BinaryRoundTrip<Vector4, DVector4Serializer>(new DVector4Serializer(), new Vector4(1f, 2f, 3f, 4f)));
		Assert.AreEqual(new Vector2Int(3, -7), BinaryRoundTrip<Vector2Int, Vector2IntSerializer>(new Vector2IntSerializer(), new Vector2Int(3, -7)));
		Assert.AreEqual(new Vector3Int(3, -7, 11), BinaryRoundTrip<Vector3Int, Vector3IntSerializer>(new Vector3IntSerializer(), new Vector3Int(3, -7, 11)));
	}

	[Test, Category("UnitySerializers")]
	public void UnityColorQuatMatrix_Binary_RoundTrips()
	{
		Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), BinaryRoundTrip<Color, ColorSerializer>(new ColorSerializer(), new Color(0.1f, 0.2f, 0.3f, 0.4f)));
		Assert.AreEqual(new Color32(10, 20, 30, 40), BinaryRoundTrip<Color32, Color32Serializer>(new Color32Serializer(), new Color32(10, 20, 30, 40)));
		Assert.AreEqual(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), BinaryRoundTrip<Quaternion, QuaternionSerializer>(new QuaternionSerializer(), new Quaternion(0.1f, 0.2f, 0.3f, 0.4f)));

		var m = Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.identity, new Vector3(2, 2, 2));
		Assert.AreEqual(m, BinaryRoundTrip<Matrix4x4, Matrix4x4Serializer>(new Matrix4x4Serializer(), m));
	}

	// ───────── BSON round-trips (moved from ImpunityBsonSerializationTests) ─────────

	[Test, Category("BsonUnitySerializers")]
	public void UnityVector_Bson_RoundTrips()
	{
		Assert.AreEqual(new Vector2(1.5f, 2.25f), BsonRoundTrip<Vector2, Vector2Serializer>(new Vector2Serializer(), new Vector2(1.5f, 2.25f)));
		Assert.AreEqual(new Vector3(1.5f, 2.25f, -3.75f), BsonRoundTrip<Vector3, Vector3Serializer>(new Vector3Serializer(), new Vector3(1.5f, 2.25f, -3.75f)));
		// Regression: Vector4 read used vect[4] (out of range) and the implicit float cast.
		Assert.AreEqual(new Vector4(1f, 2f, 3f, 4f), BsonRoundTrip<Vector4, DVector4Serializer>(new DVector4Serializer(), new Vector4(1f, 2f, 3f, 4f)));
		Assert.AreEqual(new Vector2Int(3, -7), BsonRoundTrip<Vector2Int, Vector2IntSerializer>(new Vector2IntSerializer(), new Vector2Int(3, -7)));
		Assert.AreEqual(new Vector3Int(3, -7, 11), BsonRoundTrip<Vector3Int, Vector3IntSerializer>(new Vector3IntSerializer(), new Vector3Int(3, -7, 11)));
	}

	[Test, Category("BsonUnitySerializers")]
	public void UnityColorQuatMatrix_Bson_RoundTrips()
	{
		Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), BsonRoundTrip<Color, ColorSerializer>(new ColorSerializer(), new Color(0.1f, 0.2f, 0.3f, 0.4f)));
		Assert.AreEqual(new Color32(10, 20, 30, 40), BsonRoundTrip<Color32, Color32Serializer>(new Color32Serializer(), new Color32(10, 20, 30, 40)));
		Assert.AreEqual(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), BsonRoundTrip<Quaternion, QuaternionSerializer>(new QuaternionSerializer(), new Quaternion(0.1f, 0.2f, 0.3f, 0.4f)));

		var m = Matrix4x4.TRS(new Vector3(1, 2, 3), Quaternion.identity, new Vector3(2, 2, 2));
		Assert.AreEqual(m, BsonRoundTrip<Matrix4x4, Matrix4x4Serializer>(new Matrix4x4Serializer(), m));
	}
}
