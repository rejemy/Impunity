using System;

using UnityEngine;

namespace Dreamwing.Cons
{
	public class UnityDebugInterceptor : ILogHandler
	{
		private ILogHandler DefaultLogger;

		// Re-entrancy guard. Delivering a message to Cons can itself make Unity
		// emit a log synchronously (e.g. the uGUI Text sink overflows its vertex
		// limit during a layout rebuild and logs an error), which routes straight
		// back here. Without this guard that feedback loop recurses until the stack
		// overflows. While we're already handling a message we still forward to the
		// default logger (so nothing is lost from the native log) but skip the Cons
		// echo. ThreadStatic because logs can arrive from background threads too.
		[ThreadStatic]
		private static bool Handling;

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

			if (Handling)
				return;

			Handling = true;
			try
			{
				string message = String.Format(format, args);
				Cons.LogMessage(logType, message);
			}
			finally
			{
				Handling = false;
			}
		}

		public void LogException(Exception exception, UnityEngine.Object context)
		{
			DefaultLogger.LogException(exception, context);

			if (Handling)
				return;

			Handling = true;
			try
			{
				Cons.LogException(exception);
			}
			finally
			{
				Handling = false;
			}
		}
	}
}
