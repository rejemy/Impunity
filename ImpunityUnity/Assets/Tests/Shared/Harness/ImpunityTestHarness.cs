// Shared base fixture for integration suites. Replaces the per-file scaffolding the old PlayMode
// tests duplicated (server + temp dir setup, connect helpers, coroutine pump helpers).
//
// Tests drive the async client API and pump BaseGameConnection.Update() via the Pump* helpers, which
// work identically under `dotnet test` (threadpool, sequential) and Unity (main-thread sync context).
// Deadlines use Stopwatch — never NUnit [Timeout], which is not enforced on .NET Core.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;

using UltraLiteDB;

namespace Impunity.Tests
{
	public abstract class ImpunityTestHarness
	{
		/// <summary>Default deadline for Pump/PumpUntil, matching the old WaitForYield's 5s.</summary>
		protected static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

		protected string GameStatePath;
		protected ImpunityOptions Options;
		protected GameStateFormat Format;
		protected GameStateEntityTypeDef[] EntityDefs;

		protected GameStateServer GameServer;
		protected ImpunityServer TcpServer;
		protected LocalGameConnection LocalGame;
		protected RemoteGameConnection RemoteGame;

		/// <summary>Every connection made through the harness, in creation order; pumped by the
		/// no-connections Pump* overloads and disposed in teardown.</summary>
		readonly List<BaseGameConnection> Tracked = new List<BaseGameConnection>();

		/// <summary>The schema this fixture's server and clients use.</summary>
		protected abstract GameStateFormat CreateFormat();

		/// <summary>Fixture hook to adjust options (idle timeouts, upgrade policy, …) before use.</summary>
		protected virtual void ConfigureOptions(ImpunityOptions options) { }

		[SetUp]
		public void HarnessSetUp()
		{
			// Process-global state: undo a CleanupAll() from a previous test and make sure field-based
			// BSON mapping is on (idempotent).
			ImpunityLifecycle.Reset();
			BsonMapper.Global.IncludeFields = true;

			GameStatePath = Path.Combine(TestEnv.TempRoot, GetType().Name + "_" + Guid.NewGuid().ToString("N"));

			Options = new ImpunityOptions
			{
				GameTypeCode = "Test",
				ServerPort = TestPorts.GetFreePort()
			};
			ConfigureOptions(Options);

			Format = CreateFormat();
			EntityDefs = new ClientEntityManager().RegisterEntityTypes(Format.EntityTypes);
		}

		[TearDown]
		public void HarnessTearDown()
		{
			foreach (var conn in Tracked)
			{
				try { conn.Dispose(); } catch { }
			}
			Tracked.Clear();
			LocalGame = null;
			RemoteGame = null;

			try { TcpServer?.Dispose(); } catch { }
			TcpServer = null;

			try { GameServer?.Dispose(); } catch { }
			GameServer = null;

			try
			{
				if (Directory.Exists(GameStatePath))
				{
					Directory.Delete(GameStatePath, true);
				}
			}
			catch { }
		}

		// ───────── Server lifecycle ─────────

		protected void CreateServer(string gameId = "test", string password = null)
		{
			var summary = new BsonDocument { ["name"] = GetType().Name };
			GameServer = GameStateServer.Create(gameId, password, GameStatePath, summary, Options);
			GameServer.UpdateFormat(new GameStateFormatData(Format, EntityDefs), false);
		}

		/// <summary>Starts the TCP front-end on Options.ServerPort, retrying with a fresh port if
		/// another process won the bind race (TestPorts probes, but cannot reserve).</summary>
		protected void StartTcpServer()
		{
			for (int attempt = 0; ; attempt++)
			{
				TcpServer = new ImpunityServer(GameServer, Options);
				try
				{
					TcpServer.Start();
					return;
				}
				catch (SocketException)
				{
					try { TcpServer.Dispose(); } catch { }
					TcpServer = null;
					if (attempt >= 4) throw;
					Options.ServerPort = TestPorts.GetFreePort();
				}
			}
		}

		// ───────── Connections ─────────

		/// <summary>Registers a connection for pumping and teardown disposal.</summary>
		protected T Track<T>(T connection) where T : BaseGameConnection
		{
			Tracked.Add(connection);
			return connection;
		}

		/// <summary>Disposes a connection mid-test and stops pumping it.</summary>
		protected void DisposeConnection(BaseGameConnection connection)
		{
			if (connection == null) return;
			Tracked.Remove(connection);
			if (ReferenceEquals(connection, LocalGame)) LocalGame = null;
			if (ReferenceEquals(connection, RemoteGame)) RemoteGame = null;
			connection.Dispose();
		}

		/// <summary>Removes a connection from tracking WITHOUT disposing it — simulates a client crash.
		/// (A graceful Dispose notifies the server; a crash must not.)</summary>
		protected void AbandonConnection(BaseGameConnection connection)
		{
			if (connection == null) return;
			Tracked.Remove(connection);
			if (ReferenceEquals(connection, LocalGame)) LocalGame = null;
			if (ReferenceEquals(connection, RemoteGame)) RemoteGame = null;
		}

