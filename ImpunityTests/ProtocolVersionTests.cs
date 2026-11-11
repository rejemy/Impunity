// The connect handshake's protocol check: a client whose ImpunityConstants.ImpunityVersion differs from the server's
// is refused with ServerProtocolIncompatible, and the server keeps accepting matching clients. dotnet-only: plays an
// other-version client through the internal BaseGameConnection.ImpunityVersionOverride seam. The standalone TCP and
// WebSocket legs are in Host/ProtocolVersion_Standalone.cs.
#nullable disable

using System;
using System.Threading.Tasks;

using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

namespace Impunity.Tests
{
	[Category("ProtocolVersion")]
	public class ProtocolVersionTests : ImpunityTestHarness
	{
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

		[Test]
		public async Task MismatchedProtocolVersionIsRefusedOverTcp()
		{
			CreateServer();
			StartTcpServer();
			await Task.Delay(100);

			var stale = Track(RemoteGameConnection.MakeTCPRemoteConnection(TcpServer.TCPEndpoint, "test", null, Format, Options));
			stale.ImpunityVersionOverride = "0-test";
			var error = await PumpExpectingError(stale.ConnectAsync(), stale);

			Assert.IsNotNull(error, "A client with a different protocol version connected");
			Assert.AreEqual(ImpunityErrorCode.ServerProtocolIncompatible, error.ErrorId);
			Assert.IsFalse(stale.Connected);

			// The refusal is per connection: a matching client still gets in.
			var current = Track(RemoteGameConnection.MakeTCPRemoteConnection(TcpServer.TCPEndpoint, "test", null, Format, Options));
			await Pump(current.ConnectAsync(), current);
			Assert.IsTrue(current.Connected);
		}

		[Test]
		public async Task LocalConnectionsSkipTheProtocolCheck()
		{
			CreateServer();

			var local = Track(new LocalGameConnection(GameServer, Format));
			local.ImpunityVersionOverride = "0-test";
			await Pump(local.ConnectAsync(), local);
			Assert.IsTrue(local.Connected);
		}
	}
}
