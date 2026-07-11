// Launches the real ImpunityStandaloneServer binary as a child process for the standalone transport
// legs. Out-of-proc on purpose: the standalone server compiles its own copy of the Impunity.GameState
// server types, so referencing its assembly next to ImpunityRuntime would collide — and this way the
// tests exercise the actual deployed artifact (config parsing, WorldService, ASP.NET pipeline, /ws).
//
// dotnet-only: this file lives outside the Shared tree and is never compiled by Unity.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Impunity.Tests
{
	public sealed class StandaloneServerFixture : IDisposable
	{
		public ushort HttpPort { get; private set; }
		public ushort TcpPort { get; private set; }
		public string WorldId => "testworld";
		public string Password => "pw";

		Process Proc;
		string WorkDir;
		readonly StringBuilder Output = new StringBuilder();
		readonly object OutputLock = new object();

		// Belt-and-braces orphan guard: kill any still-running servers if the test host exits abruptly.
		static readonly List<Process> LiveProcesses = new List<Process>();
		static StandaloneServerFixture()
		{
			AppDomain.CurrentDomain.ProcessExit += (s, e) =>
			{
				lock (LiveProcesses)
				{
					foreach (var p in LiveProcesses)
					{
						try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
					}
				}
			};
		}

		public static StandaloneServerFixture Launch()
		{
			var fixture = new StandaloneServerFixture();
			Exception lastFailure = null;
			for (int attempt = 0; attempt < 3; attempt++)
			{
				try
				{
					fixture.LaunchOnce();
					return fixture;
				}
				catch (Exception e)
				{
					lastFailure = e;
					fixture.StopProcess();
				}
			}
			fixture.Dispose();
			throw new InvalidOperationException(
				"Standalone server failed to start after 3 attempts: " + lastFailure?.Message +
				"\n--- server output ---\n" + fixture.CapturedOutput(), lastFailure);
		}

		/// <summary>Path of the server DLL: IMPUNITY_STANDALONE_DLL env var if set, else the path baked
		/// in by the csproj (always built — the test project declares a build-only ProjectReference).</summary>
		static string ServerDllPath()
		{
			string fromEnv = Environment.GetEnvironmentVariable("IMPUNITY_STANDALONE_DLL");
			if (!string.IsNullOrEmpty(fromEnv)) return Path.GetFullPath(fromEnv);

			foreach (var meta in typeof(StandaloneServerFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
			{
				if (meta.Key == "StandaloneServerDll" && !string.IsNullOrEmpty(meta.Value))
				{
					return Path.GetFullPath(meta.Value);
				}
			}
			throw new InvalidOperationException("StandaloneServerDll assembly metadata missing from test assembly");
		}

		void LaunchOnce()
		{
			string dll = ServerDllPath();
			if (!File.Exists(dll))
			{
				throw new FileNotFoundException(
					"Standalone server binary not found — build it with `dotnet build ImpunityStandaloneServer` " +
					"(building the test project does this automatically) or set IMPUNITY_STANDALONE_DLL.", dll);
			}

			HttpPort = TestPorts.GetFreePort();
			TcpPort = TestPorts.GetFreePort();

			WorkDir = Path.Combine(TestEnv.TempRoot, "ImpunityStandalone_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(WorkDir);

			// Program.cs reads config.json relative to the process working directory.
			string config = "{\n" +
				"  \"game_type_code\": \"Test\",\n" +
				$"  \"http_port\": {HttpPort},\n" +
				$"  \"tcp_port\": {TcpPort},\n" +
				"  \"datapath\": \"worlds\",\n" +
				"  \"worlds\": [\n" +
				$"    {{ \"id\": \"{WorldId}\", \"name\": \"Transport Test World\", \"password\": \"{Password}\", \"max_players\": 16 }}\n" +
				"  ]\n" +
				"}\n";
			File.WriteAllText(Path.Combine(WorkDir, "config.json"), config);

			lock (OutputLock) { Output.Clear(); }

			Proc = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					Arguments = "\"" + dll + "\"",
					WorkingDirectory = WorkDir,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				}
			};
			Proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (OutputLock) Output.AppendLine(e.Data); };
			Proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (OutputLock) Output.AppendLine(e.Data); };
			Proc.Start();
			Proc.BeginOutputReadLine();
			Proc.BeginErrorReadLine();
			lock (LiveProcesses) { LiveProcesses.Add(Proc); }

			WaitUntilReady().GetAwaiter().GetResult();
		}

		async Task WaitUntilReady()
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
			string url = $"http://127.0.0.1:{HttpPort}/info";
			var deadline = Stopwatch.StartNew();

			while (deadline.Elapsed < TimeSpan.FromSeconds(15))
			{
				if (Proc.HasExited)
				{
					throw new InvalidOperationException(
						$"Standalone server exited early with code {Proc.ExitCode} (likely a port bind race)");
				}

				try
				{
					var response = await http.GetAsync(url);
					if (response.IsSuccessStatusCode) return;
				}
				catch (HttpRequestException) { }
				catch (TaskCanceledException) { }

				await Task.Delay(100);
			}

			throw new TimeoutException("Standalone server did not become ready within 15s");
		}

		public string CapturedOutput()
		{
			lock (OutputLock) { return Output.ToString(); }
		}

		void StopProcess()
		{
			if (Proc != null)
			{
				try
				{
					if (!Proc.HasExited)
					{
						Proc.Kill(entireProcessTree: true);
						Proc.WaitForExit(5000);
					}
				}
				catch { }
				lock (LiveProcesses) { LiveProcesses.Remove(Proc); }
				Proc.Dispose();
				Proc = null;
			}

			if (WorkDir != null)
			{
				try { if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); } catch { }
				WorkDir = null;
			}
		}

		public void Dispose()
		{
			StopProcess();
		}
	}
}
