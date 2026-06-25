using System;

using UnityEngine;

namespace Dreamwing.Cons
{
	public class UnityDebugInterceptor : ILogHandler
	{
		private ILogHandler DefaultLogger;

		public static void Init()
		{
			if (Debug.unityLogger.logHandler == null || Debug.unityLogger.logHandler is UnityDebugInterceptor)
			{
				return;
			}

			Debug.unityLogger.logHandler = new UnityDebugInterceptor(Debug.unityLogger.logHandler);
		}

		private UnityDebugInterceptor(ILogHandler defaultLogger)
		{
			DefaultLogger = defaultLogger;
		}

		public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
		{
			DefaultLogger.LogFormat(logType, context, format, args);

			string message = String.Format(format, args);
			Cons.LogMessage(logType, message);
		}

		public void LogException(Exception exception, UnityEngine.Object context)
		{
			DefaultLogger.LogException(exception, context);
			Cons.LogException(exception);
		}
	}
}
