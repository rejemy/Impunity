// Dirty detection for distributed fields whose serializer declares DistributedValueSemantics.Mutable.
//
// A mutable value handed out by Get() is the field's own state, not a copy. When a caller modifies it in
// place and calls Set(), newValue and CurrentValue are the same object (or, for a blob, the same buffer), so
// an equality comparison compares the value with itself and reports "unchanged" — which used to swallow the
// update unless the caller remembered force: true. Fields over Mutable serializers therefore skip the
// unchanged short-circuit entirely; fields over Immutable ones must still deduplicate.
//
// These drive the client-side field API directly on connection-less client-authoritative entities (the
// pattern from BsonSerializationTests), so they need no server: dirty state is entity-local.
#nullable disable

using System;
using System.Collections.Generic;
using NUnit.Framework;

using Impunity.Connection;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class MutableValueDirtyTests
	{
		// ───────── Helpers ─────────

		/// <summary>A connection-less, client-authoritative entity: field setters apply to CurrentValue
		/// immediately and SetDirty records on the entity itself, with no manager or server involved.</summary>
		static MutableValueTestEntity MakeEntity() => new MutableValueTestEntity { IsClientAuthoritative = true };

		static bool IsDirty(MutableValueTestEntity e) => e.DirtyBits != 0;

		// ───────── Mutable: the reported bug ─────────

		[Test, Category("MutableValue")]
		public void Mutable_InPlaceMutationOfGetResult_MarksDirty()
		{
			var e = MakeEntity();
			e.Poco.Set(new BsonTestPoco { Number = 1, Label = "start" });
			e.ClearDirty();

			// The reported pattern: take the value out, change a member, put it back — no force: true.
			BsonTestPoco live = e.Poco.Get();
			live.Number = 2;

			Assert.IsTrue(e.Poco.Set(live), "Set of an in-place-mutated value must report a change");
			Assert.IsTrue(IsDirty(e), "the field must be dirty so the change replicates");
		}

		[Test, Category("MutableValue")]
		public void Mutable_ExactSameReference_MarksDirty()
		{
			var e = MakeEntity();
			var poco = new BsonTestPoco { Number = 1, Label = "start" };
			e.Poco.Set(poco);
			e.ClearDirty();

			// Nothing was changed. The field cannot tell this apart from the mutation above, and takes the
			// safe branch: a redundant update rather than a dropped one.
			Assert.IsTrue(e.Poco.Set(poco));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Mutable_EqualButDistinctInstance_MarksDirty()
		{
			var e = MakeEntity();
			e.Poco.Set(new BsonTestPoco { Number = 1, Label = "start" });
			e.ClearDirty();

			var equal = new BsonTestPoco { Number = 1, Label = "start" };
			Assert.IsTrue(equal.Equals(e.Poco.Get()), "precondition: structurally equal");

			// Mutable semantics skip the comparison outright, so even a provably-equal fresh instance sends.
			// This is the accepted cost of the policy — re-setting an unchanged mutable value is an
			// anti-pattern, not a supported way to avoid traffic.
			Assert.IsTrue(e.Poco.Set(equal));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Mutable_Null_IsHandled()
		{
			var e = MakeEntity();
			e.Poco.Set(null);
			e.ClearDirty();

			Assert.IsTrue(e.Poco.Set(null), "null is a mutable-typed value like any other; no special case");
			Assert.IsTrue(IsDirty(e));
		}

		// ───────── Mutable: blobs (a struct wrapping a shared buffer) ─────────

		[Test, Category("MutableValue")]
		public void Blob_RewrittenInPlace_MarksDirty()
		{
			var e = MakeEntity();
			byte[] buffer = { 1, 2, 3, 4 };
			e.Blob.Set(new ArraySegment<byte>(buffer));
			e.ClearDirty();

			// ArraySegment is a struct, so nothing here is reference-aliased in the object sense — but its
			// Equals compares (array, offset, count) and never the bytes, so rewriting the buffer is exactly
			// as invisible to a comparison as mutating an object's members.
			buffer[0] = 99;

			Assert.IsTrue(e.Blob.Set(new ArraySegment<byte>(buffer)));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Blob_SameSegmentValue_MarksDirty()
		{
			var e = MakeEntity();
			byte[] buffer = { 1, 2, 3, 4 };
			var segment = new ArraySegment<byte>(buffer);
			e.Blob.Set(segment);
			e.ClearDirty();

			Assert.IsTrue(e.Blob.Set(segment));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Blob_FieldCompiles_WithoutIEquatable()
		{
			// ArraySegment<byte> does not implement IEquatable<ArraySegment<byte>>. This field existing at all
			// is the regression guard against reinstating that constraint on the field types.
			Assert.IsFalse(typeof(IEquatable<ArraySegment<byte>>).IsAssignableFrom(typeof(ArraySegment<byte>)));

			var e = MakeEntity();
			Assert.IsTrue(e.Blob.Set(new ArraySegment<byte>(new byte[] { 7 })));
			Assert.AreEqual(7, e.Blob.Get().Array[0]);
		}

		// ───────── Null values through the full field path ─────────

		static ClientEntityManager MakeManager()
		{
			var em = new ClientEntityManager();
			em.RegisterEntityTypes(new[] { typeof(MutableValueTestEntity) });
			return em;
		}

		/// <summary>Encodes an entity's dirty fields and applies them to a second entity, the way an update
		/// travels to another subscriber.</summary>
		static void ReplicateTo(ClientEntityManager em, MutableValueTestEntity from, MutableValueTestEntity to)
		{
			ArraySegment<byte> bytes = em.GetPropertyBytes(from, out _);
			em.SetPropertyBytes(to, bytes, initialRead: false, seq: 1);
		}

		[Test, Category("MutableValue")]
		public void Null_BsonValue_RoundTripsOverTheWire()
		{
			var em = MakeManager();
			var src = MakeEntity();
			var dst = MakeEntity();

			src.Poco.Set(new BsonTestPoco { Number = 5, Label = "present" });
			ReplicateTo(em, src, dst);
			Assert.IsNotNull(dst.Poco.Get(), "precondition: a present value replicates");

			// CustomNullable framing writes a single false byte for null; the reader must decode that back to
			// null rather than attempting to read a BSON payload.
			src.Poco.Set(null);
			ReplicateTo(em, src, dst);

			Assert.IsNull(dst.Poco.Get(), "null must replicate as null");
		}

		[Test, Category("MutableValue")]
		public void Null_ThenNonNull_RoundTripsOverTheWire()
		{
			var em = MakeManager();
			var src = MakeEntity();
			var dst = MakeEntity();

			src.Poco.Set(null);
			ReplicateTo(em, src, dst);
			Assert.IsNull(dst.Poco.Get());

			// Recovering from null must not leave the stream misaligned.
			src.Poco.Set(new BsonTestPoco { Number = 9, Label = "back" });
			ReplicateTo(em, src, dst);

			Assert.IsNotNull(dst.Poco.Get());
			Assert.AreEqual(9, dst.Poco.Get().Number);
			Assert.AreEqual("back", dst.Poco.Get().Label);
		}

		[Test, Category("MutableValue")]
		public void Null_DoesNotDesynchronizeOtherFields()
		{
			var em = MakeManager();
			var src = MakeEntity();
			var dst = MakeEntity();

			// A null blob and a null object sit between two ordinary fields: if either wrote the wrong number
			// of bytes, the fields decoded after them would come back wrong.
			src.Number.Set(1234);
			src.Poco.Set(null);
			src.Blob.Set(default);
			src.Text.Set("after the nulls");
			ReplicateTo(em, src, dst);

			Assert.AreEqual(1234, dst.Number.Get());
			Assert.IsNull(dst.Poco.Get());
			Assert.IsNull(dst.Blob.Get().Array);
			Assert.AreEqual("after the nulls", dst.Text.Get());
		}

		// MutableValueTestEntity is ephemeral, so the persisted-field path has to be exercised through
		// BsonTestEntity, whose [PersistAs("data")] Data field uses BsonSerializer<BsonTestPoco>.

		[Test, Category("MutableValue")]
		public void Null_BsonValue_SurvivesThePersistedFieldPath()
		{
			var em = new ClientEntityManager();
			em.RegisterEntityTypes(new[] { typeof(BsonTestEntity) });

			var src = new BsonTestEntity { IsClientAuthoritative = true };
			src.Name.Set("has a null data field");
			src.Data.Set(null);

			// GetAsBsonValue runs BsonSerializer<T>.ToBsonValue over a null value.
			BsonDocument doc = null;
			Assert.DoesNotThrow(() => doc = em.GetPersistedFieldsAsBson(src), "serializing a null must not throw");
			Assert.IsTrue(doc.ContainsKey("data"), "the persisted key is still written for a null value");
			Assert.IsTrue(doc["data"].IsNull, "and it is written as BSON null");

			var dst = new BsonTestEntity { IsClientAuthoritative = true };
			Assert.DoesNotThrow(() => em.ApplyPersistedFieldsFromBson(dst, doc), "reading a null back must not throw");
			Assert.IsNull(dst.Data.Get());
			Assert.AreEqual("has a null data field", dst.Name.Get(), "a null field must not disturb the ones after it");
		}

		[Test, Category("MutableValue")]
		public void Null_InStorage_ClearsAnExistingValue()
		{
			var em = new ClientEntityManager();
			em.RegisterEntityTypes(new[] { typeof(BsonTestEntity) });

			var src = new BsonTestEntity { IsClientAuthoritative = true };
			src.Data.Set(null);
			BsonDocument doc = em.GetPersistedFieldsAsBson(src);

			var dst = new BsonTestEntity { IsClientAuthoritative = true };
			dst.Data.Set(new BsonTestPoco { Number = 3, Label = "existing" });
			em.ApplyPersistedFieldsFromBson(dst, doc);

			// A stored null is a value, not an absence: applying a document that records null must produce
			// null, so a database round trip preserves it.
			Assert.IsNull(dst.Data.Get(), "a stored null must be applied, not skipped");
		}

		[Test, Category("MutableValue")]
		public void MissingKey_LeavesTheFieldUnchanged()
		{
			var em = new ClientEntityManager();
			em.RegisterEntityTypes(new[] { typeof(BsonTestEntity) });

			// A document written before the field existed does not mention it at all. That must stay distinct
			// from a document that records null — BsonDocument's indexer answers BsonValue.Null for both, so
			// this is the regression guard for probing absence with ContainsKey.
			var doc = new BsonDocument();
			doc["name"] = "only the name was stored";

			var dst = new BsonTestEntity { IsClientAuthoritative = true };
			dst.Data.Set(new BsonTestPoco { Number = 3, Label = "existing" });
			dst.Count.Set(42);

			em.ApplyPersistedFieldsFromBson(dst, doc);

			Assert.AreEqual("only the name was stored", dst.Name.Get());
			Assert.IsNotNull(dst.Data.Get(), "an unmentioned field keeps its value");
			Assert.AreEqual(3, dst.Data.Get().Number);
			Assert.AreEqual(42, dst.Count.Get());
		}

		[Test, Category("MutableValue")]
		public void Null_AgainstANonNullableField_IsIgnoredNotThrown()
		{
			var em = new ClientEntityManager();
			em.RegisterEntityTypes(new[] { typeof(BsonTestEntity) });

			// An explicit null recorded against value-typed fields, which have no null to represent. Their
			// serializers would unbox a null onto a struct and throw, so the fields ignore it.
			var doc = new BsonDocument();
			doc["count"] = BsonValue.Null;
			doc["ratio"] = BsonValue.Null;
			doc["when"] = BsonValue.Null;
			doc["pos"] = BsonValue.Null;
			doc["scores"] = BsonValue.Null;
			doc["items"] = BsonValue.Null;

			var dst = new BsonTestEntity { IsClientAuthoritative = true };
			dst.Count.Set(42);

			Assert.DoesNotThrow(() => em.ApplyPersistedFieldsFromBson(dst, doc));
			Assert.AreEqual(42, dst.Count.Get(), "a null against a non-nullable field changes nothing");
		}

		[Test, Category("MutableValue")]
		public void Null_Blob_RoundTripsThroughBson()
		{
			// BlobSerializer writes a default segment as BSON null; reading it back used to unbox that null
			// onto ArraySegment<byte> and throw.
			var ser = new BlobSerializer();

			BsonValue stored = ser.ToBsonValue(default);
			Assert.IsTrue(stored.IsNull, "a null blob stores as BSON null");

			ArraySegment<byte> back = default;
			Assert.DoesNotThrow(() => back = ser.FromBsonValue(stored));
			Assert.IsNull(back.Array);

			// And a present blob still round-trips.
			ArraySegment<byte> present = ser.FromBsonValue(ser.ToBsonValue(new ArraySegment<byte>(new byte[] { 1, 2, 3 })));
			Assert.AreEqual(3, present.Count);
			Assert.AreEqual(2, present.Array[1]);
		}

		// ───────── Semantics declarations ─────────

		[Test, Category("MutableValue")]
		public void BsonSerializers_DeclareMutable_AndPrimitivesImmutable()
		{
			Assert.AreEqual(DistributedValueSemantics.Mutable, new BsonSerializer<BsonTestPoco>().ValueSemantics);
			Assert.AreEqual(DistributedValueSemantics.Mutable, new BsonSmallSerializer<BsonTestPoco>().ValueSemantics);

			Assert.AreEqual(DistributedValueSemantics.Immutable, new Int32Serializer().ValueSemantics);
			Assert.AreEqual(DistributedValueSemantics.Immutable, new StringSerializer().ValueSemantics);
		}

		// ───────── Immutable: deduplication must survive ─────────

		[Test, Category("MutableValue")]
		public void Immutable_UnchangedPrimitive_StaysClean()
		{
			var e = MakeEntity();
			e.Number.Set(7);
			e.ClearDirty();

			Assert.IsFalse(e.Number.Set(7), "unchanged primitive must still deduplicate");
			Assert.IsFalse(IsDirty(e));

			Assert.IsTrue(e.Number.Set(8));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Immutable_UnchangedString_StaysClean()
		{
			var e = MakeEntity();
			e.Text.Set("hello");
			e.ClearDirty();

			// string is a class but cannot be mutated, so StringSerializer stays Immutable and both an
			// identical reference and an equal-but-distinct instance deduplicate.
			Assert.IsFalse(e.Text.Set(e.Text.Get()));
			Assert.IsFalse(e.Text.Set(new string("hello".ToCharArray())));
			Assert.IsFalse(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Immutable_Force_StillOverrides()
		{
			var e = MakeEntity();
			e.Number.Set(7);
			e.ClearDirty();

			Assert.IsTrue(e.Number.Set(7, force: true), "force: true must still bypass the comparison");
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Immutable_TypeWithoutIEquatable_StillDeduplicates()
		{
			// The field types no longer require IEquatable<T>, so deduplication falls through
			// EqualityComparer<T>.Default to ValueType.Equals for a plain struct like this one. Immutable
			// semantics must still mean "unchanged sets stay clean".
			Assert.IsFalse(typeof(IEquatable<TestPlainPair>).IsAssignableFrom(typeof(TestPlainPair)));

			var e = MakeEntity();
			e.Pair.Set(new TestPlainPair(1, 2));
			e.ClearDirty();

			Assert.IsFalse(e.Pair.Set(new TestPlainPair(1, 2)), "equal value must still deduplicate");
			Assert.IsFalse(IsDirty(e));

			Assert.IsTrue(e.Pair.Set(new TestPlainPair(1, 3)));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Immutable_NullString_StaysClean()
		{
			var e = MakeEntity();
			e.Text.Set(null);
			e.ClearDirty();

			Assert.IsFalse(e.Text.Set(null), "null-to-null on an immutable type is genuinely unchanged");
			Assert.IsFalse(IsDirty(e));
		}

		// ───────── Array elements take the same path ─────────

		[Test, Category("MutableValue")]
		public void Array_MutableElementMutatedInPlace_MarksDirty()
		{
			var e = MakeEntity();
			e.Pocos.Replace(new List<BsonTestPoco>
			{
				new BsonTestPoco { Number = 1, Label = "a" },
				new BsonTestPoco { Number = 2, Label = "b" },
			});
			e.ClearDirty();

			BsonTestPoco live = e.Pocos.Get(0);
			live.Number = 42;

			Assert.IsTrue(e.Pocos.Set(0, live));
			Assert.IsTrue(IsDirty(e));
		}

		[Test, Category("MutableValue")]
		public void Array_NullElement_DoesNotThrow()
		{
			var e = MakeEntity();
			e.Pocos.Replace(new List<BsonTestPoco> { null, null });
			e.ClearDirty();

			// The comparison used to be an instance call on the stored element, which threw on a null one.
			Assert.DoesNotThrow(() => e.Pocos.Set(0, new BsonTestPoco { Number = 1, Label = "a" }));
			Assert.IsTrue(IsDirty(e));
		}
	}
}
