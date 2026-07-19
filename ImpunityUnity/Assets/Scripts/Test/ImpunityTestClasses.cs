using UnityEngine;

using System.Collections.Generic;

using Impunity;
using Impunity.Connection;

using UltraLiteDB;
using Impunity.Unity;
using System.IO;
using System;
using System.Collections;


public static class TestCollectionTypes
{
	// 0 - 9 are reserved
	public const int CHARACTERS = 10;
}

public static class TestEntityTypes
{
	// 0 is reserved
	public const int OBJ = 1;
	public const int PLAYER = 2;
	public const int ZONE = 3;
	public const int PERSISTED_ZONE = 4;
	public const int PERSISTED_ZONE_OBJECT = 5;
	public const int EMPTY_OBJ = 10;
}

public class BsonDataRecord : IEquatable<BsonDataRecord>
{
	[BsonField("T1")]
	public string Thingy1;

	[BsonField("T2")]
	public int Thingy2;

	[BsonField("T3")]
	public float Thingy3;

	public bool Equals(BsonDataRecord other)
	{
		if (other == null)
		{
			return false;
		}
		return Thingy1 == other.Thingy1 && Thingy2 == other.Thingy2 && Thingy3 == other.Thingy3;
	}
}

public class CustomMovementStateData : IEquatable<CustomMovementStateData>
{
	public Vector3 StartPos;
	public Vector3 Velocity;

	public bool Equals(CustomMovementStateData other)
	{
		if (other == null)
		{
			return false;
		}
		return StartPos.Equals(other.StartPos) && Velocity.Equals(other.Velocity);
	}
}

public struct CustomMovementStateDataSerializer : IDistributableValueSerializer<CustomMovementStateData>, ICustomPayloadSerializer<CustomMovementStateData>
{
	public void WriteTo(CustomMovementStateData value, BinaryWriter w) => default(CustomSmallNullableSerializer<CustomMovementStateData, CustomMovementStateDataSerializer>).WriteTo(value, w);
	public CustomMovementStateData ReadFrom(BinaryReader r) => default(CustomSmallNullableSerializer<CustomMovementStateData, CustomMovementStateDataSerializer>).ReadFrom(r);

	public void WritePayload(CustomMovementStateData value, BinaryWriter w)
	{
		w.Write(value.StartPos.x);
		w.Write(value.StartPos.y);
		w.Write(value.StartPos.z);
		w.Write(value.Velocity.x);
		w.Write(value.Velocity.y);
		w.Write(value.Velocity.z);
	}

	public CustomMovementStateData ReadPayload(BinaryReader r, int byteCount)
	{
		CustomMovementStateData value = new CustomMovementStateData();	

		value.StartPos.x = r.ReadSingle();
		value.StartPos.y = r.ReadSingle();
		value.StartPos.z = r.ReadSingle();
		value.Velocity.x = r.ReadSingle();
		value.Velocity.y = r.ReadSingle();
		value.Velocity.z = r.ReadSingle();

		return value;
	}

	/// <summary>Converts value to BsonValue</summary>
	public BsonValue ToBsonValue(CustomMovementStateData value)
	{
		BsonDocument doc = new BsonDocument();
		return doc;
	}

	/// <summary>Converts BsonValue to C# type, might throw if incompatible types</summary>
	public CustomMovementStateData FromBsonValue(BsonValue value)
	{
		return new CustomMovementStateData();
	}

	public GameStateEntityPropertyValueType ValueType { get { return GameStateEntityPropertyValueType.CustomSmallNullable; } }
}

[DistributedEntity(TestEntityTypes.EMPTY_OBJ)]
public partial class TestEmptyObj : DistributedObjectBase
{

}

[DistributedEntity(TestEntityTypes.OBJ, FactoryMethod = "DistributedObjFactory")]
public partial class TestDistObj : DistributedObjectBase
{
	enum DistributedPropIds : byte
	{
		POS = 1
	}

	public static IDistributedEntity DistributedObjFactory() { return new TestDistObj(); }

	[Distributed((byte)DistributedPropIds.POS)]
	public DistributedValue<Vector3, Vector3Serializer> Position;

	public TestDistObj()
	{
		Position.OnChanged += OnPositionChanged;
	}

	private void OnPositionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got position change on TestDistObj, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}


[DistributedEntity(TestEntityTypes.PLAYER, FactoryMethod = "TestPlayerFactory")]
public partial class TestPlayer : TestDistObj, IEquatable<TestPlayer>
{
	enum DistributedPropIds : byte
	{
		TESTBOOL = 10,
		DIRECTION = 11,
		FLAGS = 12,
		QUESTS = 13,
		BIGDATA = 14,
		MOVEMENT = 15
	}

