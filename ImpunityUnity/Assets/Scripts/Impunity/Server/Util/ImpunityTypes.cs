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
	}

	public class GameStateCollection
	{
		[BsonField("n")]
		public string Name;
	}


}