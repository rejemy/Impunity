// The WebRTC leg of the TransportSuite, and the tests of the data channel itself. Every client here is an
// RtcWebSocketTestClient: a WebSocket to the real standalone server plus a libdatachannel peer, standing in for a
// browser. OpenConnectionAsync waits for the channel to open, so the whole transport battery runs with unguaranteed
// delivery on the data channel and everything else on the socket.
// dotnet-only (child process, P/Invoke); never compiled by Unity.
#nullable disable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;

namespace Impunity.Tests
{
	public abstract class TransportSuite_StandaloneRtcBase : TransportSuite_StandaloneBase
	{
		protected readonly Dictionary<BaseGameConnection, RtcWebSocketTestClient> Transports = new Dictionary<BaseGameConnection, RtcWebSocketTestClient>();

		/// <summary>Whether a new connection waits for its data channel before the test goes on.</summary>
		protected virtual bool ExpectChannel => true;

		protected override async Task<BaseGameConnection> OpenConnectionAsync()
		{
			var transport = new RtcWebSocketTestClient("127.0.0.1", Server.HttpPort, StandaloneServerFixture.RuntimesDirectory);
			var conn = new RemoteGameConnection(transport, Server.WorldId, Server.Password, Format, Options, null);
			Transports[conn] = transport;
			await ConnectAsync(conn);

			if (ExpectChannel)
			{
				await PumpUntil(() => transport.SupportsUnguaranteed || transport.UnavailableReason != null, TimeSpan.FromSeconds(10), AllConnections());
				Assert.IsTrue(transport.SupportsUnguaranteed, "The data channel never opened: " + (transport.UnavailableReason ?? "no reason given")
					+ "\n--- server output ---\n" + Server.CapturedOutput());
			}
			return conn;
		}

