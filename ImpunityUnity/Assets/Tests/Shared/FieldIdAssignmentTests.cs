// ───────── Automatic field-id assignment ─────────
//
// Distributed fields no longer carry a hand-assigned wire id; DistributedFieldIds derives one from the
// entity type itself. These tests pin down the assignment rules, because they are what the format
// checksum (and therefore schema compatibility) is built on:
//
//   - base classes first, then ordinal field name within each class, numbered from 1
//   - ids are per CONCRETE type, so sibling subclasses never have to agree
//   - the generated InitializeDistributedFields and ClientEntityManager.RegisterEntityType must agree
#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

using Impunity.Connection;

namespace Impunity.Tests
{
	// ───────── Fixtures ─────────

	[DistributedEntity(FieldIdTestTypes.BASE)]
	public partial class FieldIdBase : DistributedObjectBase
	{
		// Deliberately declared out of alphabetical order: assignment must not depend on declaration order.
		public DistributedValue<int, Int32Serializer> Zulu;
		public DistributedValue<int, Int32Serializer> Alpha;
		public DistributedValue<int, Int32Serializer> Mike;
	}

	[DistributedEntity(FieldIdTestTypes.DERIVED)]
	public partial class FieldIdDerived : FieldIdBase
	{
		// Sorts before every base field, to prove a subclass cannot renumber what it inherits.
		public DistributedValue<int, Int32Serializer> Aardvark;
		public DistributedValue<int, Int32Serializer> Nadir;
	}

	// A second subclass of the same base: its own fields overlap FieldIdDerived's id range, which is fine
	// because ids are per concrete type.
	[DistributedEntity(FieldIdTestTypes.SIBLING)]
	public partial class FieldIdSibling : FieldIdBase
	{
		public DistributedValue<int, Int32Serializer> Solo;
	}

	// A private distributed field on a base class. The flattened Type.GetFields does not return these, so
	// walking the chain level by level is what keeps it visible to the subclass.
	[DistributedEntity(FieldIdTestTypes.PRIVATE_BASE)]
	public partial class FieldIdPrivateBase : DistributedObjectBase
	{
		private DistributedValue<int, Int32Serializer> Hidden;
		public DistributedValue<int, Int32Serializer> Shown;

		public int ReadHidden() { return Hidden.Get(); }
	}

	[DistributedEntity(FieldIdTestTypes.PRIVATE_DERIVED)]
	public partial class FieldIdPrivateDerived : FieldIdPrivateBase
	{
		public DistributedValue<int, Int32Serializer> Own;
	}

	// Nothing here is marked up: a field is distributed because of its TYPE. The non-distributed members are
	// present to prove the detection discriminates rather than sweeping up everything on the class.
	[DistributedEntity(FieldIdTestTypes.DETECTION, PersistAs = "fidetect")]
	public partial class FieldIdDetectionEntity : DistributedObjectBase
	{
		public DistributedValue<int, Int32Serializer> Replicated;
		[PersistAs("stored")] public DistributedValue<int, Int32Serializer> Stored;

		// Only instance fields replicate — the runtime enumerates with BindingFlags.Instance and the generator
		// skips static/const declarations, so this must not become part of the schema.
		public static DistributedValue<int, Int32Serializer> StaticField;

		public const int SomeConstant = 3;
		public int PlainField;
		public string AnotherPlainField;
	}

	public static class FieldIdTestTypes
	{
		public const int BASE = 210;
		public const int DERIVED = 211;
		public const int SIBLING = 212;
		public const int PRIVATE_BASE = 213;
		public const int PRIVATE_DERIVED = 214;
		public const int DETECTION = 215;
	}

	// ───────── Tests ─────────

	public class FieldIdAssignmentTests
	{
		static Dictionary<string, byte> Ids(Type t) => DistributedFieldIds.ForType(t);

		[Test, Category("FieldIds")]
		public void IdsAreSortedByNameNotDeclarationOrder()
		{
			var ids = Ids(typeof(FieldIdBase));

			Assert.AreEqual(3, ids.Count);
			Assert.AreEqual(1, ids["Alpha"]);
			Assert.AreEqual(2, ids["Mike"]);
			Assert.AreEqual(3, ids["Zulu"]);
		}

		[Test, Category("FieldIds")]
		public void IdsAreDenseAndStartAtOne()
		{
			var ids = Ids(typeof(FieldIdDerived));
			var assigned = ids.Values.OrderBy(v => v).ToList();

			Assert.AreEqual(5, assigned.Count);
			for (int i = 0; i < assigned.Count; i++)
			{
				Assert.AreEqual(i + 1, assigned[i], "Ids should be dense, 1..N");
			}
		}

		[Test, Category("FieldIds")]
		public void SubclassNeverRenumbersInheritedFields()
		{
			var baseIds = Ids(typeof(FieldIdBase));
			var derivedIds = Ids(typeof(FieldIdDerived));

			foreach (var kv in baseIds)
			{
				Assert.AreEqual(kv.Value, derivedIds[kv.Key],
					"Inherited field " + kv.Key + " must keep its base id");
			}

			// ...even though Aardvark sorts before every one of them.
			Assert.AreEqual(4, derivedIds["Aardvark"]);
			Assert.AreEqual(5, derivedIds["Nadir"]);
		}

		[Test, Category("FieldIds")]
		public void SiblingSubclassesMayReuseTheSameIds()
		{
			// Each registered entity type has its own property table on the server, so overlap is harmless —
			// and it is exactly what removes the "unique across a class and all its parents" burden.
			Assert.AreEqual(4, Ids(typeof(FieldIdDerived))["Aardvark"]);
			Assert.AreEqual(4, Ids(typeof(FieldIdSibling))["Solo"]);
		}

