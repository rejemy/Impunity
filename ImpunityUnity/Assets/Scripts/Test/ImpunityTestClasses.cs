using UnityEngine;

using System.Collections.Generic;

using Impunity;
using Impunity.GameState;
using Impunity.Connection;

using UltraLiteDB;


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

[DistributedEntity(TestEntityTypes.EMPTY_OBJ)]
public partial class TestEmptyObj : DistributedEntityBase
{

}

[DistributedEntity(TestEntityTypes.OBJ, FactoryMethod = "DistributedObjFactory")]
public partial class TestDistObj : DistributedEntityBase
{
	enum DistributedPropIds
	{
		POS = 1
	}

	public static IDistributedEntity DistributedObjFactory() { return new TestDistObj(); }

	[Distributed((int)DistributedPropIds.POS, OnChanged = "OnPositionChanged")]
	private DistributedValue<DVector3> Position;

	private void OnPositionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got position change on TestDistObj, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}


[DistributedEntity(TestEntityTypes.PLAYER, FactoryMethod = "TestPlayerFactory")]
public partial class TestPlayer : TestDistObj
{
	enum DistributedPropIds
	{
		TESTBOOL = 10,
		DIRECTION = 11,
		FLAGS = 12,
		QUESTS = 13
	}

	public static IDistributedEntity TestPlayerFactory() { return new TestPlayer(); }

	[Distributed((int)DistributedPropIds.TESTBOOL, OnChanged = "OnTestBoolChanged")]
	private DistributedValue<DBool> TestBool;

	private void OnTestBoolChanged(bool oldValue, bool newValue)
	{
		ImpunityLogger.LogInformation("Got testbool change on TestPlayer, from " + oldValue.ToString() + " to " + newValue.ToString());
	}

