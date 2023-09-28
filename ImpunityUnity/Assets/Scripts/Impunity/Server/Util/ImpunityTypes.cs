using System;
using Impunity.Networking;
using UltraLiteDB;

namespace Impunity
{
	public delegate void ImpunityCallback(ImpunityError err);
	public delegate void ImpunityCallback<TReturn>(ImpunityError err, TReturn returnValue);

	public static class ImpunityConstants
	{
		public const string ImpunityVersion = "1";
		public const ushort DefaultServerPort = 29654;
		public const ushort DefaultClientPort = 29655;
		public const int MinMessageSize = 4096;
		public const int MaxMessageSize = 65000;
		public const string ServerSearchPacketHeader = "IMP" + ImpunityVersion + "_SRCH:";
		public const string ServerAnnouncePacketHeader = "IMP" + ImpunityVersion + "_ANNC:";
	}

	public class ImpunityOptions
	{
		public string DBPassword = null;
		public bool RemoteUpgradeAllows = false;
		public bool LANDiscoverable = false;
		public string NetworkPassword = null;
		public ushort ServerPort = ImpunityConstants.DefaultServerPort;
		public ushort ClientPort = ImpunityConstants.DefaultClientPort;
		public string GameTypeCode = "IMP";
	}

	[Flags]
	public enum ImpunityInstanceFlags : byte
	{
		None = 0,
		ClientAuthoritative = 1
	}

	public enum ImpunityLogLevel
	{
		TRACE = 1,
		DEBUG = 2,
		INFO = 3,
		WARN = 4,
		ERROR = 5,
		CRITICAL = 6
	}

	public class ImpunityError
	{
		[BsonField("msg")]
		public string Message { get; private set; }
		[BsonField("stk")]
		public string Stacktrace { get; private set; }

		public ImpunityError() {}

		public ImpunityError(string message, string stackTrace = null)
		{
			Message = message;
			Stacktrace = stackTrace;
		}

		public ImpunityError(Exception e)
		{
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

	}

	public class ImpuntyErrorException : Exception
	{
		string Stacktrace;

		public ImpuntyErrorException(ImpunityError err) : base(err.Message)
		{
			Stacktrace = err.Stacktrace;	
		}

		public override string ToString()
        {
			return Message + "\n" + Stacktrace;
        }
	}

	internal interface IGameStateListener
    {
		void OnGameStateFormatChanged(int version, string dataChecksum);
		void OnGameSummaryChanged(BsonDocument summary);
    }

	public class GameStateFormat
	{
		public int Version;

		public GameStateCollection[] Collections;

		public Type[] EntityTypes;


		public GameStateFormat(int version, GameStateCollection[] collections, Type[] entityTypes)
		{
			Version = version;

			Collections = collections;
			EntityTypes = entityTypes;
		}
	}

	public class GameStateFormatData
	{
		[BsonField("v")]
		public int Version;

		[BsonField("dc")]
		public string DataChecksum;

		[BsonField("cs")]
		public GameStateCollection[] Collections;

		[BsonField("es")]
		public GameStateEntityType[] EntityTypes;

		public GameStateFormatData()
		{ }


		public GameStateFormatData(GameStateFormat format, GameStateEntityType[] entityTypes)
		{
			Version = format.Version;

			Collections = format.Collections;
			if (Collections != null && Collections.Length > 0)
			{
				Array.Sort(Collections,
					(c1, c2) =>
					{
						return c1.Index - c2.Index;
					}
				);

				if (Collections[0].Index == 0)
				{
					throw new Exception("Can't use 0 as a collection index");
				}
			}

			// Entity types should be pre-sorted from ClientEntityManager
			EntityTypes = entityTypes;

			DataChecksum = ImpunityNetworkingUtil.MakeDataChecksum(this);
		}

	}

	public class GameStateCollection
	{
		[BsonField("i")]
		public int Index;

		[BsonField("n")]
		public string Name;

	}

	public enum GameStateEntityPropertyValueType : byte
	{
		Boolean = 1,

		Int8 = 2,
		UInt8 = 3,
		Int16 = 4,
		UInt16 = 5,
		Int32 = 6,
		UInt32 = 7,
		Int64 = 8,
		UInt64 = 9,

		Float = 10,
		Double = 11,
		Decimal = 12,

		Char = 13,
		String = 14,
		Blob = 15,

		DateTime = 16,
		TimeSpan = 17,
		Guid = 18,

		CustomSmall = 100,
		CustomSmallNullable = 101,
		Custom = 102,
		CustomNullable = 103
	}

	public enum GameStateEntityFieldType : byte
	{
		Value = 1,
		Array = 2,
		Queue = 3,
		IntDictionary = 4,
		StringDictionary = 5
	}

	public enum DistributedCollectionUpdateType : byte
	{
		None = 0,
		Set = 1,
		Update = 2
	}

	public class GameStateEntityPropertyDef
    {
		[BsonField("id")]
		public int Index;

		[BsonField("n")]
		public string Name;

		[BsonField("ft")]
		public byte FieldType = (byte)GameStateEntityFieldType.Value;

		[BsonField("v")]
		public byte PropValueType;
	}

	public class GameStateEntityType
    {
		[BsonField("id")]
		public int Index;

		[BsonField("n")]
		public string Name;

		[BsonField("ps")]
		public GameStateEntityPropertyDef[] Properties;
	}


}