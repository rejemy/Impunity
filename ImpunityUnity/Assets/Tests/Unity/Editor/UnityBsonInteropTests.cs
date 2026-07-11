// ───────── Unity-Typed Entity BSON Interop Tests (Edit Mode) ─────────
//
// The Unity-side counterpart of the shared BsonSerializationTests: proves that entity fields using
// Unity-typed serializers (Vector3/Quaternion/Color) survive the persisted-field BSON path and are
// classified correctly in the field schema. (The shared suite proves the same machinery with the
// portable TestVec3; this proves the actual Unity serializers plug into it.)

using System.Linq;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.Unity;

using UltraLiteDB;
using UnityEngine;

[DistributedEntity(60, PersistAs = "uent")]
public partial class UnityBsonTestEntity : DistributedObjectBase
{
	[Distributed(1, PersistAs = "pos")] public DistributedValue<Vector3, Vector3Serializer> Position;
	[Distributed(2, PersistAs = "rot")] public DistributedValue<Quaternion, QuaternionSerializer> Rotation;
	[Distributed(3, PersistAs = "tint")] public DistributedValue<Color, ColorSerializer> Tint;
}

public class UnityBsonInteropTests
{
	static ClientEntityManager MakeManager()
	{
		var em = new ClientEntityManager();
		em.RegisterEntityTypes(new[] { typeof(UnityBsonTestEntity) });
		return em;
	}

	static UnityBsonTestEntity MakeEntity()
	{
		// Connection-less, client-authoritative: the editor usage pattern (see BsonSerializationTests).
		return new UnityBsonTestEntity { IsClientAuthoritative = true };
	}

	[Test, Category("BsonUnityInterop")]
	public void Manager_UnityFieldsRoundTrip()
	{
		var em = MakeManager();
		var src = MakeEntity();
		src.Position.Set(new Vector3(1.5f, 2.25f, -3.75f));
		src.Rotation.Set(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f));
		src.Tint.Set(new Color(0.1f, 0.2f, 0.3f, 0.4f));

		BsonDocument doc = em.GetPersistedFieldsAsBson(src);
		var dst = MakeEntity();
		em.ApplyPersistedFieldsFromBson(dst, doc);

		Assert.AreEqual(new Vector3(1.5f, 2.25f, -3.75f), dst.Position.Get());
		Assert.AreEqual(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), dst.Rotation.Get());
		Assert.AreEqual(new Color(0.1f, 0.2f, 0.3f, 0.4f), dst.Tint.Get());
	}

	[Test, Category("BsonUnityInterop")]
	public void GetFieldSchema_ClassifiesUnityTypes()
	{
		var em = MakeManager();
		var byId = em.GetFieldSchema(typeof(UnityBsonTestEntity)).ToDictionary(f => f.FieldId);

		Assert.AreEqual(3, byId.Count);
		Assert.AreEqual("pos", byId[1].PersistAs);
		Assert.AreEqual(GameStateEntityPropertyValueType.CustomSmall, byId[1].ValueType);
		Assert.AreEqual(typeof(Vector3), byId[1].ValueClrType);
		Assert.AreEqual(typeof(Quaternion), byId[2].ValueClrType);
		Assert.AreEqual(typeof(Color), byId[3].ValueClrType);
	}
}
