// Silence detection against the real standalone server over WebSocket. The WS transport closes a dropped client by
// cancelling its read loop rather than closing a socket, so it gets its own end-to-end check. Runs at the real 10s
// minimum window (the out-of-proc server can't use the test floor), hence Slow. dotnet-only.
#nullable disable

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

namespace Impunity.Tests
{
	[Category("Standalone"), Category("WebSocket"), Category("KeepAlive"), Category("Slow")]
	public class ConnectionKeepAlive_StandaloneWs : ImpunityTestHarness
	{
		StandaloneServerFixture Server;

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

		[OneTimeSetUp]
		public void LaunchServer()
		{
			Server = StandaloneServerFixture.Launch();
		}

		[OneTimeTearDown]
		public void StopServer()
		{
			Server?.Dispose();
			Server = null;
		}

		async Task<RemoteGameConnection> Connect(string key, ImpunityCallback onError)
		{
			var conn = Track(RemoteGameConnection.MakeWebsocketRemoteConnection(
				"127.0.0.1", Server.HttpPort, Server.WorldId, Server.Password, Format, Options));
			conn.ConnectionKey = key;
			conn.OnNetworkError = onError;
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected, "Connection to standalone server failed.\n--- server output ---\n" + Server.CapturedOutput());
			return conn;
		}

		[Test]
		public async Task SilentWebSocketClientIsDroppedAndItsLockHandedOn()
		{
			ImpunityErrorResponse waiterError = null;
			var holder = await Connect("holder", _ => { });
			var waiter = await Connect("waiter", err => waiterError = err);
			Assert.AreEqual(Server.ConnectionTimeoutMillis, waiter.ConnectionTimeoutMillis);

			string lockName = "ws_keepalive_" + Guid.NewGuid().ToString("N").Substring(0, 8);
			Assert.IsTrue(await Pump(holder.TryToLockAsync(lockName), holder, waiter));

			holder.KeepAlivesSuppressed = true;
			AbandonConnection(holder);
			var sw = Stopwatch.StartNew();
			try
			{
				// The waiter sits idle (bar keepalives) for the whole window, so this also checks it isn't dropped.
				var result = await Pump(waiter.WaitForLockAsync(lockName), TimeSpan.FromMilliseconds(Server.ConnectionTimeoutMillis * 2), waiter);
				sw.Stop();

				Assert.AreEqual(LockWaitResult.Locked, result, "\n--- server output ---\n" + Server.CapturedOutput());
				Assert.GreaterOrEqual(sw.ElapsedMilliseconds, Server.ConnectionTimeoutMillis - 500);
				Assert.Less(sw.ElapsedMilliseconds, Server.ConnectionTimeoutMillis + 2000);
				Assert.IsNull(waiterError, "The healthy waiter reported an error: " + waiterError?.Message);
				Assert.IsTrue(waiter.Connected);
			}
			finally
			{
				holder.Dispose();
			}
		}
	}
}
