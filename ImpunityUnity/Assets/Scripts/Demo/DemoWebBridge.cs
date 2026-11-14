// How the demo is started and driven from outside the game.
//
// Desktop builds and the editor read the command line. A WebGL build reads its page address, and also talks to the
// page through Assets/Plugins/ImpunityDemo.jslib: the page (or a test driving it) queues dev-console lines with
// window.impunityDemo.run("demo spawn 16"), reads their output from window.impunityDemo.results, and reads the
// demo's state from window.impunityDemo.status. A browser page can't host the Pipeline server the editor tooling
// uses, so this is the WebGL build's equivalent of `tools/ucmd game …` and `tools/ucmd status`.
using System;
using System.Collections.Generic;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

using UnityEngine;

using Dreamwing.Cons;

namespace ImpunityDemo
{
	public static class DemoWebBridge
	{
#if UNITY_WEBGL && !UNITY_EDITOR
		[DllImport("__Internal")]
		private static extern void ImpunityDemoInitialize();
		[DllImport("__Internal")]
		private static extern string ImpunityDemoTakeCommand();
		[DllImport("__Internal")]
		private static extern void ImpunityDemoPostResult(string command, string output, int failed);
		[DllImport("__Internal")]
		private static extern void ImpunityDemoSetStatus(string json);
#endif

		public static void Start(DemoGame game)
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			ImpunityDemoInitialize();
#endif
		}

#if UNITY_WEBGL && !UNITY_EDITOR
		// Lines that are running. Most finish at once; some (the `net server …` queries) answer on a later frame.
		private static readonly List<KeyValuePair<string, System.Threading.Tasks.Task<(string output, bool failed)>>> Running
			= new List<KeyValuePair<string, System.Threading.Tasks.Task<(string output, bool failed)>>>();
#endif

		/// <summary>Starts the console lines the page has queued since the last frame, and reports the ones that have
		/// finished.</summary>
		public static void Pump()
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			for (int i = 0; i < 8; i++)
			{
				string command = ImpunityDemoTakeCommand();
				if (string.IsNullOrEmpty(command))
				{
					break;
				}

				try
				{
					Running.Add(new KeyValuePair<string, System.Threading.Tasks.Task<(string output, bool failed)>>(
						command, Cons.RunCommandCapturingOutputAsync(command, 20f)));
				}
				catch (Exception e)
				{
					ImpunityDemoPostResult(command, e.GetType().Name + ": " + e.Message, 1);
				}
			}

			for (int i = 0; i < Running.Count; i++)
			{
				var task = Running[i].Value;
				if (!task.IsCompleted)
				{
					continue;
				}

				if (task.IsFaulted || task.IsCanceled)
				{
					ImpunityDemoPostResult(Running[i].Key, task.Exception != null ? task.Exception.GetBaseException().Message : "cancelled", 1);
				}
				else
				{
					ImpunityDemoPostResult(Running[i].Key, task.Result.output ?? "", task.Result.failed ? 1 : 0);
				}
				Running.RemoveAt(i--);
			}
#endif
		}

		public static void PublishStatus(string json)
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			ImpunityDemoSetStatus(json);
#endif
		}

		/// <summary>Startup settings by name: from the page address's query string on WebGL (<c>?server=…&amp;spawn=16</c>),
		/// from the command line elsewhere (<c>-server … -spawn 16</c>).</summary>
		public static Dictionary<string, string> StartupArguments()
		{
			var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#if UNITY_WEBGL && !UNITY_EDITOR
			string url = Application.absoluteURL ?? "";
			int question = url.IndexOf('?');
			if (question >= 0)
			{
				string query = url.Substring(question + 1);
				int hash = query.IndexOf('#');
				if (hash >= 0)
				{
					query = query.Substring(0, hash);
				}
				foreach (string pair in query.Split('&'))
				{
					int equals = pair.IndexOf('=');
					if (equals > 0)
					{
						arguments[Uri.UnescapeDataString(pair.Substring(0, equals))] = Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' '));
					}
				}
			}
#else
			string[] args = Environment.GetCommandLineArgs();
			foreach (string name in new[] { "server", "world", "password", "spawn", "rate", "mode" })
			{
				int index = Array.IndexOf(args, "-" + name);
				if (index >= 0 && index + 1 < args.Length)
				{
					arguments[name] = args[index + 1];
				}
			}
#endif
			return arguments;
		}
	}
}
