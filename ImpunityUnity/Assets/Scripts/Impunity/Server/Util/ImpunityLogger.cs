using System;

namespace Impunity
{
	public interface IImpunityLogger
	{
		void LogTrace(string message);
		void LogTrace(Exception exception, string message);

		void LogDebug(string message);
		void LogDebug(Exception exception, string message);

		void LogInformation(string message);
		void LogInformation(Exception exception, string message);

		void LogWarning(string message);
		void LogWarning(Exception exception, string message);

		void LogError(string message);
		void LogError(Exception exception, string message);

		void LogCritical(string message);
		void LogCritical(Exception exception, string message);
	}

	public static class ImpunityLogger
	{
		public static IImpunityLogger LoggerInstance;

		public static void LogTrace(string message)
		{
			LoggerInstance?.LogTrace(message);
		}

		public static void LogTrace(Exception exception, string message)
		{
			LoggerInstance?.LogTrace(exception, message);
		}

		public static void LogDebug(string message)
		{
			LoggerInstance?.LogDebug(message);
		}

		public static void LogDebug(Exception exception, string message)
		{
			LoggerInstance?.LogTrace(exception, message);
		}

		public static void LogInformation(string message)
		{
			LoggerInstance?.LogInformation(message);
		}

		public static void LogInformation(Exception exception, string message)
		{
			LoggerInstance?.LogInformation(exception, message);
		}

		public static void LogWarning(string message)
		{
			LoggerInstance?.LogWarning(message);
		}

		public static void LogWarning(Exception exception, string message)
		{
			LoggerInstance?.LogWarning(exception, message);
		}

		public static void LogError(string message)
		{
			LoggerInstance?.LogError(message);
		}

		public static void LogError(Exception exception, string message)
		{
			LoggerInstance?.LogError(exception, message);
		}

		public static void LogCritical(string message)
		{
			LoggerInstance?.LogCritical(message);
		}

		public static void LogCritical(Exception exception, string message)
		{
			LoggerInstance?.LogCritical(exception, message);
		}


	}
}
