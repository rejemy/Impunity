using System;
using System.Threading;

using UnityEngine;


namespace Impunity.Unity
{

	public class ImpunityUnityLogger : IImpunityLogger
	{
		ImpunityLogLevel LogLevel;

		private static string ThreadName()
		{
			if (Thread.CurrentThread.Name != null && Thread.CurrentThread.Name.Length > 0)
			{
				return "[On " + Thread.CurrentThread.Name + " thread] ";
			}

			return "";
		}

		private ImpunityUnityLogger(ImpunityLogLevel logLevel)
		{
			LogLevel = logLevel;
		}

		public static void Setup(ImpunityLogLevel logLevel)
		{
			ImpunityLogger.LoggerInstance = new ImpunityUnityLogger(logLevel);
		}

		public void LogTrace(string message)
		{
			if (LogLevel > ImpunityLogLevel.TRACE)
				return;

			Debug.Log(ThreadName() + message);
		}

		public void LogTrace(Exception exception, string message)
		{
			if (LogLevel > ImpunityLogLevel.TRACE)
				return;

			Debug.Log(ThreadName() + message);
			Debug.LogException(exception);
		}

		public void LogDebug(string message)
		{
			if (LogLevel > ImpunityLogLevel.DEBUG)
				return;

			Debug.Log(ThreadName() + message);
		}

		public void LogDebug(Exception exception, string message)
		{
			if (LogLevel > ImpunityLogLevel.DEBUG)
				return;

			Debug.Log(ThreadName() + message);
			Debug.LogException(exception);
		}

		public void LogInformation(string message)
		{
			if (LogLevel > ImpunityLogLevel.INFO)
				return;

			Debug.Log(ThreadName() + message);
		}

		public void LogInformation(Exception exception, string message)
		{
			if (LogLevel > ImpunityLogLevel.INFO)
				return;

			Debug.Log(ThreadName() + message);
			Debug.LogException(exception);
		}

		public void LogWarning(string message)
		{
			if (LogLevel > ImpunityLogLevel.WARN)
				return;

			Debug.LogWarning(ThreadName() + message);
		}

		public void LogWarning(Exception exception, string message)
		{
			if (LogLevel > ImpunityLogLevel.WARN)
				return;

			Debug.LogWarning(ThreadName() + message);
			Debug.LogException(exception);
		}

		public void LogError(string message)
		{
			if (LogLevel > ImpunityLogLevel.ERROR)
				return;

			Debug.LogError(ThreadName() + message);
		}

		public void LogError(Exception exception, string message)
		{
			if (LogLevel > ImpunityLogLevel.ERROR)
				return;

			Debug.LogError(ThreadName() + message);
			Debug.LogException(exception);
		}

		public void LogCritical(string message)
		{
			Debug.LogError(ThreadName() + message);
		}

		public void LogCritical(Exception exception, string message)
		{
			Debug.LogError(ThreadName() + message);
			Debug.LogException(exception);
		}
	}

}