		protected async Task ConnectLocal()
		{
			LocalGame = Track(new LocalGameConnection(GameServer, Format));
			await Pump(LocalGame.ConnectAsync(), LocalGame);
			Assert.IsTrue(LocalGame.Connected, "Local connection failed");
		}

		protected async Task StartTCPAndConnectRemote(string gameId = "test", string password = null)
		{
			StartTcpServer();
			await Task.Delay(100); // let the listener threads spin up (was WaitForSeconds(0.1f))

			RemoteGame = Track(RemoteGameConnection.MakeTCPRemoteConnection(
				TcpServer.TCPEndpoint, gameId, password, Format, Options));
			RemoteGame.OnNetworkError = (err) => TestEnv.LogError("Network error: " + err.Message);

			await Pump(RemoteGame.ConnectAsync(), RemoteGame);
			Assert.IsTrue(RemoteGame.Connected, "Remote connection failed");
		}

		protected BaseGameConnection[] AllConnections()
		{
			return Tracked.ToArray();
		}

		// ───────── Pumping ─────────

		/// <summary>Calls Update() once on the given connections, or on every tracked connection when
		/// none are given. Passing an explicit subset is how tests keep a client "stale".</summary>
		protected void PumpOnce(params BaseGameConnection[] connections)
		{
			var toPump = (connections == null || connections.Length == 0) ? Tracked.ToArray() : connections;
			foreach (var c in toPump)
			{
				c?.Update();
			}
		}

		/// <summary>Pumps until the task completes, then propagates its failure (if any) as a test
		/// failure. Replaces WaitForYield/RunTask.</summary>
		protected Task Pump(Task task, params BaseGameConnection[] connections)
			=> Pump(task, DefaultTimeout, connections);

		protected async Task Pump(Task task, TimeSpan timeout, params BaseGameConnection[] connections)
		{
			await PumpUntilComplete(task, timeout, connections);
			await task;
		}

		/// <summary>Pumps until the task completes and returns its value.</summary>
		protected Task<T> Pump<T>(Task<T> task, params BaseGameConnection[] connections)
			=> Pump(task, DefaultTimeout, connections);

		protected async Task<T> Pump<T>(Task<T> task, TimeSpan timeout, params BaseGameConnection[] connections)
		{
			await PumpUntilComplete(task, timeout, connections);
			return await task;
		}

		/// <summary>Pumps until the task completes — success OR failure — without throwing on failure.
		/// For tests that expect an error and inspect the task afterwards.</summary>
		protected Task PumpUntilComplete(Task task, params BaseGameConnection[] connections)
			=> PumpUntilComplete(task, DefaultTimeout, connections);

		protected async Task PumpUntilComplete(Task task, TimeSpan timeout, params BaseGameConnection[] connections)
		{
			var sw = Stopwatch.StartNew();
			while (!task.IsCompleted && sw.Elapsed < timeout)
			{
				PumpOnce(connections);
				await Task.Delay(1);
			}
			if (!task.IsCompleted)
			{
				Assert.Fail("Operation timed out after " + timeout.TotalSeconds + "s");
			}
		}

		/// <summary>Pumps until the task completes and returns the Impunity error it failed with, or
		/// null if it succeeded. Fails the test if it failed with anything other than an Impunity
		/// error. For tests that expect a specific <see cref="ImpunityErrorCode"/>.</summary>
		protected async Task<ImpunityErrorResponseException> PumpExpectingError(Task task, params BaseGameConnection[] connections)
		{
			await PumpUntilComplete(task, connections);
			if (!task.IsFaulted) return null;

			Exception ex = task.Exception;
			while (ex is AggregateException agg && agg.InnerException != null)
			{
				ex = agg.InnerException;
			}
			var impunityError = ex as ImpunityErrorResponseException;
			Assert.IsNotNull(impunityError, "Task failed with an unexpected exception: " + ex);
			return impunityError;
		}

		/// <summary>Pumps until the condition holds, asserting it did within the deadline. Replaces TickUntil.</summary>
		protected Task PumpUntil(Func<bool> condition, params BaseGameConnection[] connections)
			=> PumpUntil(condition, DefaultTimeout, connections);

		protected async Task PumpUntil(Func<bool> condition, TimeSpan timeout, params BaseGameConnection[] connections)
		{
			await PollUntil(condition, timeout, connections);
			Assert.IsTrue(condition(), "Condition not met within " + timeout.TotalSeconds + "s");
		}

		/// <summary>Pumps until the condition holds or the deadline passes, WITHOUT asserting. Replaces PollUntil.</summary>
		protected async Task PollUntil(Func<bool> condition, TimeSpan timeout, params BaseGameConnection[] connections)
		{
			var sw = Stopwatch.StartNew();
			while (!condition() && sw.Elapsed < timeout)
			{
				PumpOnce(connections);
				await Task.Delay(1);
			}
		}

		/// <summary>Pumps for a fixed duration without asserting anything — gives would-be-relayed
		/// messages time to arrive so a test can assert they did NOT. Replaces TickFor/WaitForSeconds.</summary>
		protected async Task PumpFor(TimeSpan duration, params BaseGameConnection[] connections)
		{
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < duration)
			{
				PumpOnce(connections);
				await Task.Delay(1);
			}
		}
	}
}
