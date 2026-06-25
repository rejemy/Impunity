using System;
using System.Threading;

using UnityEngine;


namespace Impunity.Unity
{

	/// <summary>
	/// Impunity logger implementation that routes log output to Unity's <see cref="Debug"/> console.
	/// Prepends thread name for messages originating from background threads.
	/// </summary>
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

		/// <summary>Installs this logger as the global Impunity logger at the specified log level.</summary>
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

		public void LogTrace(string message, Exception exception)
		{
			if (LogLevel > ImpunityLogLevel.TRACE)
				return;

			Debug.Log(ThreadName() + message + "\n" + exception.ToString());
		}

		public void LogDebug(string message)
		{
			if (LogLevel > ImpunityLogLevel.DEBUG)
				return;

			Debug.Log(ThreadName() + message);
		}

		public void LogDebug(string message, Exception exception)
		{
			if (LogLevel > ImpunityLogLevel.DEBUG)
				return;

			Debug.Log(ThreadName() + message + "\n" + exception.ToString());
		}

		public void LogInformation(string message)
		{
			if (LogLevel > ImpunityLogLevel.INFO)
				return;

			Debug.Log(ThreadName() + message);
		}

		public void LogInformation(string message, Exception exception)
		{
			if (LogLevel > ImpunityLogLevel.INFO)
				return;

			Debug.Log(ThreadName() + message + "\n" + exception.ToString());
		}

		public void LogWarning(string message)
		{
			if (LogLevel > ImpunityLogLevel.WARN)
				return;

			Debug.LogWarning(ThreadName() + message);
		}

		public void LogWarning(string message, Exception exception)
		{
			if (LogLevel > ImpunityLogLevel.WARN)
				return;

			Debug.LogWarning(ThreadName() + message + "\n" + exception.ToString());
		}

		public void LogError(string message)
		{
			if (LogLevel > ImpunityLogLevel.ERROR)
				return;

			Debug.LogError(ThreadName() + message);
		}

		public void LogError(string message, Exception exception)
		{
			if (LogLevel > ImpunityLogLevel.ERROR)
				return;

			Debug.LogError(ThreadName() + message + "\n" + exception.ToString());
		}

		public void LogCritical(string message)
		{
			Debug.LogError(ThreadName() + message);
		}

		public void LogCritical(string message, Exception exception)
		{
			Debug.LogError(ThreadName() + message + "\n" + exception.ToString());
		}
	}

}
