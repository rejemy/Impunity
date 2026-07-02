using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.Unity;

using UltraLiteDB;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// Test entity types for the persisted-field BSON path. These live inside the
// Tests assembly (not Assembly-CSharp) so the Impunity source generator emits the
// _imp_GetBsonValueWrapper_/_imp_SetFromBsonValueWrapper_ helpers for them and the
// Tests asmdef can reference them. Serializers come from the Impunity assembly.
// ─────────────────────────────────────────────────────────────────────────────

public static class BsonTestIds
{
	public const int ENTITY = 50;
	public const int SUBENTITY = 51;
	public const int EPHEMERAL_SUBENTITY = 52;
	public const int DUPKEY_ENTITY = 53;
}

/// <summary>Complex value type persisted via BsonSerializer (uses public fields → relies on the
/// mapper's IncludeFields, which ImpunityUtil.GetBsonMapper configures).</summary>
public class BsonTestPoco : IEquatable<BsonTestPoco>
{
	public int Number;
	public string Label;

	public bool Equals(BsonTestPoco other) => other != null && Number == other.Number && Label == other.Label;
	public override bool Equals(object obj) => Equals(obj as BsonTestPoco);
	public override int GetHashCode() => Number;
}

[DistributedEntity(BsonTestIds.ENTITY, PersistAs = "ent")]
public partial class BsonTestEntity : DistributedObjectBase
{
	[Distributed(1, PersistAs = "name")] public DistributedValue<string, StringSerializer> Name;
	[Distributed(2, PersistAs = "count")] public DistributedValue<int, Int32Serializer> Count;
	[Distributed(3, PersistAs = "ratio")] public DistributedValue<float, FloatSerializer> Ratio;
	[Distributed(4, PersistAs = "bigId")] public DistributedValue<ulong, UInt64Serializer> BigId;
	[Distributed(5, PersistAs = "when")] public DistributedValue<DateTimeOffset, DateTimeOffsetSerializer> When;
	[Distributed(6, PersistAs = "active")] public DistributedValue<bool, BoolSerializer> Active;

	// Not persisted (no PersistAs) — must be absent from the produced document.
	[Distributed(7)] public DistributedValue<int, Int32Serializer> Transient;
	// Temporal, not persisted — proves the persisted path skips temporal fields (no Connection needed).
	[Distributed(8)] public DistributedTemporalValue<int, Int32Serializer> Ephemeral;

	[Distributed(9, PersistAs = "scores")] public DistributedArray<int, Int32Serializer> Scores;
	[Distributed(10, PersistAs = "items")] public DistributedIntDictionary<string, StringSerializer> Items;
	[Distributed(11, PersistAs = "tags")] public DistributedStringDictionary<string, StringSerializer> Tags;
	[Distributed(12, PersistAs = "log")] public DistributedQueue<string, StringSerializer> Log;

	[Distributed(13, PersistAs = "data")] public DistributedValue<BsonTestPoco, BsonSerializer<BsonTestPoco>> Data;
	[Distributed(14, PersistAs = "pos")] public DistributedValue<Vector3, Vector3Serializer> Position;

}

[DistributedEntity(BsonTestIds.SUBENTITY, PersistAs = "subent")]
public partial class BsonTestSubEntity : BsonTestEntity
{
	[Distributed(20, PersistAs = "extra")] public DistributedValue<string, StringSerializer> Extra;
}

/// <summary>Non-persisted subclass of a persisted base — allowed; the inherited persisted fields become
/// replicated-only on this type.</summary>
[DistributedEntity(BsonTestIds.EPHEMERAL_SUBENTITY)]
public partial class BsonTestEphemeralSubEntity : BsonTestEntity
{
	[Distributed(21)] public DistributedValue<int, Int32Serializer> Runtime;
}

