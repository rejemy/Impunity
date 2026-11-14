using UnityEngine;

using Unity.Pipeline;

namespace DevTools.Cli
{
	// Starts the Pipeline runtime server in development player builds, so tools/ucmd --runtime (and a
	// multiplayer harness) can drive a built game like they drive the editor. Project Settings ▸ Pipeline ▸
	// Runtime has enableInBuilds on but autoStart OFF: with autoStart on, every editor play session
	// would also start a runtime server (a second one, beside the editor's) and write its port file
	// into the project root. So builds start it here instead. This assembly only exists in the
	// editor and development builds; release builds contain no Pipeline code at all.
	internal static class RuntimePipelineStarter
	{
#if !UNITY_EDITOR && !UNITY_WEBGL
		// Not on WebGL: a browser page can't open a listening socket, so the server only logs errors there.
		// After RuntimePipelineBootstrap (BeforeSceneLoad) has created the driver
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void StartInBuild()
		{
			RuntimePipelineDriver driver = RuntimePipelineBootstrap.Instance;
			if (driver == null)
			{
				Debug.LogWarning("Pipeline runtime server unavailable: enable it in Project Settings ▸ Pipeline ▸ Runtime (enableInBuilds)");
				return;
			}
			driver.StartServer();
		}
#endif
	}
}
