// Ported from Assets/Tests/Unit/ImpunityBsonSerializationTests.cs. The Unity-typed serializer tests
// (Vector2/3/4, Color, Quaternion, Matrix4x4) and the Vector3 entity field moved to
// Assets/Tests/Unity/Editor/; field 14 here uses the portable TestVec3 stand-in, which exercises the
// same CustomSmall serializer machinery. Entity types live in Harness/SharedTestEntities.cs.
#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class BsonSerializationTests
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
			e.Position.Set(new TestVec3(1.5f, 2.25f, -3.75f));
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

		[Test, Category("BsonSerializers")]
		public void CustomSmall_Bson_RoundTrip()
		{
			// Same machinery as the Unity Vector3Serializer (covered in Assets/Tests/Unity/Editor/).
			var v = new TestVec3(1.5f, 2.25f, -3.75f);
			Assert.AreEqual(v, BsonRoundTrip<TestVec3, TestVec3Serializer>(new TestVec3Serializer(), v));
		}

		// ───────── 2. Manager: GetPersistedFieldsAsBson / ApplyPersistedFieldsFromBson ─────────

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
		public void Manager_ComplexAndCustomSmallFieldsRoundTrip()
		{
			var em = MakeManager();
			var src = MakeEntity<BsonTestEntity>();
			PopulateBaseFields(src);

			BsonDocument doc = em.GetPersistedFieldsAsBson(src);
			Assert.AreEqual(BsonType.Document, doc["data"].Type, "complex field persists as a readable document");

			var dst = MakeEntity<BsonTestEntity>();
			em.ApplyPersistedFieldsFromBson(dst, doc);

			Assert.AreEqual(new BsonTestPoco { Number = 42, Label = "loot" }, dst.Data.Get());
			Assert.AreEqual(new TestVec3(1.5f, 2.25f, -3.75f), dst.Position.Get());
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

		// ───────── 3. Field schema ─────────

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
			Assert.AreEqual(typeof(TestVec3), byId[14].ValueClrType);
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
			Assert.Throws<Exception>(() => em.GetFieldSchema(typeof(BsonSerializationTests)));
		}

		// ───────── 4. Registration validation ─────────

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

		// ───────── Exclusive-update wire fields (xs on UpdateEntityAction, sq on create messages) ─────────

		static T WireRoundTrip<T>(T obj) where T : class
		{
			var mapper = ImpunityUtil.GetBsonMapper();
			byte[] bytes = mapper.SerializeToBytes(obj.GetType(), obj);
			return mapper.DeserializeFromBytes<T>(new ArraySegment<byte>(bytes));
		}

		[Test, Category("ExclusiveUpdateWire")]
		public void UpdateEntityAction_WithoutKnownSeqs_RoundTrips()
		{
			var action = new UpdateEntityAction(42u, new ArraySegment<byte>(new byte[] { 1, 5, 0 }), 7);
			var back = WireRoundTrip(action);

			Assert.AreEqual(42u, back.EntityId);
			Assert.AreEqual(7, back.Seq);
			Assert.AreEqual(new byte[] { 1, 5, 0 }, back.UpdateBytes.ToArray());
			// Absent xs must deserialize to a null-array segment (⇒ ordinary, non-exclusive update).
			Assert.IsNull(back.KnownFieldSeqs.Array, "Absent xs should round-trip as a null-array segment");
		}

		[Test, Category("ExclusiveUpdateWire")]
		public void UpdateEntityAction_WithKnownSeqs_RoundTrips()
		{
			// Blob: field 1 known seq 0x0102, field 3 known seq 0x00FF, terminator 0.
			byte[] xs = { 1, 0x02, 0x01, 3, 0xFF, 0x00, 0 };
			var action = new UpdateEntityAction(42u, new ArraySegment<byte>(new byte[] { 1, 9, 0 }), 7, new ArraySegment<byte>(xs), null);
			var back = WireRoundTrip(action);

			Assert.IsNotNull(back.KnownFieldSeqs.Array, "Present xs should round-trip as a non-null segment");
			Assert.AreEqual(xs, back.KnownFieldSeqs.ToArray());
		}

		[Test, Category("ExclusiveUpdateWire")]
		public void ChannelCreateMessage_SeqRoundTrips()
		{
			var msg = new ChannelCreateMessageAction { ChannelId = 3u, ChannelName = "room", ChannelType = 1, Seq = 12345 };
			var back = WireRoundTrip(msg);
			Assert.AreEqual((ushort)12345, back.Seq);
		}

		[Test, Category("ExclusiveUpdateWire")]
		public void ObjectCreateMessage_SeqRoundTrips_AndDefaultsToZero()
		{
			var withSeq = WireRoundTrip(new ObjectCreateMessageAction { ObjectId = 5u, ChannelId = 3u, ObjectType = 1, Seq = 999 });
			Assert.AreEqual((ushort)999, withSeq.Seq);

			// A message serialized without ever setting Seq must default to 0 (back-compat with old producers).
			var noSeq = WireRoundTrip(new ObjectCreateMessageAction { ObjectId = 5u, ChannelId = 3u, ObjectType = 1 });
			Assert.AreEqual((ushort)0, noSeq.Seq);
		}
	}
}