/// <summary>Shares BsonTestEntity's PersistAs key — only referenced by the duplicate-key registration test.</summary>
[DistributedEntity(BsonTestIds.DUPKEY_ENTITY, PersistAs = "ent")]
public partial class BsonTestDupKeyEntity : DistributedObjectBase
{
	[Distributed(1, PersistAs = "other")] public DistributedValue<int, Int32Serializer> Other;
}


public class ImpunityBsonSerializationTests
{
	// ───────── Helpers ─────────

	static T BsonRoundTrip<T, S>(S ser, T value) where S : IDistributableValueSerializer<T>
		=> ser.FromBsonValue(ser.ToBsonValue(value));

	static ClientEntityManager MakeManager()
	{
		var em = new ClientEntityManager();
		em.RegisterEntityTypes(new[] { typeof(BsonTestEntity), typeof(BsonTestSubEntity) });
		return em;
	}

	/// <summary>A connection-less, client-authoritative entity — the editor usage pattern. The base
	/// constructor resolves DistributedEntityType from the [DistributedEntity] attribute, so no manager
	/// or manual type id is needed. IsClientAuthoritative is required for field setters to populate
	/// CurrentValue, which is what the BSON path reads/writes.</summary>
	static T MakeEntity<T>() where T : IDistributedEntity, new()
	{
		return new T { IsClientAuthoritative = true };
	}

