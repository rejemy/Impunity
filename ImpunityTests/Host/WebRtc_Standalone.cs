// The standalone server's WebRTC signaling, driven over a raw WebSocket: when the server offers WebRTC, what it
// answers, and that every way of not having it leaves the server running and the client told so. These tests have
// no WebRTC peer of their own (an offer is canned text), so they stop at the answer; a live data channel is covered
// by the TransportSuite leg that brings its own peer.
// dotnet-only: launches the real server binary (StandaloneServerFixture).
#nullable disable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using NUnit.Framework;

namespace Impunity.Tests
{
	[Category("WebRtc")]
	public class WebRtc_Standalone
	{
		StandaloneServerFixture Server;
		readonly List<ClientWebSocket> Sockets = new List<ClientWebSocket>();

		[TearDown]
		public void TearDown()
		{
			foreach (var socket in Sockets)
			{
				try { socket.Abort(); socket.Dispose(); } catch { }
			}
			Sockets.Clear();
			Server?.Dispose();
			Server = null;
		}

		// What a browser sends for a data-channel-only connection, less its candidates. The fingerprint and ICE
		// credentials are made up: nothing will ever connect, the server only has to accept it and answer.
		const string Offer =
			"v=0\r\n" +
			"o=- 4611731400430051336 2 IN IP4 127.0.0.1\r\n" +
			"s=-\r\n" +
			"t=0 0\r\n" +
			"a=group:BUNDLE 0\r\n" +
			"a=msid-semantic: WMS\r\n" +
			"m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n" +
			"c=IN IP4 0.0.0.0\r\n" +
			"a=candidate:1 1 udp 2113937151 198.51.100.9 54321 typ host\r\n" +
			"a=ice-ufrag:Tq3x\r\n" +
			"a=ice-pwd:9vYpGZ3nQ0wq1c5JmXb7LhRd\r\n" +
			"a=ice-options:trickle\r\n" +
			"a=fingerprint:sha-256 3A:4F:11:9C:7E:02:B5:D8:66:21:0A:F3:5C:88:1D:E4:97:30:6B:C2:4D:59:A1:0E:7F:83:2B:D6:15:C9:60:AE\r\n" +
			"a=setup:actpass\r\n" +
			"a=mid:0\r\n" +
			"a=sctp-port:5000\r\n" +
			"a=max-message-size:262144\r\n";

		async Task<ClientWebSocket> OpenSocket()
		{
			var socket = new ClientWebSocket();
			Sockets.Add(socket);
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Server.HttpPort}/ws"), timeout.Token);
			return socket;
		}

