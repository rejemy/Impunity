// Connection keepalives and silence detection (ImpunityOptions.ConnectionTimeoutMillis): healthy idle connections stay
// up, a client that goes silent is dropped by the server (releasing what it held), and a client notices a silent or
// closed server. dotnet-only: lowers the timeout floor and suppresses keepalives through internal seams
// (InternalsVisibleTo in ImpunityRuntime.csproj), so the window here is 1.5s instead of the 10s minimum.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	[Category("KeepAlive")]
	public class ConnectionKeepAliveTests : ImpunityTestHarness
	{
		const int TimeoutMillis = 1500;

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = IntegrationTestCollections.ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(IntegrationTestEntity),
					typeof(IntegrationTestChannel)
				}
			);
		}

		protected override void ConfigureOptions(ImpunityOptions options)
		{
			options.ConnectionTimeoutFloorMillis = TimeoutMillis;
			options.ConnectionTimeoutMillis = TimeoutMillis;
		}

		readonly List<RemoteGameConnection> Abandoned = new List<RemoteGameConnection>();

		[TearDown]
		public void DisposeAbandoned()
		{
			foreach (var conn in Abandoned)
			{
				try { conn.Dispose(); } catch { }
			}
			Abandoned.Clear();
		}

		async Task StartServer()
		{
			CreateServer();
			StartTcpServer();
			await Task.Delay(100);
		}

		async Task<RemoteGameConnection> Connect(string connectionKey, List<ImpunityErrorResponse> errors)
		{
			var conn = Track(RemoteGameConnection.MakeTCPRemoteConnection(TcpServer.TCPEndpoint, "test", null, Format, Options));
			conn.ConnectionKey = connectionKey;
			conn.OnNetworkError = (err) => errors.Add(err);
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected, "Connection failed");
			return conn;
		}

		// Stops a client cold without closing its socket: no keepalives, no Update() (so no clock sync or entity flush).
		// From the server's side this is a machine that lost power or dropped off the network.
		void GoSilent(RemoteGameConnection conn)
		{
			conn.KeepAlivesSuppressed = true;
			AbandonConnection(conn);
			Abandoned.Add(conn);
		}

		[Test]
		public void TimeoutBelowFloorIsRaisedAndZeroDisables()
		{
			Assert.AreEqual(ImpunityKeepAlive.MinTimeoutMillis, ImpunityKeepAlive.EffectiveTimeoutMillis(new ImpunityOptions { ConnectionTimeoutMillis = 2000 }));
			Assert.AreEqual(45000, ImpunityKeepAlive.EffectiveTimeoutMillis(new ImpunityOptions { ConnectionTimeoutMillis = 45000 }));
			Assert.AreEqual(0, ImpunityKeepAlive.EffectiveTimeoutMillis(new ImpunityOptions { ConnectionTimeoutMillis = 0 }));
			Assert.AreEqual(0, ImpunityKeepAlive.EffectiveTimeoutMillis(new ImpunityOptions { ConnectionTimeoutMillis = -1 }));
			Assert.AreEqual(30000, ImpunityKeepAlive.EffectiveTimeoutMillis(new ImpunityOptions()), "Default should be 30s");
		}

		[Test]
		public async Task HandshakeHandsTheServerTimeoutToTheClient()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			Assert.AreEqual(TimeoutMillis, TcpServer.ConnectionTimeoutMillis);
			Assert.AreEqual(TimeoutMillis, conn.ConnectionTimeoutMillis);
		}

		[Test]
		public async Task ClientAdoptsTheServerTimeoutNotItsOwn()
		{
			await StartServer();

			// The client's own options say something else entirely; the server's value must win.
			var clientOptions = new ImpunityOptions { GameTypeCode = Options.GameTypeCode, ConnectionTimeoutMillis = 120000 };
			var conn = Track(RemoteGameConnection.MakeTCPRemoteConnection(TcpServer.TCPEndpoint, "test", null, Format, clientOptions));
			await Pump(conn.ConnectAsync(), conn);

			Assert.AreEqual(TimeoutMillis, conn.ConnectionTimeoutMillis);
		}

		[Test]
		public async Task DisabledTimeoutIsReportedAsZero()
		{
			Options.ConnectionTimeoutMillis = 0;
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			Assert.AreEqual(0, TcpServer.ConnectionTimeoutMillis);
			Assert.AreEqual(0, conn.ConnectionTimeoutMillis);

			await PumpFor(TimeSpan.FromMilliseconds(TimeoutMillis * 2), conn);
			Assert.IsTrue(conn.Connected);
			Assert.AreEqual(0, errors.Count);
		}

		[Test, Category("Slow")]
		public async Task IdleConnectionStaysUp()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			// Several full windows with no application traffic at all: only keepalives keep it alive.
			await PumpFor(TimeSpan.FromMilliseconds(TimeoutMillis * 4), conn);

			Assert.AreEqual(0, errors.Count, "Idle connection reported an error: " + (errors.Count > 0 ? errors[0].Message : ""));
			Assert.IsTrue(conn.Connected);
			Assert.AreEqual(1, TcpServer.NumConnections);
			Assert.IsNotNull(await Pump(conn.GetGameSummaryAsync(), conn));
		}

		[Test, Category("Slow")]
		public async Task MainThreadStallIsNotADisconnect()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			// No Update() for several windows, like a long level load. Keepalives go out on the writer thread and inbound
			// traffic is timestamped on the socket thread, so neither end should see silence.
			await Task.Delay(TimeoutMillis * 4);

			PumpOnce(conn);
			Assert.AreEqual(0, errors.Count, "Stalled connection reported an error: " + (errors.Count > 0 ? errors[0].Message : ""));
			Assert.IsTrue(conn.Connected);
			Assert.AreEqual(1, TcpServer.NumConnections);
			Assert.IsNotNull(await Pump(conn.GetGameSummaryAsync(), conn));
		}

		[Test]
		public async Task SilentClientIsDroppedAndItsLockHandedOn()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var holder = await Connect("holder", errors);
			var waiter = await Connect("waiter", errors);

			string lockName = "keepalive_lock";
			Assert.IsTrue(await Pump(holder.TryToLockAsync(lockName), holder, waiter));
			Assert.IsFalse(await Pump(waiter.TryToLockAsync(lockName), holder, waiter));

			GoSilent(holder);
			var sw = Stopwatch.StartNew();

			// The waiter queues for the lock; the server hands it over when it drops the silent holder.
			var result = await Pump(waiter.WaitForLockAsync(lockName), TimeSpan.FromMilliseconds(TimeoutMillis * 4), waiter);
			sw.Stop();

			Assert.AreEqual(LockWaitResult.Locked, result);
			Assert.GreaterOrEqual(sw.ElapsedMilliseconds, TimeoutMillis - 200, "Holder was dropped before its window ran out");
			Assert.Less(sw.ElapsedMilliseconds, TimeoutMillis + 1000, "Holder took too long to be dropped");
			await PumpUntil(() => TcpServer.NumConnections == 1, waiter);

			Assert.IsTrue(waiter.Connected);
			Assert.AreEqual(0, errors.Count, "The healthy waiter reported an error: " + (errors.Count > 0 ? errors[0].Message : ""));
		}

		[Test]
		public async Task SilentServerIsDetectedByTheClient()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			TcpServer.KeepAlivesSuppressed = true;
			var sw = Stopwatch.StartNew();
			await PumpUntil(() => errors.Count > 0, TimeSpan.FromMilliseconds(TimeoutMillis * 4), conn);
			sw.Stop();

			Assert.AreEqual(1, errors.Count);
			Assert.AreEqual(ImpunityErrorCode.ClientConnectionBrokenError, errors[0].ErrorCode);
			Assert.Less(sw.ElapsedMilliseconds, TimeoutMillis + 1000);
			Assert.IsFalse(conn.Connected);

			// Further actions fail straight away rather than waiting out ActionTimeoutMillis.
			var after = Stopwatch.StartNew();
			var err = await PumpExpectingError(conn.GetGameSummaryAsync(), conn);
			Assert.IsNotNull(err);
			Assert.AreEqual(ImpunityErrorCode.ClientConnectionBrokenError, err.ErrorId);
			Assert.Less(after.ElapsedMilliseconds, 1000);

			// Closing a lost connection is still fine, and doesn't report a second time.
			DisposeConnection(conn);
			await PumpFor(TimeSpan.FromMilliseconds(100), conn);
			Assert.AreEqual(1, errors.Count);
		}

		[Test]
		public async Task ServerCloseIsReportedToTheClient()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			TcpServer.Dispose();
			TcpServer = null;

			await PumpUntil(() => errors.Count > 0, TimeSpan.FromSeconds(5), conn);
			await PumpFor(TimeSpan.FromMilliseconds(200), conn);

			Assert.AreEqual(1, errors.Count, "A lost connection should be reported exactly once");
			Assert.AreEqual(ImpunityErrorCode.ClientConnectionBrokenError, errors[0].ErrorCode);
			Assert.IsFalse(conn.Connected);
		}

		[Test]
		public async Task ClientDisposeIsNotReportedAsAnError()
		{
			await StartServer();
			var errors = new List<ImpunityErrorResponse>();
			var conn = await Connect("a", errors);

			DisposeConnection(conn);
			await PumpFor(TimeSpan.FromMilliseconds(TimeoutMillis * 2), conn);

			Assert.AreEqual(0, errors.Count);
		}
	}
}
