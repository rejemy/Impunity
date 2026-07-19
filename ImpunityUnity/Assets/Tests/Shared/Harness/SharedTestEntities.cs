// Entity types shared by the test suites. They live inside the test assembly (not the runtime) so
// the Impunity source generator emits the _imp_*Wrapper_ serialization helpers for them, in both the
// dotnet test project (analyzer project reference) and Unity (global RoslynAnalyzer plugin).
//
// Everything here must stay free of UnityEngine types — this file compiles against the non-Unity
// ImpunityRuntime. Unity-typed entities belong in Assets/Tests/Unity/.
#nullable disable

using System;
using System.IO;

using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	// ───────── Portable stand-in for a Unity vector ─────────

	/// <summary>A 3-float struct with the same wire shape as UnityEngine.Vector3 (12 bytes,
	/// CustomSmall). Lets the shared suite exercise the small-custom-serializer machinery that the
	/// Unity serializers (Vector3Serializer etc.) are built on.</summary>
	public struct TestVec3 : IEquatable<TestVec3>
	{
		public float x;
		public float y;
		public float z;

		public TestVec3(float x, float y, float z)
		{
			this.x = x;
			this.y = y;
			this.z = z;
		}

		public bool Equals(TestVec3 other) => x == other.x && y == other.y && z == other.z;
		public override bool Equals(object obj) => obj is TestVec3 other && Equals(other);
		public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);
		public override string ToString() => $"({x}, {y}, {z})";
	}

	/// <summary>Binary serializer for <see cref="TestVec3"/> — mirrors Vector3Serializer
	/// (Client/Unity/DistributedUnitySerializers.cs): 12 bytes, CustomSmall. Also doubles as the
	/// portable coverage for the payload-serializer + framing-wrapper machinery
	/// (<see cref="ICustomPayloadSerializer{T}"/> / <see cref="CustomSmallSerializer{T,P}"/>).</summary>
	public readonly struct TestVec3Serializer : IDistributableValueSerializer<TestVec3>, ICustomPayloadSerializer<TestVec3>
	{
		public void WriteTo(TestVec3 value, BinaryWriter w) => default(CustomSmallSerializer<TestVec3, TestVec3Serializer>).WriteTo(value, w);
		public TestVec3 ReadFrom(BinaryReader r) => default(CustomSmallSerializer<TestVec3, TestVec3Serializer>).ReadFrom(r);
		public GameStateEntityPropertyValueType ValueType { get => GameStateEntityPropertyValueType.CustomSmall; }

		public void WritePayload(TestVec3 value, BinaryWriter w)
		{
			w.Write(value.x);
			w.Write(value.y);
			w.Write(value.z);
		}

		public TestVec3 ReadPayload(BinaryReader r, int byteCount)
		{
			return new TestVec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
		}

		public BsonValue ToBsonValue(TestVec3 value)
		{
			BsonArray vect = new BsonArray
			{
				value.x,
				value.y,
				value.z
			};
			return vect;
		}

		public TestVec3 FromBsonValue(BsonValue value)
		{
			BsonArray vect = value.AsArray;

			return new TestVec3(vect[0].AsSingle, vect[1].AsSingle, vect[2].AsSingle);
		}
	}

	// ───────── Integration test types (ephemeral) ─────────

	[DistributedEntity(IntegrationTestTypes.ENTITY)]
	public partial class IntegrationTestEntity : DistributedObjectBase
	{
		public enum Props : byte { HEALTH = 1, NAME = 2, ACTION = 3 }

		[Distributed((byte)Props.HEALTH)]
		public DistributedValue<int, Int32Serializer> Health;

		[Distributed((byte)Props.NAME)]
		public DistributedValue<string, StringSerializer> DisplayName;

		[Distributed((byte)Props.ACTION)]
		public DistributedTemporalValue<int, Int32Serializer> Action;

		public bool WasDeleted;
		public int UndistributedCount;

		public override void OnDeleted(BsonValue deleteData)
		{
			WasDeleted = true;
		}

		public override void OnUndistributed()
		{
			UndistributedCount++;
		}
	}

	[DistributedEntity(IntegrationTestTypes.CHANNEL)]
	public partial class IntegrationTestChannel : DistributedChannelBase
	{
		public enum Props : byte { STATUS = 1, GRID = 2, CHAT = 3, FLAGS = 4, HISTORY = 5 }

		[Distributed((byte)Props.STATUS)]
		public DistributedValue<string, StringSerializer> Status;

		[Distributed((byte)Props.GRID)]
		public DistributedArray<int, Int32Serializer> Grid;

		[Distributed((byte)Props.CHAT)]
		public DistributedQueue<string, StringSerializer> Chat;

		[Distributed((byte)Props.FLAGS)]
		public DistributedIntDictionary<string, StringSerializer> Flags;

		[Distributed((byte)Props.HISTORY)]
		public DistributedStack<string, StringSerializer> History;

		public int UndistributedCount;

		public override void OnUndistributed()
		{
			UndistributedCount++;
		}
	}

	public static class IntegrationTestTypes
	{
		public const int ENTITY = 1;
		public const int CHANNEL = 2;
	}

	public static class IntegrationTestCollections
	{
		public const int ITEMS = 10;
	}

	// ───────── Migration test types (persisted) ─────────

	[DistributedEntity(MigTestTypes.PCHANNEL, PersistAs = "mchan")]
	public partial class MigTestChannel : DistributedChannelBase
	{
		public enum Props : byte { LABEL = 1 }

		[Distributed((byte)Props.LABEL, PersistAs = "label")]
		public DistributedValue<string, StringSerializer> Label;
	}

	[DistributedEntity(MigTestTypes.POBJECT, PersistAs = "mobj")]
	public partial class MigTestObject : DistributedObjectBase
	{
		public enum Props : byte { SCORE = 1 }

		[Distributed((byte)Props.SCORE, PersistAs = "score")]
		public DistributedValue<int, Int32Serializer> Score;
	}

	public static class MigTestTypes
	{
		public const int PCHANNEL = 101;
		public const int POBJECT = 102;
	}

	public static class MigTestCollections
	{
		public const int ITEMS = 10;
		public const int PLAYERS = 11;
		public const string ITEMS_NAME = "Items";
		public const string PLAYERS_NAME = "Players";
	}

	// ───────── BSON persisted-field test types ─────────

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
		[Distributed(14, PersistAs = "pos")] public DistributedValue<TestVec3, TestVec3Serializer> Position;
		[Distributed(15, PersistAs = "undo")] public DistributedStack<string, StringSerializer> Undo;

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
}