		static async Task<JsonElement> Signal(ClientWebSocket socket, string sdp)
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			byte[] request = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["t"] = "offer", ["sdp"] = sdp });
			await socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token);

			byte[] buffer = new byte[64 * 1024];
			int length = 0;
			while (true)
			{
				WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
				Assert.AreNotEqual(WebSocketMessageType.Close, result.MessageType, "server closed the socket instead of answering");
				length += result.Count;
				if (result.EndOfMessage)
				{
					Assert.AreEqual(WebSocketMessageType.Text, result.MessageType, "signaling replies are text messages");
					return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, length)).RootElement.Clone();
				}
			}
		}

		static void AssertUnavailable(JsonElement reply, string reason)
		{
			Assert.AreEqual("unavailable", reply.GetProperty("t").GetString(), reply.ToString());
			Assert.AreEqual(reason, reply.GetProperty("r").GetString());
		}

		/// <summary>With the library in place the server says so, and an offer gets a complete answer: its own
		/// fingerprint and candidates on the configured port, plus the configured public address.</summary>
		[Test]
		public async Task Offer_IsAnsweredWithCandidates()
		{
			Server = StandaloneServerFixture.Launch("\"public_address\": \"203.0.113.7\"");
			StringAssert.Contains("WebRTC available on UDP port " + Server.WebRtcPort, Server.CapturedOutput());

			JsonElement reply = await Signal(await OpenSocket(), Offer);
			Assert.AreEqual("answer", reply.GetProperty("t").GetString(), reply.ToString());
			string sdp = reply.GetProperty("sdp").GetString();

			StringAssert.Contains("a=fingerprint:sha-256 ", sdp);
			StringAssert.DoesNotContain("3A:4F:11:9C", sdp, "the answer must carry the server's fingerprint, not the client's");
			StringAssert.Contains("a=ice-ufrag:", sdp);
			StringAssert.Contains(" 203.0.113.7 " + Server.WebRtcPort + " typ host", sdp);
			StringAssert.DoesNotContain("198.51.100.9", sdp);

			int candidates = 0;
			foreach (string line in sdp.Split('\n'))
			{
				if (line.StartsWith("a=candidate"))
				{
					candidates++;
					StringAssert.Contains(" " + Server.WebRtcPort + " typ host", line, "every candidate shares the one UDP port");
				}
			}
			Assert.GreaterOrEqual(candidates, 2, "expected the public address and at least one local address:\n" + sdp);
		}

		[Test]
		public async Task Disabled_AnswersUnavailable()
		{
			Server = StandaloneServerFixture.Launch("\"enabled\": false");
			StringAssert.Contains("WebRTC disabled by configuration", Server.CapturedOutput());
			AssertUnavailable(await Signal(await OpenSocket(), Offer), "disabled");
		}

		/// <summary>No native library (as on an unsupported platform): the server starts anyway and says no.</summary>
		[Test]
		public async Task MissingLibrary_ServerStartsAndAnswersUnavailable()
		{
			Server = StandaloneServerFixture.Launch(environment: new Dictionary<string, string>
			{
				["IMPUNITY_WEBRTC_RUNTIMES"] = System.IO.Path.Combine(TestEnv.TempRoot, "no_such_runtimes_" + Guid.NewGuid().ToString("N"))
			});
			StringAssert.Contains("WebRTC unavailable, clients will use WebSocket only", Server.CapturedOutput());
			AssertUnavailable(await Signal(await OpenSocket(), Offer), "unavailable");
		}

		/// <summary>Something else holds the UDP port: found at startup, not at the first client.</summary>
		[Test]
		public async Task UdpPortTaken_AnswersUnavailable()
		{
			ushort port = TestPorts.GetFreePort();
			using var squatter = new UdpClient(new IPEndPoint(IPAddress.Any, port));
			Server = StandaloneServerFixture.Launch("\"udp_port\": " + port);
			StringAssert.Contains("UDP port " + port + " can't be bound", Server.CapturedOutput());
			AssertUnavailable(await Signal(await OpenSocket(), Offer), "unavailable");
		}

		/// <summary>An offer that isn't one is refused, and the same socket can then offer properly.</summary>
		[Test]
		public async Task BadOffer_IsRefused_AndTheSocketStillWorks()
		{
			Server = StandaloneServerFixture.Launch();
			ClientWebSocket socket = await OpenSocket();

			AssertUnavailable(await Signal(socket, "this is not SDP"), "failed");
			AssertUnavailable(await Signal(socket, new string('x', 20000)), "failed");

			JsonElement reply = await Signal(socket, Offer);
			Assert.AreEqual("answer", reply.GetProperty("t").GetString(), reply.ToString());
		}

		/// <summary>Peers beyond max_peers are refused; a refused client is simply left on WebSocket.</summary>
		[Test]
		public async Task PeerLimit_RefusesTheNextClient()
		{
			Server = StandaloneServerFixture.Launch("\"max_peers\": 1");

			JsonElement first = await Signal(await OpenSocket(), Offer);
			Assert.AreEqual("answer", first.GetProperty("t").GetString(), first.ToString());

			AssertUnavailable(await Signal(await OpenSocket(), Offer), "full");
		}
	}
}