	public static IDistributedEntity TestPlayerFactory() { return new TestPlayer(); }

	public TestPlayer()
	{
		TestBool.OnChanged += OnTestBoolChanged;
		Direction.OnChanged += OnDirectionChanged;
		Flags.OnChanged += OnFlagsChanged;
		Quests.OnChanged += OnQuestsChanged;
		BigData.OnChanged += OnBigDataChanged;
		MovementState.OnInitialized += OnMovementStateInitialized;
	}


	[Distributed((byte)DistributedPropIds.TESTBOOL)]
	public DistributedValue<bool, BoolSerializer> TestBool;

	private void OnTestBoolChanged(bool oldValue, bool newValue)
	{
		ImpunityLogger.LogInformation("Got testbool change on TestPlayer, from " + oldValue.ToString() + " to " + newValue.ToString());
	}

	[Distributed((byte)DistributedPropIds.DIRECTION)]
	public DistributedValue<Vector3, Vector3Serializer> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got direction change on TestPlayer, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((byte)DistributedPropIds.FLAGS)]
	public DistributedIntDictionary<string, StringSerializer> Flags;

	private void OnFlagsChanged(int key, string oldFlag, string newFlag)
	{
		ImpunityLogger.LogInformation("Got flags change on TestPlayer, key " + key + " from " + oldFlag + " to " + newFlag);
	}

	[Distributed((byte)DistributedPropIds.QUESTS)]
	public DistributedStringDictionary<string, StringSerializer> Quests;

	private void OnQuestsChanged(string key, string oldQuest, string newQuest)
	{
		ImpunityLogger.LogInformation("Got quests change on TestPlayer, key " + key + " from " + oldQuest + " to " + newQuest);
	}

	[Distributed((byte)DistributedPropIds.BIGDATA)]
	public DistributedValue<BsonDataRecord, BsonSerializer<BsonDataRecord>> BigData;

	[Distributed((byte)DistributedPropIds.MOVEMENT)]
	public DistributedTemporalValue<CustomMovementStateData, CustomMovementStateDataSerializer> MovementState;

	private void OnBigDataChanged(BsonDataRecord oldData, BsonDataRecord newData)
	{
		ImpunityLogger.LogInformation("Got bigdata change on TestPlayer");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnMovementStateInitialized(CustomMovementStateData newData, TimeSpan age)
	{
		ImpunityLogger.LogInformation("Got MovementState initialized on TestPlayer with age " + age.ToString());
		//ImpunityTestComponent.WaitingForCount -= 1;
	}

	public override void OnEventTriggered(int eventType, BsonValue eventData)
	{
		ImpunityLogger.LogInformation("Got event " + eventType + " on TestPlayer with data " + eventData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public override void OnDeleted(BsonValue deleteData)
	{
		ImpunityLogger.LogInformation("Player deleted: " + deleteData?.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public bool Equals(TestPlayer other)
	{
		return TestBool.Equals(other.TestBool)
			&& Direction.Equals(other.Direction)
			&& Flags.Equals(other.Flags)
			&& BigData.Equals(other.BigData)
			&& MovementState.Equals(other.MovementState);
	}
}


[DistributedEntity(TestEntityTypes.ZONE)]
public partial class TestZone : DistributedChannelBase, IEquatable<TestZone>
{
	enum DistributedPropIds : byte
	{
		STATUS = 1,
		SCALAR = 2,
		GRID = 3,
		CHAT = 4
	}

	public TestZone()
	{
		Grid.OnChanged += OnGridChanged;
		Grid.OnReplaced += OnGridReplaced;
		Chat.OnChanged += OnChatChanged;
		Chat.OnReplaced += OnChatReplaced;
	}

	[Distributed((byte)DistributedPropIds.STATUS)]
	public DistributedValue<string, StringSerializer> Status;


	[Distributed((byte)DistributedPropIds.SCALAR)]
	public DistributedValue<float, FloatSerializer> Scalar;

	[Distributed((byte)DistributedPropIds.GRID)]
	public DistributedArray<int, Int32Serializer> Grid;

	private void OnGridChanged(int index, int oldValue, int newValue)
	{
		ImpunityLogger.LogInformation("Got grid change on TestZone, index " + index + " from " + oldValue + " to " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnGridReplaced(int[] oldValue, int[] newValue)
	{
		ImpunityLogger.LogInformation("Got grid replaced on TestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((byte)DistributedPropIds.CHAT)]
	public DistributedQueue<string, StringSerializer> Chat;

	private void OnChatChanged(string newValue)
	{
		ImpunityLogger.LogInformation("Got chat change on TestZone: " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnChatReplaced(Queue<string> oldValue, Queue<string> newValue)
	{
		ImpunityLogger.LogInformation("Got chat replaced on TestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public bool Equals(TestZone other)
	{
		return Status.Equals(other.Status)
				&& Scalar.Equals(other.Scalar)
				&& Grid.Equals(other.Grid);
	}
}

[DistributedEntity(TestEntityTypes.PERSISTED_ZONE, PersistAs = "zone")]
public partial class PersistedTestZone : DistributedChannelBase
{
	enum DistributedPropIds : byte
	{
		STATUS = 1,
		SCALAR = 2,
		GRID = 3,
		CHAT = 4
	}


	public PersistedTestZone()
	{
		Grid.OnChanged += OnGridChanged;
		Grid.OnReplaced += OnGridReplaced;
		Chat.OnChanged += OnChatChanged;
		Chat.OnReplaced += OnChatReplaced;
	}

	[Distributed((byte)DistributedPropIds.STATUS)]
	public DistributedValue<string, StringSerializer> Status;

	[Distributed((byte)DistributedPropIds.SCALAR)]
	public DistributedValue<float, FloatSerializer> Scalar;

	[Distributed((byte)DistributedPropIds.GRID, PersistAs = "grid")]
	public DistributedArray<int, Int32Serializer> Grid;

	private void OnGridChanged(int index, int oldValue, int newValue)
	{
		ImpunityLogger.LogInformation("Got grid change on PersistedTestZone, index " + index + " from " + oldValue + " to " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnGridReplaced(int[] oldValue, int[] newValue)
	{
		ImpunityLogger.LogInformation("Got grid replaced on PersistedTestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((byte)DistributedPropIds.CHAT)]
	public DistributedQueue<string, StringSerializer> Chat;

	private void OnChatChanged(string newValue)
	{
		ImpunityLogger.LogInformation("Got chat change on PersistedTestZone: " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnChatReplaced(Queue<string> oldValue, Queue<string> newValue)
	{
		ImpunityLogger.LogInformation("Got chat replaced on PersistedTestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}

[DistributedEntity(TestEntityTypes.PERSISTED_ZONE_OBJECT, PersistAs = "zobj")]
public partial class ZonePersistedObject : DistributedObjectBase
{
	enum DistributedPropIds : byte
	{
		POSITION = 1,
		DIRECTION = 2,
		FLAGS = 3,
		QUESTS = 4
	}

	public ZonePersistedObject()
	{
		Position.OnChanged += OnPositionChanged;
		Direction.OnChanged += OnDirectionChanged;
		Flags.OnChanged += OnFlagsChanged;
		Quests.OnChanged += OnQuestsChanged;
	}

	[Distributed((byte)DistributedPropIds.POSITION, PersistAs = "pos")]
	public DistributedValue<Vector2Int, Vector2IntSerializer> Position;

	private void OnPositionChanged(Vector2Int oldValue, Vector2Int newValue)
	{
		ImpunityLogger.LogInformation("Got position change on ZonePersistedObject, from " + oldValue.ToString() + " to " + newValue.ToString());
	}

	[Distributed((byte)DistributedPropIds.DIRECTION)]
	public DistributedValue<Vector3, Vector3Serializer> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got direction change on ZonePersistedObject, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((byte)DistributedPropIds.FLAGS, PersistAs = "flags")]
	public DistributedIntDictionary<string, StringSerializer> Flags;

	private void OnFlagsChanged(int key, string oldFlag, string newFlag)
	{
		ImpunityLogger.LogInformation("Got flags change on ZonePersistedObject, key " + key + " from " + oldFlag + " to " + newFlag);
	}

	[Distributed((byte)DistributedPropIds.QUESTS)]
	public DistributedStringDictionary<string, StringSerializer> Quests;

	private void OnQuestsChanged(string key, string oldQuest, string newQuest)
	{
		ImpunityLogger.LogInformation("Got quests change on ZonePersistedObject, key " + key + " from " + oldQuest + " to " + newQuest);
	}

	public override void OnEventTriggered(int eventType, BsonValue eventData)
	{
		ImpunityLogger.LogInformation("Got event " + eventType + " on ZonePersistedObject with data " + eventData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public override void OnDeleted(BsonValue deleteData)
	{
		ImpunityLogger.LogInformation("ZonePersistedObject deleted: " + deleteData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}
