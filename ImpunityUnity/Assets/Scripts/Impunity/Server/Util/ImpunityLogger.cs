using System;

namespace Impunity
{
	public interface IImpunityLogger
	{
		void LogTrace(string message);
		void LogTrace(string message, Exception exception);

		void LogDebug(string message);
		void LogDebug(string message, Exception exception);

		void LogInformation(string message);
		void LogInformation(string message, Exception exception);

		void LogWarning(string message);
		void LogWarning(string message, Exception exception);

		void LogError(string message);
		void LogError(string message, Exception exception);

		void LogCritical(string message);
		void LogCritical(string message, Exception exception);
	}

	public static class ImpunityLogger
	{
		public static IImpunityLogger LoggerInstance;

		public static void LogTrace(string message)
		{
			LoggerInstance?.LogTrace(message);
		}

		public static void LogTrace(string message, Exception exception)
		{
			LoggerInstance?.LogTrace(message, exception);
		}

		public static void LogDebug(string message)
		{
			LoggerInstance?.LogDebug(message);
		}

		public static void LogDebug(string message, Exception exception)
		{
			LoggerInstance?.LogDebug(message, exception);
		}

		public static void LogInformation(string message)
		{
			LoggerInstance?.LogInformation(message);
		}

		public static void LogInformation(string message, Exception exception)
		{
			LoggerInstance?.LogInformation(message, exception);
		}

		public static void LogWarning(string message)
		{
			LoggerInstance?.LogWarning(message);
		}

		public static void LogWarning(string message, Exception exception)
		{
			LoggerInstance?.LogWarning(message, exception);
		}

		public static void LogError(string message)
		{
			LoggerInstance?.LogError(message);
		}

		public static void LogError(string message, Exception exception)
		{
			LoggerInstance?.LogError(message, exception);
		}

		public static void LogCritical(string message)
		{
			LoggerInstance?.LogCritical(message);
		}

		public static void LogCritical(string message, Exception exception)
		{
			LoggerInstance?.LogCritical(message, exception);
		}


	}
}
