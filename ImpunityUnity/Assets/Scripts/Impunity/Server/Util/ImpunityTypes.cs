using System;

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
		public const int MaxMessageSize = 65000;
		public const string ServerSearchPacketHeader = "IMP" + ImpunityVersion + "_SRCH:";
		public const string ServerAnnouncePacketHeader = "IMP" + ImpunityVersion + "_ANNC:";
	}

	public class ImpunityOptions
	{
		public bool LANDiscoverable = false;
		public ushort ServerPort = ImpunityConstants.DefaultServerPort;
		public ushort ClientPort = ImpunityConstants.DefaultClientPort;
		public string GameTypeCode = "IMP";
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
	}

	internal interface IGameStateListener
    {
		void OnGameSummaryChanged(BsonDocument summary);
    }

	public class GameStateFormat
	{
		[BsonField("v")]
		public int Version;

		[BsonField("cs")]
		public GameStateCollection[] Collections;

		[BsonField("es")]
		public GameStateEntityType[] EntityTypes;

		public GameStateFormat()
		{
		}

		public GameStateFormat(int version, GameStateCollection[] collections, GameStateEntityType[] entityTypes)
		{
			Version = version;

			Collections = collections;
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


			EntityTypes = entityTypes;
			if (EntityTypes != null && EntityTypes.Length > 0)
			{
				Array.Sort(EntityTypes,
					(e1, e2) =>
					{
						return e1.Index - e2.Index;
					}
				);

				if (EntityTypes[0].Index == 0)
				{
					throw new Exception("Can't use 0 as a entity type Id");
				}
			}
		}
	}

	public class GameStateCollection
	{
		[BsonField("i")]
		public int Index;

		[BsonField("n")]
		public string Name;

	}

	public enum GameStateEntityPropertyValueType
	{
		Boolean = 1,

		Int8 = 2,
		Int16 = 3,
		Int32 = 4,
		Int64 = 5,

		Float = 6,
		Double = 7,

		String = 8,
		Binary = 9,

		Vector2 = 10,
		Vector3 = 11,
		Vector4 = 12,
		Quaternion = 13,

		Color3 = 14,
		Color4 = 15
	}

	public enum GameStateEntityPropertyType
	{
		Value = 1,
		Array = 2,
		Queue = 3
	}

	public class GameStateEntityPropertyDef
    {
		[BsonField("n")]
		public string Name;

		[BsonField("pt")]
		public byte PropType = (byte)GameStateEntityPropertyType.Value;

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