	static void PopulateBaseFields(BsonTestEntity e)
	{
		e.Name.Set("Alice");
		e.Count.Set(7);
		e.Ratio.Set(0.5f);
		e.BigId.Set(ulong.MaxValue);                                  // > long.MaxValue
		e.When.Set(new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.FromMinutes(330))); // +05:30
		e.Active.Set(true);
		e.Transient.Set(999);                                         // not persisted
		e.Scores.Replace(new List<int> { 10, 20, 30 });
		e.Items.Replace(new Dictionary<int, string> { { 1, "sword" }, { 2, "shield" } });
		e.Tags.Replace(new Dictionary<string, string> { { "color", "red" }, { "size", "L" } });
		e.Log.Replace(3, new List<string> { "hello", "world" });
		e.Data.Set(new BsonTestPoco { Number = 42, Label = "loot" });
		e.Position.Set(new Vector3(1.5f, 2.25f, -3.75f));
	}

	// ───────── 1. Serializer ToBsonValue / FromBsonValue round-trips ─────────

	[Test, Category("BsonSerializers")]
	public void Scalar_Bson_RoundTrips()
	{
		Assert.AreEqual(true, BsonRoundTrip<bool, BoolSerializer>(new BoolSerializer(), true));
		Assert.AreEqual((sbyte)-42, BsonRoundTrip<sbyte, Int8Serializer>(new Int8Serializer(), (sbyte)-42));
		Assert.AreEqual((byte)200, BsonRoundTrip<byte, UInt8Serializer>(new UInt8Serializer(), (byte)200));
		Assert.AreEqual((short)-12345, BsonRoundTrip<short, Int16Serializer>(new Int16Serializer(), (short)-12345));
		Assert.AreEqual((ushort)65535, BsonRoundTrip<ushort, UInt16Serializer>(new UInt16Serializer(), (ushort)65535));
		Assert.AreEqual(-123456, BsonRoundTrip<int, Int32Serializer>(new Int32Serializer(), -123456));
		Assert.AreEqual(long.MinValue, BsonRoundTrip<long, Int64Serializer>(new Int64Serializer(), long.MinValue));
		Assert.AreEqual(2.71828d, BsonRoundTrip<double, DoubleSerializer>(new DoubleSerializer(), 2.71828d));
		Assert.AreEqual(99999.99999m, BsonRoundTrip<decimal, DecimalSerializer>(new DecimalSerializer(), 99999.99999m));
		Assert.AreEqual('Z', BsonRoundTrip<char, CharSerializer>(new CharSerializer(), 'Z'));
		Assert.AreEqual("hello", BsonRoundTrip<string, StringSerializer>(new StringSerializer(), "hello"));
		Assert.IsNull(BsonRoundTrip<string, StringSerializer>(new StringSerializer(), null));
		var g = Guid.NewGuid();
		Assert.AreEqual(g, BsonRoundTrip<Guid, GuidSerializer>(new GuidSerializer(), g));
		var dt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
		Assert.AreEqual(dt, BsonRoundTrip<DateTime, DateTimeSerializer>(new DateTimeSerializer(), dt));
		Assert.AreEqual(TimeSpan.FromHours(3.5), BsonRoundTrip<TimeSpan, TimeSpanSerializer>(new TimeSpanSerializer(), TimeSpan.FromHours(3.5)));
	}

	[Test, Category("BsonSerializers")]
	public void Float_Bson_RoundTrip()
	{
		// Regression: floats are stored as BSON Double; the read path must use AsSingle, not the
		// implicit BsonValue->float cast (which throws InvalidCastException on a boxed Double).
		Assert.AreEqual(3.14f, BsonRoundTrip<float, FloatSerializer>(new FloatSerializer(), 3.14f));
		Assert.AreEqual(-0.001f, BsonRoundTrip<float, FloatSerializer>(new FloatSerializer(), -0.001f));
	}

	[Test, Category("BsonSerializers")]
	public void ULong_Bson_RoundTrip_AboveLongRange()
	{
		// Regression: ulong must round-trip via the Int64 bit pattern, not ulong->double.
		var ser = new UInt64Serializer();
		Assert.AreEqual(0UL, BsonRoundTrip<ulong, UInt64Serializer>(ser, 0UL));
		Assert.AreEqual(42UL, BsonRoundTrip<ulong, UInt64Serializer>(ser, 42UL));
		Assert.AreEqual((ulong)long.MaxValue, BsonRoundTrip<ulong, UInt64Serializer>(ser, (ulong)long.MaxValue));
		Assert.AreEqual(ulong.MaxValue, BsonRoundTrip<ulong, UInt64Serializer>(ser, ulong.MaxValue));
		Assert.AreEqual(12345678901234567890UL, BsonRoundTrip<ulong, UInt64Serializer>(ser, 12345678901234567890UL));
	}

	[Test, Category("BsonSerializers")]
	public void DateTimeOffset_Bson_PreservesOffset()
	{
		// Regression: the offset must be encoded as TotalMinutes, not Minutes, or +05:30 would
		// come back as +00:30.
		var ser = new DateTimeOffsetSerializer();
		foreach (var off in new[] { TimeSpan.FromMinutes(330), TimeSpan.FromMinutes(-330), TimeSpan.FromMinutes(345), TimeSpan.Zero })
		{
			var dto = new DateTimeOffset(2025, 6, 15, 10, 30, 0, off);
			var back = BsonRoundTrip<DateTimeOffset, DateTimeOffsetSerializer>(ser, dto);
			Assert.AreEqual(dto.Ticks, back.Ticks, "ticks for offset " + off);
			Assert.AreEqual(dto.Offset, back.Offset, "offset for " + off);
		}
	}

	[Test, Category("BsonSerializers")]
	public void Blob_Bson_RoundTrip()
	{
		var data = new ArraySegment<byte>(new byte[] { 1, 2, 3, 250 });
		var back = BsonRoundTrip<ArraySegment<byte>, BlobSerializer>(new BlobSerializer(), data);
		Assert.AreEqual(data.Count, back.Count);
		for (int i = 0; i < data.Count; i++) Assert.AreEqual(data.Array[i], back.Array[i]);

		var nul = BsonRoundTrip<ArraySegment<byte>, BlobSerializer>(new BlobSerializer(), default);
		Assert.IsNull(nul.Array);
	}

	[Test, Category("BsonSerializers")]
	public void Complex_Bson_RoundTrip_IsDocument()
	{
		var ser = new BsonSerializer<BsonTestPoco>();
		var obj = new BsonTestPoco { Number = 99, Label = "big" };
		BsonValue bv = ser.ToBsonValue(obj);
		Assert.AreEqual(BsonType.Document, bv.Type, "complex value should be a readable nested document, not Binary");
		Assert.AreEqual(obj, ser.FromBsonValue(bv));
	}

	// ───────── 2. Unity serializer round-trips ─────────

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

	// ───────── 3. Manager: GetPersistedFieldsAsBson / ApplyPersistedFieldsFromBson ─────────

	[Test, Category("BsonManager")]
	public void Manager_WorksWithNoConnection()
	{
		var em = MakeManager();
		Assert.IsNull(em.Connection, "test exercises the connection-less path");

		var src = MakeEntity<BsonTestEntity>();
		PopulateBaseFields(src);

		Assert.DoesNotThrow(() => em.GetPersistedFieldsAsBson(src));
	}

	[Test, Category("BsonManager")]
	public void Manager_DocumentContainsOnlyPersistedKeys()
	{
		var em = MakeManager();
		var src = MakeEntity<BsonTestEntity>();
		PopulateBaseFields(src);

		BsonDocument doc = em.GetPersistedFieldsAsBson(src);

		CollectionAssert.AreEquivalent(
			new[] { "name", "count", "ratio", "bigId", "when", "active", "scores", "items", "tags", "log", "data", "pos" },
			new List<string>(doc.Keys));

		// Non-persisted and temporal fields must not leak in. (BsonDocument keys are
		// case-insensitive, so we probe on the field names — which have no case-collision with any
		// persisted key — rather than e.g. "Count", which would match the persisted "count".)
		Assert.IsFalse(doc.ContainsKey("Transient"));
		Assert.IsFalse(doc.ContainsKey("Ephemeral"));
	}

	[Test, Category("BsonManager")]
	public void Manager_ScalarsRoundTrip()
	{
		var em = MakeManager();
		var src = MakeEntity<BsonTestEntity>();
		PopulateBaseFields(src);

		BsonDocument doc = em.GetPersistedFieldsAsBson(src);
		var dst = MakeEntity<BsonTestEntity>();
		em.ApplyPersistedFieldsFromBson(dst, doc);

		Assert.AreEqual("Alice", dst.Name.Get());
		Assert.AreEqual(7, dst.Count.Get());
		Assert.AreEqual(0.5f, dst.Ratio.Get());
		Assert.AreEqual(ulong.MaxValue, dst.BigId.Get());
		Assert.AreEqual(true, dst.Active.Get());

		var when = dst.When.Get();
		Assert.AreEqual(src.When.Get().Ticks, when.Ticks);
		Assert.AreEqual(TimeSpan.FromMinutes(330), when.Offset);

		// Non-persisted field is not transferred.
		Assert.AreEqual(0, dst.Transient.Get());
	}

	[Test, Category("BsonManager")]
	public void Manager_CollectionsRoundTrip()
	{
		var em = MakeManager();
		var src = MakeEntity<BsonTestEntity>();
		PopulateBaseFields(src);

		BsonDocument doc = em.GetPersistedFieldsAsBson(src);
		var dst = MakeEntity<BsonTestEntity>();
		em.ApplyPersistedFieldsFromBson(dst, doc);

		// Array
		Assert.AreEqual(3, dst.Scores.Count);
		Assert.AreEqual(10, dst.Scores[0]);
		Assert.AreEqual(20, dst.Scores[1]);
		Assert.AreEqual(30, dst.Scores[2]);

		// Int dictionary (int key, string value)
		Assert.AreEqual(2, dst.Items.Count);
		Assert.AreEqual("sword", dst.Items[1]);
		Assert.AreEqual("shield", dst.Items[2]);

		// String dictionary
		Assert.AreEqual("red", dst.Tags["color"]);
		Assert.AreEqual("L", dst.Tags["size"]);

		// Queue (use the implicit Queue<T> conversion; the struct's own GetEnumerator self-recurses).
		Queue<string> log = dst.Log;
		Assert.AreEqual(new[] { "hello", "world" }, log.ToArray());
	}

	[Test, Category("BsonManager")]
	public void Manager_ComplexAndUnityFieldsRoundTrip()
	{
		var em = MakeManager();
		var src = MakeEntity<BsonTestEntity>();
		PopulateBaseFields(src);

		BsonDocument doc = em.GetPersistedFieldsAsBson(src);
		Assert.AreEqual(BsonType.Document, doc["data"].Type, "complex field persists as a readable document");

		var dst = MakeEntity<BsonTestEntity>();
		em.ApplyPersistedFieldsFromBson(dst, doc);

		Assert.AreEqual(new BsonTestPoco { Number = 42, Label = "loot" }, dst.Data.Get());
		Assert.AreEqual(new Vector3(1.5f, 2.25f, -3.75f), dst.Position.Get());
	}

	[Test, Category("BsonManager")]
	public void Manager_InheritedFieldsRoundTrip()
	{
		var em = MakeManager();
		var src = MakeEntity<BsonTestSubEntity>();
		PopulateBaseFields(src);
		src.Extra.Set("derived-only");

		BsonDocument doc = em.GetPersistedFieldsAsBson(src);
		Assert.IsTrue(doc.ContainsKey("extra"), "subclass field present");
		Assert.IsTrue(doc.ContainsKey("name"), "inherited base field present");

		var dst = MakeEntity<BsonTestSubEntity>();
		em.ApplyPersistedFieldsFromBson(dst, doc);

		Assert.AreEqual("derived-only", dst.Extra.Get());
		Assert.AreEqual("Alice", dst.Name.Get());     // inherited
		Assert.AreEqual(7, dst.Count.Get());          // inherited
	}

	[Test, Category("BsonManager")]
	public void Manager_PartialDocument_LeavesMissingFieldsDefault()
	{
		var em = MakeManager();
		var dst = MakeEntity<BsonTestEntity>();

		var doc = new BsonDocument();
		doc["name"] = "OnlyName";
		// "count" deliberately absent; an explicit null must also be ignored.
		doc["ratio"] = BsonValue.Null;

		Assert.DoesNotThrow(() => em.ApplyPersistedFieldsFromBson(dst, doc));
		Assert.AreEqual("OnlyName", dst.Name.Get());
		Assert.AreEqual(0, dst.Count.Get());
		Assert.AreEqual(0f, dst.Ratio.Get());
	}

	[Test, Category("BsonManager")]
	public void Entity_ResolvesTypeIdAtConstruction()
	{
		// The base constructor pulls the id from [DistributedEntity]; no manager or manual set needed.
		Assert.AreEqual(BsonTestIds.ENTITY, new BsonTestEntity().DistributedEntityType);
		Assert.AreEqual(BsonTestIds.SUBENTITY, new BsonTestSubEntity().DistributedEntityType);
	}

	[Test, Category("BsonManager")]
	public void Manager_UnregisteredType_ThrowsClearError()
	{
		var em = MakeManager();
		var e = new BsonTestEntity { DistributedEntityType = 999 }; // not registered with this manager
		Assert.Throws<Exception>(() => em.GetPersistedFieldsAsBson(e));
	}

	// ───────── 4. Field schema (Part D) ─────────

	[Test, Category("BsonSchema")]
	public void GetFieldSchema_ClassifiesKindsAndClrTypes()
	{
		var em = MakeManager();
		var byId = em.GetFieldSchema(typeof(BsonTestEntity)).ToDictionary(f => f.FieldId);

		Assert.AreEqual(14, byId.Count);

		// Scalar, persisted
		Assert.AreEqual("Name", byId[1].FieldName);
		Assert.AreEqual("name", byId[1].PersistAs);
		Assert.AreEqual(GameStateEntityFieldType.Value, byId[1].FieldType);
		Assert.AreEqual(GameStateEntityPropertyValueType.String, byId[1].ValueType);
		Assert.AreEqual(typeof(string), byId[1].ValueClrType);
		Assert.IsFalse(byId[1].IsTemporal);

		// Non-persisted → PersistAs null
		Assert.IsNull(byId[7].PersistAs);

		// Temporal (and never persisted)
		Assert.IsTrue(byId[8].IsTemporal);
		Assert.IsNull(byId[8].PersistAs);

		// Collections expose the element/value CLR type
		Assert.AreEqual(GameStateEntityFieldType.Array, byId[9].FieldType);
		Assert.AreEqual(typeof(int), byId[9].ValueClrType);
		Assert.AreEqual(GameStateEntityFieldType.IntDictionary, byId[10].FieldType);
		Assert.AreEqual(typeof(string), byId[10].ValueClrType);   // value type, not the int key
		Assert.AreEqual(GameStateEntityFieldType.StringDictionary, byId[11].FieldType);
		Assert.AreEqual(GameStateEntityFieldType.Queue, byId[12].FieldType);
		Assert.AreEqual(typeof(string), byId[12].ValueClrType);

		// Complex classification via ValueType (no BsonSerializer<> sniffing)
		Assert.AreEqual(GameStateEntityPropertyValueType.Custom, byId[13].ValueType);
		Assert.AreEqual(typeof(BsonTestPoco), byId[13].ValueClrType);
		Assert.AreEqual(GameStateEntityPropertyValueType.CustomSmall, byId[14].ValueType);
		Assert.AreEqual(typeof(Vector3), byId[14].ValueClrType);
	}

	[Test, Category("BsonSchema")]
	public void GetFieldSchema_IncludesInheritedFields()
	{
		var em = MakeManager();
		var byId = em.GetFieldSchema(typeof(BsonTestSubEntity)).ToDictionary(f => f.FieldId);

		Assert.AreEqual(15, byId.Count);             // 14 inherited + Extra
		Assert.IsTrue(byId.ContainsKey(1));          // inherited Name
		Assert.AreEqual("extra", byId[20].PersistAs); // subclass-declared field
	}

	[Test, Category("BsonSchema")]
	public void GetFieldSchema_UnregisteredType_Throws()
	{
		var em = MakeManager();
		Assert.Throws<Exception>(() => em.GetFieldSchema(typeof(ImpunityBsonSerializationTests)));
	}

	// ───────── 5. Registration validation ─────────

	[Test, Category("BsonSchema")]
	public void Register_NonPersistedSubclassOfPersistedBase_InheritedFieldsAreReplicatedOnly()
	{
		var em = new ClientEntityManager();
		var defs = em.RegisterEntityTypes(new[] { typeof(BsonTestEntity), typeof(BsonTestEphemeralSubEntity) });

		var subDef = defs.First(d => d.Index == BsonTestIds.EPHEMERAL_SUBENTITY);
		Assert.IsNull(subDef.PersistedAs);
		foreach (var prop in subDef.Properties)
		{
			Assert.IsNull(prop.PersistedAs, prop.Name + " should be replicated-only on the non-persisted subclass");
		}

		// The persisted base type registered alongside it is unaffected.
		var baseDef = defs.First(d => d.Index == BsonTestIds.ENTITY);
		Assert.AreEqual("ent", baseDef.PersistedAs);
		Assert.AreEqual("name", baseDef.Properties.First(p => p.Name == "Name").PersistedAs);
	}

	[Test, Category("BsonSchema")]
	public void Register_DuplicatePersistAsKeyAcrossTypes_Throws()
	{
		var em = new ClientEntityManager();
		var ex = Assert.Throws<Exception>(() => em.RegisterEntityTypes(new[] { typeof(BsonTestEntity), typeof(BsonTestDupKeyEntity) }));
		StringAssert.Contains("PersistAs", ex.Message);
	}
}
