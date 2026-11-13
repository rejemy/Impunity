// Unguaranteed entity updates end to end over real loopback TCP+UDP: the server relays them the way they were sent,
// and neither end puts a message too big for one packet on the datagram path.
// dotnet-only: reads ImpunityTCPClient.UnguaranteedReceived (internal) to tell which path a message arrived by.
#nullable disable

using System;
using System.Threading.Tasks;

using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;

namespace Impunity.Tests
{
	[Category("Unguaranteed")]
	public class UnguaranteedDeliveryTests : ImpunityTestHarness
	{
		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[] { new GameStateCollection { Index = IntegrationTestCollections.ITEMS, Name = "Items" } },
				new Type[] { typeof(IntegrationTestEntity), typeof(IntegrationTestChannel), typeof(IntegrationEmptyEntity), typeof(MigTestChannel), typeof(MigTestObject) });
		}

		async Task<(RemoteGameConnection game, ImpunityTCPClient transport)> ConnectTcp()
		{
			var transport = (ImpunityTCPClient)ImpunityTCPClient.MakeTCPClient(TcpServer.TCPEndpoint, Options);
			var game = Track(new RemoteGameConnection(transport, "test", null, Format, Options, null));
			game.OnNetworkError = (err) => TestEnv.LogError("Network error: " + err.Message);
			await Pump(game.ConnectAsync(), game);
			Assert.IsTrue(game.Connected);
			await PumpUntil(() => transport.SupportsUnguaranteed, TimeSpan.FromSeconds(5), AllConnections());
			return (game, transport);
		}

		async Task<(IntegrationTestEntity writer, IntegrationTestEntity listener, ImpunityTCPClient listenerTransport)> Setup(string channelName)
		{
			CreateServer();
			StartTcpServer();
			await Task.Delay(100);

			var (c1, _) = await ConnectTcp();
			var (c2, c2Transport) = await ConnectTcp();

			var c1Channel = await Pump(c1.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var writer = new IntegrationTestEntity();
			writer.Health.Set(100);
			await Pump(c1.EntityManager.CreateObjectAsync(writer, c1Channel, false), AllConnections());

			var c2Channel = await Pump(c2.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());
			await PumpUntil(() => c2Channel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());
			IntegrationTestEntity listener = null;
			foreach (var obj in c2Channel.DistributedObjects.Values)
			{
				listener = (IntegrationTestEntity)obj;
			}
			return (writer, listener, c2Transport);
		}

		/// <summary>An update sent unguaranteed reaches another UDP-capable client over UDP. The server used to forget
		/// how it was sent and relay everything reliably.</summary>
		[Test]
		public async Task UnguaranteedUpdate_IsRelayedOverUdp()
		{
			var (writer, listener, listenerTransport) = await Setup("ug1");

			int before = listenerTransport.UnguaranteedReceived;
			writer.Health.SetUnguaranteed(42);
			await PumpUntil(() => listener.Health.Get() == 42, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(before + 1, listenerTransport.UnguaranteedReceived, "The relayed update did not arrive over UDP");

			// A guaranteed update still arrives reliably.
			writer.Health.Set(43);
			await PumpUntil(() => listener.Health.Get() == 43, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(before + 1, listenerTransport.UnguaranteedReceived, "A guaranteed update was relayed over UDP");
		}

		/// <summary>An unguaranteed update too big for one packet still arrives, by the reliable path.</summary>
		[Test]
		public async Task UnguaranteedUpdate_OverTheSizeLimit_ArrivesReliably()
		{
			var (writer, listener, listenerTransport) = await Setup("ug2");

			string big = new string('x', ImpunityConstants.MaxUnguaranteedMessageSize * 2);
			int before = listenerTransport.UnguaranteedReceived;
			writer.DisplayName.SetUnguaranteed(big);
			await PumpUntil(() => listener.DisplayName.Get() == big, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(before, listenerTransport.UnguaranteedReceived, "An oversize update was put on UDP");
		}
	}
}
