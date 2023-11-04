using System;
using Impunity.GameState;
using UltraLiteDB;

namespace Impunity
{
	public delegate void ImpunityCallback(ImpunityErrorResponse err);
	public delegate void ImpunityCallback<TReturn>(ImpunityErrorResponse err, TReturn returnValue);

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
		public bool RemoteUpgradeAllowed = false;
		public bool LANDiscoverable = false;
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

	public enum ImpunityInternalCollectionIds
	{
		Entities = 1
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

	public enum ImpunityErrorCode
	{
		UnknownError = 0,
		InternalServerError = 1,

		ClientUnableToConnectError = 1000,
		ClientConnectionBrokenError = 1001,

		ServerUnavailable = 2000, // New connections to the server are temporarily paused
		ServerPasswordIncorrect = 2001, // Attempt to connect to password protected server with the wrong password
		ServerVersionIncompatible = 2002, // Client is not the same version as the server

		ActionInvalidParameter = 3000,
		ActionBadRequest = 3001,
		ActionCompoundFailure = 3002
	}

	public class GameMetadata
	{
		[BsonId]
		public string Id;

		[BsonField("Version")]
		public int Version;

		[BsonField("DataFormatChecksum")]
		public string DataFormatChecksum;

		[BsonField("Collections")]
		public GameStateCollection[] Collections;

		[BsonField("EntityTypes")]
		public GameStateEntityTypeDef[] EntityTypes;
	}

	public class ImpunityServerException : Exception
	{
		public ImpunityErrorCode ErrorCode { get; private set; }

		public ImpunityServerException(ImpunityErrorCode errorCode, string message) : base(message)
		{
			ErrorCode = errorCode;
		}
	}

	public class ImpunityServerFatalException : ImpunityServerException
	{
		public ImpunityServerFatalException(ImpunityErrorCode errorCode, string message) : base(errorCode, message)
		{
			
		}
	}


	public class ImpunityErrorResponse
	{
		[BsonField("err")]
		protected int ErrorInt { get; private set; }

		[BsonIgnore]
		public ImpunityErrorCode ErrorCode {
			get { return (ImpunityErrorCode)ErrorInt; }
			set { ErrorInt = (int)value; }
		}


		[BsonField("msg")]
		public string Message { get; private set; }
		[BsonField("stk")]
		public string Stacktrace { get; private set; }

		public ImpunityErrorResponse() {}

		public ImpunityErrorResponse(Exception e)
		{
			if(e is ImpunityServerException ise)
			{
				ErrorCode = ise.ErrorCode;
			}
			else
			{
				ErrorCode = ImpunityErrorCode.InternalServerError;
			}
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityServerException e)
		{
			ErrorCode = e.ErrorCode;
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityErrorCode code, Exception e)
		{
			ErrorCode = code;
			Message = e.Message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityErrorCode code, string message, Exception e)
		{
			ErrorCode = code;
			Message = message;
			Stacktrace = e.StackTrace;
		}

		public ImpunityErrorResponse(ImpunityErrorCode code, string message)
		{
			ErrorCode = code;
			Message = message;
		}

	}

	public class ImpuntyErrorResponseException : Exception
	{
		public ImpunityErrorCode ErrorId { get; private set; }
		public string ServerStacktrace { get; private set; }

		public ImpuntyErrorResponseException(ImpunityErrorResponse err) : base(err.Message)
		{
			ErrorId = err.ErrorCode;
			ServerStacktrace = err.Stacktrace;	
		}

		public override string ToString()
        {
			return "Error " + ErrorId + ": " + Message + "\n" + ServerStacktrace;
        }
	}

	internal interface IGameStateListener
    {
		void OnGameMetadataChanged(GameStateServer game);
		void OnGameSummaryChanged(GameStateServer game);
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
		public GameStateEntityTypeDef[] EntityTypes;

		public GameStateFormatData()
		{ }


		public GameStateFormatData(GameStateFormat format, GameStateEntityTypeDef[] entityTypes)
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

				if (Collections[0].Index < 10)
				{
					throw new Exception("Can't use 0 - 9 as a collection index");
				}
			}

			// Entity types should be pre-sorted from ClientEntityManager
			EntityTypes = entityTypes;

			DataChecksum = ImpunityUtil.MakeDataChecksum(this);
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

		[BsonField("pa")]
		public string PersistedAs;
	}

	public class GameStateEntityTypeDef
    {
		[BsonField("id")]
		public int Index;

		[BsonField("n")]
		public string Name;

		[BsonField("pa")]
		public string PersistedAs;

		[BsonField("ps")]
		public GameStateEntityPropertyDef[] Properties;
	}


}