	[Distributed((int)DistributedPropIds.DIRECTION, OnChanged = "OnDirectionChanged")]
	private DistributedValue<DVector3> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got direction change on TestPlayer, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.FLAGS, OnChanged = "OnFlagsChanged")]
	private DistributedIntDictionary<DString> Flags;

	private void OnFlagsChanged(int key, string oldFlag, string newFlag)
	{
		ImpunityLogger.LogInformation("Got flags change on TestPlayer, key " + key + " from " + oldFlag + " to " + newFlag);
	}

	[Distributed((int)DistributedPropIds.QUESTS, OnChanged = "OnQuestsChanged")]
	private DistributedStringDictionary<DString> Quests;

	private void OnQuestsChanged(string key, string oldQuest, string newQuest)
	{
		ImpunityLogger.LogInformation("Got quests change on TestPlayer, key " + key + " from " + oldQuest + " to " + newQuest);
	}

	public override void OnEventTriggered(int eventType, BsonValue eventData)
	{
		ImpunityLogger.LogInformation("Got event " + eventType + " on TestPlayer with data " + eventData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	public override void OnDeleted(BsonValue deleteData)
	{
		ImpunityLogger.LogInformation("Player deleted: " + deleteData.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}


[DistributedEntity(TestEntityTypes.ZONE)]
public partial class TestZone : DistributedChannelBase
{
	enum DistributedPropIds
	{
		STATUS = 1,
		SCALAR = 2,
		GRID = 3,
		CHAT = 4
	}

	[Distributed((int)DistributedPropIds.STATUS)]
	private DistributedValue<DString> Status;


	[Distributed((int)DistributedPropIds.SCALAR)]
	private DistributedValue<DFloat> Scalar;

	[Distributed((int)DistributedPropIds.GRID, OnChanged = "OnGridChanged", OnReplaced = "OnGridReplaced")]
	private DistributedArray<DInt32> Grid;

	private void OnGridChanged(int index, int oldValue, int newValue)
	{
		ImpunityLogger.LogInformation("Got grid change on TestZone, index " + index + " from " + oldValue + " to " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnGridReplaced(DInt32[] oldValue, DInt32[] newValue)
	{
		ImpunityLogger.LogInformation("Got grid replaced on TestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.CHAT, OnChanged = "OnChatChanged", OnReplaced = "OnChatReplaced")]
	private DistributedQueue<DString> Chat;

	private void OnChatChanged(string newValue)
	{
		ImpunityLogger.LogInformation("Got chat change on TestZone: " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnChatReplaced(Queue<DString> oldValue, Queue<DString> newValue)
	{
		ImpunityLogger.LogInformation("Got chat replaced on TestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}

[DistributedEntity(TestEntityTypes.PERSISTED_ZONE, PersistAs = "zone")]
public partial class PersistedTestZone : DistributedChannelBase
{
	enum DistributedPropIds
	{
		STATUS = 1,
		SCALAR = 2,
		GRID = 3,
		CHAT = 4
	}

	[Distributed((int)DistributedPropIds.STATUS)]
	private DistributedValue<DString> Status;

	[Distributed((int)DistributedPropIds.SCALAR)]
	private DistributedValue<DFloat> Scalar;

	[Distributed((int)DistributedPropIds.GRID, PersistAs = "grid", OnChanged = "OnGridChanged", OnReplaced = "OnGridReplaced")]
	private DistributedArray<DInt32> Grid;

	private void OnGridChanged(int index, int oldValue, int newValue)
	{
		ImpunityLogger.LogInformation("Got grid change on PersistedTestZone, index " + index + " from " + oldValue + " to " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnGridReplaced(DInt32[] oldValue, DInt32[] newValue)
	{
		ImpunityLogger.LogInformation("Got grid replaced on PersistedTestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.CHAT, OnChanged = "OnChatChanged", OnReplaced = "OnChatReplaced")]
	private DistributedQueue<DString> Chat;

	private void OnChatChanged(string newValue)
	{
		ImpunityLogger.LogInformation("Got chat change on PersistedTestZone: " + newValue);
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	private void OnChatReplaced(Queue<DString> oldValue, Queue<DString> newValue)
	{
		ImpunityLogger.LogInformation("Got chat replaced on PersistedTestZone");
		ImpunityTestComponent.WaitingForCount -= 1;
	}
}

[DistributedEntity(TestEntityTypes.PERSISTED_ZONE_OBJECT, PersistAs = "zobj")]
public partial class ZonePersistedObject : DistributedEntityBase
{
	enum DistributedPropIds
	{
		POSITION = 1,
		DIRECTION = 2,
		FLAGS = 3,
		QUESTS = 4
	}

	[Distributed((int)DistributedPropIds.POSITION, PersistAs = "pos", OnChanged = "OnPositionChanged")]
	private DistributedValue<DVector2Int> Position;

	private void OnPositionChanged(Vector2Int oldValue, Vector2Int newValue)
	{
		ImpunityLogger.LogInformation("Got position change on ZonePersistedObject, from " + oldValue.ToString() + " to " + newValue.ToString());
	}

	[Distributed((int)DistributedPropIds.DIRECTION, OnChanged = "OnDirectionChanged")]
	private DistributedValue<DVector3> Direction;

	private void OnDirectionChanged(Vector3 oldValue, Vector3 newValue)
	{
		ImpunityLogger.LogInformation("Got direction change on ZonePersistedObject, from " + oldValue.ToString() + " to " + newValue.ToString());
		ImpunityTestComponent.WaitingForCount -= 1;
	}

	[Distributed((int)DistributedPropIds.FLAGS, PersistAs = "flags", OnChanged = "OnFlagsChanged")]
	private DistributedIntDictionary<DString> Flags;

	private void OnFlagsChanged(int key, string oldFlag, string newFlag)
	{
		ImpunityLogger.LogInformation("Got flags change on ZonePersistedObject, key " + key + " from " + oldFlag + " to " + newFlag);
	}

	[Distributed((int)DistributedPropIds.QUESTS, OnChanged = "OnQuestsChanged")]
	private DistributedStringDictionary<DString> Quests;

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