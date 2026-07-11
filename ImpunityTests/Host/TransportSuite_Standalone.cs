// Standalone-server legs of the TransportSuite: the same transport-agnostic tests, run against the
// real ImpunityStandaloneServer binary out-of-proc — once over raw TCP, once over the WebSocket
// endpoint (/ws). dotnet-only (child processes; never compiled by Unity).
//
// One server (and one world) is shared across each fixture, which is why TransportSuite uniquifies
// every channel/lock/document name. The world starts with no schema; the first client's handshake
// establishes the v1 format (the server runs with RemoteUpgradeAllowed = true).
#nullable disable

using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;

namespace Impunity.Tests
{
	public abstract class TransportSuite_StandaloneBase : TransportSuite
	{
		protected StandaloneServerFixture Server;

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

		// The server runs out-of-proc for the whole fixture; nothing to do per-test.
		protected override Task EnsureServerAsync() => Task.CompletedTask;

		protected async Task<BaseGameConnection> ConnectAsync(RemoteGameConnection conn)
		{
			Track(conn);
			conn.OnNetworkError = (err) => TestEnv.LogError("Network error: " + err.Message);
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected, "Connection to standalone server failed.\n--- server output ---\n" + Server.CapturedOutput());
			return conn;
		}
	}

	[Category("Standalone")]
	public class TransportSuite_StandaloneTcp : TransportSuite_StandaloneBase
	{
		protected override Task<BaseGameConnection> OpenConnectionAsync()
		{
			return ConnectAsync(RemoteGameConnection.MakeTCPRemoteConnection(
				"127.0.0.1", Server.TcpPort, Server.WorldId, Server.Password, Format, Options));
		}
	}

	[Category("Standalone"), Category("WebSocket")]
	public class TransportSuite_StandaloneWs : TransportSuite_StandaloneBase
	{
		protected override Task<BaseGameConnection> OpenConnectionAsync()
		{
			return ConnectAsync(RemoteGameConnection.MakeWebsocketRemoteConnection(
				"127.0.0.1", Server.HttpPort, Server.WorldId, Server.Password, Format, Options));
		}
	}
}
