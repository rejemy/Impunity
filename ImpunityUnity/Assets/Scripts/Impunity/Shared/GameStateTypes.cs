using System;

using UltraLiteDB;

namespace Impunity
{
	public delegate void ImpunityActionComplete(ImpunityError err);
	public delegate void ImpunityCallback(ImpunityError err);
	public delegate void ImpunityCallback<TReturn>(ImpunityError err, TReturn returnValue);

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

	public interface ImpunityResult
	{
		ImpunityError Error { get; }
	}

	public interface ImpunityResult<TReturn>
	{
		TReturn Value { get; }
		ImpunityError Error { get; }
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