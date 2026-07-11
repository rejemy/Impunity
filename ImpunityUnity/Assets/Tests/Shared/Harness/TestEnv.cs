namespace Impunity.Tests
{
	/// <summary>Environment shims for test code that runs both under `dotnet test` and inside Unity.
	/// This is the only file in the shared test tree allowed to reference UnityEngine.</summary>
	public static class TestEnv
	{
#if UNITY_5_3_OR_NEWER
		/// <summary>Root directory for per-test temp data.</summary>
		public static string TempRoot => UnityEngine.Application.temporaryCachePath;

		/// <summary>Logs an error to the host environment's error output.</summary>
		public static void LogError(string msg) => UnityEngine.Debug.LogError(msg);
#else
		/// <summary>Root directory for per-test temp data.</summary>
		public static string TempRoot => System.IO.Path.GetTempPath();

		/// <summary>Logs an error to the host environment's error output.</summary>
		public static void LogError(string msg) => System.Console.Error.WriteLine(msg);
#endif
	}
}
