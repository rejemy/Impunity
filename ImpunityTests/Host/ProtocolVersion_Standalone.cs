// The connect handshake's protocol check against the real standalone server, over raw TCP and over WebSocket: a
// client with a different ImpunityConstants.ImpunityVersion is refused with ServerProtocolIncompatible. dotnet-only
// (out-of-proc server, internal ImpunityVersionOverride seam).
#nullable disable

using System;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

namespace Impunity.Tests
{
	[Category("Standalone"), Category("WebSocket"), Category("ProtocolVersion")]
	public class ProtocolVersion_Standalone : ImpunityTestHarness
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

		RemoteGameConnection MakeTcp() => Track(RemoteGameConnection.MakeTCPRemoteConnection(
			"127.0.0.1", Server.TcpPort, Server.WorldId, Server.Password, Format, Options));

		RemoteGameConnection MakeWs() => Track(RemoteGameConnection.MakeWebsocketRemoteConnection(
			"127.0.0.1", Server.HttpPort, Server.WorldId, Server.Password, Format, Options));

		async Task AssertRefusedThenMatchingConnects(Func<RemoteGameConnection> make)
		{
			var stale = make();
			stale.ImpunityVersionOverride = "0-test";
			var error = await PumpExpectingError(stale.ConnectAsync(), stale);

			Assert.IsNotNull(error, "A client with a different protocol version connected");
			Assert.AreEqual(ImpunityErrorCode.ServerProtocolIncompatible, error.ErrorId, "\n--- server output ---\n" + Server.CapturedOutput());
			Assert.IsFalse(stale.Connected);

			var current = make();
			await Pump(current.ConnectAsync(), current);
			Assert.IsTrue(current.Connected, "Matching client failed to connect.\n--- server output ---\n" + Server.CapturedOutput());
		}

		[Test]
		public Task MismatchedProtocolVersionIsRefusedOverTcp() => AssertRefusedThenMatchingConnects(MakeTcp);

		[Test]
		public Task MismatchedProtocolVersionIsRefusedOverWebSocket() => AssertRefusedThenMatchingConnects(MakeWs);
	}
}
