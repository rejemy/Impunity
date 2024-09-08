using System;
using System.Threading;
using Microsoft.Extensions.Logging;


namespace Impunity.StandaloneServer;


public class ImpunityAspLogger : IImpunityLogger
{
	ImpunityLogLevel LogLevel;
	ILogger Logger;

	private static string ThreadName()
	{
		if (Thread.CurrentThread.Name != null && Thread.CurrentThread.Name.Length > 0)
		{
			return "[On " + Thread.CurrentThread.Name + " thread] ";
		}

		return "";
	}

	public static LogLevel GetAspLogLevel(ImpunityLogLevel logLevel)
	{
		switch (logLevel)
		{
			case ImpunityLogLevel.TRACE:
				return Microsoft.Extensions.Logging.LogLevel.Trace;
			case ImpunityLogLevel.DEBUG:
				return Microsoft.Extensions.Logging.LogLevel.Debug;
			case ImpunityLogLevel.INFO:
				return Microsoft.Extensions.Logging.LogLevel.Information;
			case ImpunityLogLevel.WARN:
				return Microsoft.Extensions.Logging.LogLevel.Warning;
			case ImpunityLogLevel.ERROR:
				return Microsoft.Extensions.Logging.LogLevel.Error;
			case ImpunityLogLevel.CRITICAL:
				return Microsoft.Extensions.Logging.LogLevel.Critical;
		}

		return Microsoft.Extensions.Logging.LogLevel.Information;
	}

	private ImpunityAspLogger(ILogger logger, ImpunityLogLevel logLevel)
	{
		Logger = logger;
		LogLevel = logLevel;
	}

	public static void Setup(ILogger logger, ImpunityLogLevel logLevel)
	{
		ImpunityLogger.LoggerInstance = new ImpunityAspLogger(logger, logLevel);
	}

	public void LogTrace(string message)
	{
		if (LogLevel > ImpunityLogLevel.TRACE)
			return;

		Logger.LogTrace(ThreadName() + message);
	}

	public void LogTrace(string message, Exception exception)
	{
		if (LogLevel > ImpunityLogLevel.TRACE)
			return;

		Logger.LogTrace(ThreadName() + message + "\n" + exception.ToString());
	}

	public void LogDebug(string message)
	{
		if (LogLevel > ImpunityLogLevel.DEBUG)
			return;

		Logger.LogDebug(ThreadName() + message);
	}

	public void LogDebug(string message, Exception exception)
	{
		if (LogLevel > ImpunityLogLevel.DEBUG)
			return;

		Logger.LogDebug(ThreadName() + message + "\n" + exception.ToString());
	}

	public void LogInformation(string message)
	{
		if (LogLevel > ImpunityLogLevel.INFO)
			return;

		Logger.LogInformation(ThreadName() + message);
	}

	public void LogInformation(string message, Exception exception)
	{
		if (LogLevel > ImpunityLogLevel.INFO)
			return;

		Logger.LogInformation(ThreadName() + message + "\n" + exception.ToString());
	}

	public void LogWarning(string message)
	{
		if (LogLevel > ImpunityLogLevel.WARN)
			return;

		Logger.LogWarning(ThreadName() + message);
	}

	public void LogWarning(string message, Exception exception)
	{
		if (LogLevel > ImpunityLogLevel.WARN)
			return;

		Logger.LogWarning(ThreadName() + message + "\n" + exception.ToString());
	}

	public void LogError(string message)
	{
		if (LogLevel > ImpunityLogLevel.ERROR)
			return;

		Logger.LogError(ThreadName() + message);
	}

	public void LogError(string message, Exception exception)
	{
		if (LogLevel > ImpunityLogLevel.ERROR)
			return;

		Logger.LogError(ThreadName() + message + "\n" + exception.ToString());
	}

	public void LogCritical(string message)
	{
		Logger.LogCritical(ThreadName() + message);
	}

	public void LogCritical(string message, Exception exception)
	{
		Logger.LogCritical(ThreadName() + message + "\n" + exception.ToString());
	}
}