		/// <summary>Two clients on one channel: A created it, B subscribed. Returns both views of the channel entity.</summary>
		protected async Task<(IntegrationTestChannel a, IntegrationTestChannel b, RtcWebSocketTestClient aTransport, RtcWebSocketTestClient bTransport)> TwoClients()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			string channelName = Name("rtc");
			var a = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var b = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());
			return (a, b, Transports[connA], Transports[connB]);
		}
	}

	[Category("Standalone"), Category("WebRtc")]
	public class TransportSuite_StandaloneRtc : TransportSuite_StandaloneRtcBase
	{
		/// <summary>An unguaranteed update goes up the writer's data channel and comes down the listener's; a guaranteed
		/// one uses the sockets.</summary>
		[Test]
		public async Task UnguaranteedUpdate_TravelsOverTheDataChannel()
		{
			var (a, b, aTransport, bTransport) = await TwoClients();

			int bSocketBefore = bTransport.SocketEntityUpdatesReceived;
			a.Status.SetUnguaranteed("moving");
			await PumpUntil(() => b.Status.Get() == "moving" && a.Status.Get() == "moving", TimeSpan.FromSeconds(3), AllConnections());

			Assert.AreEqual(1, aTransport.ChannelSent, "the writer did not send on its data channel");
			Assert.AreEqual(0, aTransport.SocketUnguaranteedSent);
			Assert.AreEqual(1, bTransport.ChannelReceived, "the listener was not sent the relay on its data channel");
			Assert.AreEqual(1, aTransport.ChannelReceived, "the writer's own echo did not come back on its data channel");
			Assert.AreEqual(bSocketBefore, bTransport.SocketEntityUpdatesReceived, "the relay also went over the socket");

			a.Status.Set("stopped");
			await PumpUntil(() => b.Status.Get() == "stopped", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(1, aTransport.ChannelSent, "a guaranteed update went out on the data channel");
			Assert.AreEqual(1, bTransport.ChannelReceived, "a guaranteed update was relayed on the data channel");
			Assert.AreEqual(bSocketBefore + 1, bTransport.SocketEntityUpdatesReceived);
		}

		/// <summary>An unguaranteed update too big for one packet uses the sockets both ways, and still arrives.</summary>
		[Test]
		public async Task OversizeUnguaranteedUpdate_UsesTheSocket()
		{
			var (a, b, aTransport, bTransport) = await TwoClients();

			string big = new string('x', ImpunityConstants.MaxUnguaranteedMessageSize * 2);
			a.Status.SetUnguaranteed(big);
			await PumpUntil(() => b.Status.Get() == big, TimeSpan.FromSeconds(3), AllConnections());

			Assert.AreEqual(0, aTransport.ChannelSent);
			Assert.AreEqual(1, aTransport.SocketUnguaranteedSent);
			Assert.AreEqual(0, bTransport.ChannelReceived);
		}

		/// <summary>Idle well past the silence limit: keepalives in both directions hold the channel open, and it still
		/// carries an update afterwards.</summary>
		[Test, Category("Slow")]
		public async Task IdleChannel_StaysOpenOnKeepAlives()
		{
			var (a, b, aTransport, bTransport) = await TwoClients();

			await PumpFor(TimeSpan.FromSeconds(7), AllConnections());

			Assert.IsTrue(aTransport.SupportsUnguaranteed && bTransport.SupportsUnguaranteed,
				"an idle channel was closed: " + aTransport.ChannelClosedReason + " / " + bTransport.ChannelClosedReason);
			Assert.GreaterOrEqual(bTransport.ChannelKeepAlivesReceived, 3, "the server did not keep the channel alive");

			a.Status.SetUnguaranteed("still here");
			await PumpUntil(() => b.Status.Get() == "still here", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(1, bTransport.ChannelReceived);
		}

		/// <summary>The listener's UDP path dies while its WebSocket lives. Both ends notice the silence and go back to
		/// the socket; the connection itself is untouched.</summary>
		[Test, Category("Slow")]
		public async Task DeadChannel_FallsBackToTheSocket()
		{
			var (a, b, aTransport, bTransport) = await TwoClients();

			bTransport.Blackhole = true;
			await PumpUntil(() => bTransport.ChannelClosedReason != null, TimeSpan.FromSeconds(10), AllConnections());
			// Whichever end counts five silent seconds first closes the channel; the other sees it close. (The test's
			// blackhole is above the transport, so a real close from the server still gets through.)
			CollectionAssert.Contains(new[] { "silent", "channel closed" }, bTransport.ChannelClosedReason);
			Assert.IsFalse(bTransport.SupportsUnguaranteed);

			// The server gives up on the same silence. Until it has, relays to B go into the dead channel and are lost,
			// as unguaranteed updates may be; so keep sending until one gets through, which has to be by socket.
			int socketBefore = bTransport.SocketEntityUpdatesReceived;
			int sent = 0;
			var deadline = System.Diagnostics.Stopwatch.StartNew();
			while (!(b.Status.Get() ?? "").StartsWith("try") && deadline.Elapsed < TimeSpan.FromSeconds(10))
			{
				a.Status.SetUnguaranteed("try" + (++sent));
				await PumpFor(TimeSpan.FromMilliseconds(100), AllConnections());
			}

			StringAssert.StartsWith("try", b.Status.Get(), "no update reached the listener after its channel died");
			Assert.Greater(bTransport.SocketEntityUpdatesReceived, socketBefore, "the update did not come over the socket");
			Assert.AreEqual(0, bTransport.ChannelReceived);
			Assert.IsTrue(aTransport.SupportsUnguaranteed, "the writer's own channel should be unaffected");

			foreach (var connection in AllConnections())
			{
				Assert.IsTrue(connection.Connected, "losing the data channel must not cost the connection");
			}
		}

		/// <summary>A client that drops its peer on purpose: the server sees the channel close and uses the socket.</summary>
		[Test]
		public async Task ClosedChannel_FallsBackToTheSocket()
		{
			var (a, b, aTransport, bTransport) = await TwoClients();

			bTransport.ClosePeer("test");
			Assert.IsFalse(bTransport.SupportsUnguaranteed);

			int socketBefore = bTransport.SocketEntityUpdatesReceived;
			int sent = 0;
			var deadline = System.Diagnostics.Stopwatch.StartNew();
			while (!(b.Status.Get() ?? "").StartsWith("try") && deadline.Elapsed < TimeSpan.FromSeconds(10))
			{
				a.Status.SetUnguaranteed("try" + (++sent));
				await PumpFor(TimeSpan.FromMilliseconds(100), AllConnections());
			}

			StringAssert.StartsWith("try", b.Status.Get(), "no update reached the listener after it closed its channel");
			Assert.Greater(bTransport.SocketEntityUpdatesReceived, socketBefore);

			// And the other way: B's own unguaranteed writes now leave by socket.
			b.Status.SetUnguaranteed("from b");
			await PumpUntil(() => a.Status.Get() == "from b", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(0, bTransport.ChannelSent);
			Assert.AreEqual(1, bTransport.SocketUnguaranteedSent);
		}
	}

	/// <summary>A server with WebRTC switched off: the same client connects and works, entirely over its WebSocket.</summary>
	[Category("Standalone"), Category("WebRtc")]
	public class WebRtcRefused_Standalone : TransportSuite_StandaloneRtcBase
	{
		protected override bool ExpectChannel => false;

		[OneTimeSetUp]
		public void RelaunchWithoutWebRtc()
		{
			Server?.Dispose();
			Server = StandaloneServerFixture.Launch("\"enabled\": false");
		}

		[Test]
		public async Task RefusedClient_WorksOverTheSocket()
		{
			var (a, b, aTransport, bTransport) = await TwoClients();

			await PumpUntil(() => aTransport.UnavailableReason != null && bTransport.UnavailableReason != null, TimeSpan.FromSeconds(5), AllConnections());
			Assert.AreEqual("disabled", aTransport.UnavailableReason);
			Assert.IsFalse(aTransport.SupportsUnguaranteed);

			a.Status.SetUnguaranteed("moving");
			await PumpUntil(() => b.Status.Get() == "moving", TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(1, aTransport.SocketUnguaranteedSent);
			Assert.AreEqual(0, aTransport.ChannelSent + bTransport.ChannelReceived);
		}
	}
}