		[Test, Category("FieldIds")]
		public void PrivateBaseFieldsAreAssignedAndVisibleToSubclasses()
		{
			var baseIds = Ids(typeof(FieldIdPrivateBase));
			Assert.AreEqual(2, baseIds.Count, "A private distributed field still gets an id");
			Assert.AreEqual(1, baseIds["Hidden"]);
			Assert.AreEqual(2, baseIds["Shown"]);

			var derivedIds = Ids(typeof(FieldIdPrivateDerived));
			Assert.AreEqual(3, derivedIds.Count,
				"A base type's private distributed field must not vanish from the subclass");
			Assert.AreEqual(1, derivedIds["Hidden"]);
			Assert.AreEqual(3, derivedIds["Own"]);

			// ...and registration must find the same set, or the field would hold a dirty bit that nothing ever
			// serializes.
			var manager = new ClientEntityManager();
			manager.RegisterEntityTypes(new Type[] { typeof(FieldIdPrivateBase), typeof(FieldIdPrivateDerived) });

			var registered = manager.GetFieldSchema(typeof(FieldIdPrivateDerived)).ToDictionary(f => f.FieldName);
			Assert.AreEqual(3, registered.Count,
				"Registration must see a base type's private distributed field too");
			Assert.AreEqual(derivedIds["Hidden"], registered["Hidden"].FieldId);
		}

		// ───────── Detection is by field type, not by attribute ─────────

		[Test, Category("FieldIds")]
		public void FieldsAreDistributedBecauseOfTheirTypeWithNoAttribute()
		{
			var ids = Ids(typeof(FieldIdDetectionEntity));

			Assert.AreEqual(2, ids.Count, "Only the IDistributedField-typed instance fields should be distributed");
			Assert.IsTrue(ids.ContainsKey("Replicated"));
			Assert.IsTrue(ids.ContainsKey("Stored"));

			Assert.IsFalse(ids.ContainsKey("PlainField"));
			Assert.IsFalse(ids.ContainsKey("AnotherPlainField"));
			Assert.IsFalse(ids.ContainsKey("SomeConstant"));
		}

		[Test, Category("FieldIds")]
		public void StaticDistributedFieldsAreNotPartOfTheSchema()
		{
			Assert.IsFalse(Ids(typeof(FieldIdDetectionEntity)).ContainsKey("StaticField"));

			var manager = new ClientEntityManager();
			manager.RegisterEntityTypes(new Type[] { typeof(FieldIdDetectionEntity) });

			foreach (var field in manager.GetFieldSchema(typeof(FieldIdDetectionEntity)))
			{
				Assert.AreNotEqual("StaticField", field.FieldName, "A static distributed field must not be registered");
			}
		}

		[Test, Category("FieldIds")]
		public void PersistAsAttributeSuppliesTheDurableKey()
		{
			var manager = new ClientEntityManager();
			manager.RegisterEntityTypes(new Type[] { typeof(FieldIdDetectionEntity) });

			var byName = manager.GetFieldSchema(typeof(FieldIdDetectionEntity)).ToDictionary(f => f.FieldName);

			Assert.AreEqual("stored", byName["Stored"].PersistAs);
			Assert.IsNull(byName["Replicated"].PersistAs, "An unannotated field is replicated but not persisted");
		}

		[Test, Category("FieldIds")]
		public void ForTypeIsCachedAndStable()
		{
			Assert.AreSame(Ids(typeof(FieldIdBase)), Ids(typeof(FieldIdBase)));
		}

		[Test, Category("FieldIds")]
		public void GetFieldIdReturnsZeroForUnknownField()
		{
			Assert.AreEqual(0, DistributedFieldIds.GetFieldId(typeof(FieldIdBase), "NoSuchField"));
			Assert.AreEqual(1, DistributedFieldIds.GetFieldId(typeof(FieldIdBase), "Alpha"));
		}

		// The two producers of a field id — the generated InitializeDistributedFields (which turns it into the
		// field's dirty bitmask) and RegisterEntityType (which puts it on the wire) — must agree, or an update
		// would be written under one id and decoded under another.
		[Test, Category("FieldIds")]
		public void GeneratedBitmasksMatchRegisteredIds()
		{
			var manager = new ClientEntityManager();
			manager.RegisterEntityTypes(new Type[] { typeof(FieldIdBase), typeof(FieldIdDerived) });

			var entity = new FieldIdDerived();
			foreach (var field in manager.GetFieldSchema(typeof(FieldIdDerived)))
			{
				ulong expected = 1ul << (field.FieldId - 1);
				entity.ClearDirty();

				switch (field.FieldName)
				{
					case "Alpha": entity.Alpha.Set(1); break;
					case "Mike": entity.Mike.Set(1); break;
					case "Zulu": entity.Zulu.Set(1); break;
					case "Aardvark": entity.Aardvark.Set(1); break;
					case "Nadir": entity.Nadir.Set(1); break;
					default: Assert.Fail("Unexpected field " + field.FieldName); break;
				}

				Assert.AreEqual(expected, entity.DirtyBits,
					"Field " + field.FieldName + " set a dirty bit that does not match its registered wire id");
			}
		}

		[Test, Category("FieldIds")]
		public void RegistrationAssignsTheSameIdsAsTheResolver()
		{
			var manager = new ClientEntityManager();
			manager.RegisterEntityTypes(new Type[] { typeof(FieldIdBase), typeof(FieldIdDerived) });

			var ids = Ids(typeof(FieldIdDerived));
			foreach (var field in manager.GetFieldSchema(typeof(FieldIdDerived)))
			{
				Assert.AreEqual(ids[field.FieldName], field.FieldId,
					"Registered id for " + field.FieldName + " disagrees with DistributedFieldIds");
			}
		}
	}
}
