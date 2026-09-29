// Server transport robustness: failures on the edges of a connection's life (LAN discovery broadcasts the OS
// refuses, pushes queued for a client that has already gone) must neither kill server threads nor log errors.
// dotnet-only: uses internal seams (InternalsVisibleTo in ImpunityRuntime.csproj) and swaps the global logger.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using NUnit.Framework;

using Impunity.GameState;
using Impunity.Networking;

using UltraLiteDB;

namespace Impunity.Tests
{
	/// <summary>Records every log line so tests can assert on levels.</summary>
	internal class CapturingLogger : IImpunityLogger
	{
		public readonly List<(string Level, string Message)> Lines = new List<(string, string)>();

		void Add(string level, string message, Exception e = null)
		{
			lock (Lines)
			{
				Lines.Add((level, e == null ? message : message + " " + e));
			}
		}

		public string Dump()
		{
			lock (Lines)
			{
				return string.Join("\n", Lines.Select(l => l.Level + ": " + l.Message));
			}
		}

		public List<string> At(string level)
		{
			lock (Lines)
			{
				return Lines.Where(l => l.Level == level).Select(l => l.Message).ToList();
			}
		}

		public void LogTrace(string message) => Add("trace", message);
		public void LogTrace(string message, Exception exception) => Add("trace", message, exception);
		public void LogDebug(string message) => Add("debug", message);
		public void LogDebug(string message, Exception exception) => Add("debug", message, exception);
		public void LogInformation(string message) => Add("info", message);
		public void LogInformation(string message, Exception exception) => Add("info", message, exception);
		public void LogWarning(string message) => Add("warning", message);
		public void LogWarning(string message, Exception exception) => Add("warning", message, exception);
		public void LogError(string message) => Add("error", message);
		public void LogError(string message, Exception exception) => Add("error", message, exception);
		public void LogCritical(string message) => Add("critical", message);
		public void LogCritical(string message, Exception exception) => Add("critical", message, exception);
	}

	/// <summary>A server-side client context whose stream can be "disposed" like a closed NetworkStream.</summary>
	internal class FakeClientContext : IImpunityNetworkServerClientContext
	{
		public ImpunityServerMessageHandler OnMessageRecieved { get; set; }
		public ImpunityServerErrorCallback OnNetworkError { get; set; }
		public ImpunityServerClientContextCallback OnClientDisconnected { get; set; }

		public string ConnectionId => "fake";
		public string RemoteAddress => "fake";
		public bool SupportsUnguaranteed => false;

		public bool StreamDisposed;
		public int Sends;

		public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			if (StreamDisposed)
			{
				// What NetworkStream.WriteAsync does once the TcpClient has been closed.
				throw new ObjectDisposedException("System.Net.Sockets.NetworkStream");
			}
			Sends++;
			return Task.CompletedTask;
		}

		public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes) => SendGuaranteedMessageAsync(messageBytes);

		/// <summary>Simulates the peer going away: the stream is disposed, then the transport reports the disconnect.</summary>
		public void Disconnect()
		{
			StreamDisposed = true;
			OnClientDisconnected?.Invoke(this);
		}

		public void Dispose() => Disconnect();
	}

	[TestFixture]
	public class ServerNetworkRobustnessTests : ImpunityTestHarness
	{
		CapturingLogger Log;
		IImpunityLogger PreviousLogger;

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(1, new GameStateCollection[0], new Type[0]);
		}

		[SetUp]
		public void CaptureLogs()
		{
			PreviousLogger = ImpunityLogger.LoggerInstance;
			Log = new CapturingLogger();
			ImpunityLogger.LoggerInstance = Log;
		}

		[TearDown]
		public void RestoreLogger()
		{
			ImpunityLogger.LoggerInstance = PreviousLogger;
		}

		static bool WaitFor(Func<bool> condition, TimeSpan timeout)
		{
			var sw = Stopwatch.StartNew();
			while (!condition())
			{
				if (sw.Elapsed > timeout)
				{
					return false;
				}
				Thread.Sleep(20);
			}
			return true;
		}

		/// <summary>The first LAN announce runs before the UDP receive loop. When the OS refuses the broadcast
		/// (macOS Local Network privacy: "No route to host"), it used to escape and kill the UDP thread, so clients
		/// never got a ping reply and silently fell back to TCP-only.</summary>
		[Test]
		public void UdpListener_AnnounceSendFails_KeepsAnsweringPings()
		{
			Options.LANDiscoverable = true;
			CreateServer();

			TcpServer = new ImpunityServer(GameServer, Options);
			// Sending from the server's IPv4 UDP socket to an IPv6 endpoint always throws.
			TcpServer.TCPTransport.AnnounceEndpoint = new IPEndPoint(IPAddress.IPv6Loopback, Options.ClientPort);
			TcpServer.Start();
			Thread.Sleep(100); // let the listener threads spin up

			// Act as a client: once a TCP session exists, the server echoes UDP pings from that session's endpoint,
			// which is how clients detect unguaranteed delivery. Only a live UDP thread answers.
			// (Raw IPv4 sockets rather than ImpunityTCPClient keep this about the server alone.)
			using (var tcp = new TcpClient(AddressFamily.InterNetwork))
			{
				tcp.Connect(IPAddress.Loopback, Options.ServerPort);
				using (var udp = new UdpClient((IPEndPoint)tcp.Client.LocalEndPoint))
				{
					byte[] ping = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPingPacketHeader + Options.GameTypeCode + ":");
					var serverUdp = new IPEndPoint(IPAddress.Loopback, Options.ServerPort);

					// The session is registered when the accept loop (100ms poll) gets to it; retry the ping until then.
					bool echoed = false;
					var sw = Stopwatch.StartNew();
					while (!echoed && sw.Elapsed < TimeSpan.FromSeconds(3))
					{
						udp.Send(ping, ping.Length, serverUdp);
						if (udp.Client.Poll(200_000, SelectMode.SelectRead))
						{
							IPEndPoint from = null;
							byte[] reply = udp.Receive(ref from);
							echoed = Encoding.UTF8.GetString(reply) == Encoding.UTF8.GetString(ping);
						}
					}

					Assert.IsTrue(echoed, "Server UDP thread stopped answering pings after a failed announce. Log:\n" + Log.Dump());

					// A LAN search makes the server announce again: that failure must stay quiet and non-fatal too.
					byte[] search = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");
					udp.Send(search, search.Length, serverUdp);
					Assert.IsTrue(WaitFor(() => Log.At("debug").Any(m => m.Contains("announce failed again")), TimeSpan.FromSeconds(3)),
						"Search packet should trigger a (failing) re-announce. Log:\n" + Log.Dump());

					udp.Send(ping, ping.Length, serverUdp);
					Assert.IsTrue(udp.Client.Poll(3_000_000, SelectMode.SelectRead), "UDP thread died after the re-announce failure");
				}
			}

			Assert.AreEqual(1, Log.At("warning").Count(m => m.Contains("LAN discovery announce")),
				"Announce failure should be warned about exactly once");
			CollectionAssert.IsEmpty(Log.At("error"));
		}

		/// <summary>The stock client over loopback ends up with UDP. Covers the .NET Core dual-mode socket (IPv4-mapped
		/// local/remote endpoints, which a UdpClient could never use against the IPv4 server) and the accept-loop lag
		/// that drops the first ping.</summary>
		[Test]
		public void TcpClient_Loopback_NegotiatesUdp()
		{
			CreateServer();
			StartTcpServer();

			IImpunityNetworkClient client = ImpunityTCPClient.MakeTCPClient(TcpServer.TCPEndpoint, Options);
			try
			{
				var connected = new ManualResetEventSlim(false);
				client.Connect(_ => connected.Set());
				Assert.IsTrue(connected.Wait(TimeSpan.FromSeconds(5)), "client did not connect");

				// Must beat the server's 1s establish timeout, which closes a connection that never establishes.
				Assert.IsTrue(WaitFor(() => client.SupportsUnguaranteed, TimeSpan.FromSeconds(3)),
					"Client never got a UDP ping reply. Log:\n" + Log.Dump());
			}
			finally
			{
				client.Dispose();
			}
		}

		/// <summary>The client keeps pinging until the server answers: a server that hasn't registered the session yet
		/// drops the first ping.</summary>
		[Test]
		public void TcpClient_FirstPingDropped_RetriesUntilAnswered()
		{
			ushort port = TestPorts.GetFreePort();
			var listener = new TcpListener(IPAddress.Loopback, port);
			listener.Start();
			var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
			byte[] ping = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPingPacketHeader + Options.GameTypeCode + ":");

			int pingsSeen = 0;
			bool stop = false;
			// Poll, don't block in Receive: closing a socket from another thread doesn't unblock Receive on macOS.
			var fakeServer = new Thread(() =>
			{
				try
				{
					while (!Volatile.Read(ref stop))
					{
						if (!serverUdp.Client.Poll(100_000, SelectMode.SelectRead))
						{
							continue;
						}
						IPEndPoint from = null;
						byte[] packet = serverUdp.Receive(ref from);
						if (Encoding.UTF8.GetString(packet) == Encoding.UTF8.GetString(ping) && Interlocked.Increment(ref pingsSeen) > 1)
						{
							serverUdp.Send(ping, ping.Length, from); // answer only the retry
						}
					}
				}
				catch (Exception) { } // socket closed at the end of the test
			}) { IsBackground = true };
			fakeServer.Start();

			IImpunityNetworkClient client = ImpunityTCPClient.MakeTCPClient(new IPEndPoint(IPAddress.Loopback, port), Options);
			TcpClient serverSide = null;
			try
			{
				var connected = new ManualResetEventSlim(false);
				client.Connect(_ => connected.Set());
				serverSide = listener.AcceptTcpClient();
				Assert.IsTrue(connected.Wait(TimeSpan.FromSeconds(5)), "client did not connect");

				Assert.IsTrue(WaitFor(() => client.SupportsUnguaranteed, TimeSpan.FromSeconds(5)),
					"Client gave up on UDP after its first ping went unanswered (pings seen: " + pingsSeen + ")");
			}
			finally
			{
				client.Dispose();
				Volatile.Write(ref stop, true);
				fakeServer.Join(TimeSpan.FromSeconds(2));
				serverSide?.Close();
				listener.Stop();
				serverUdp.Close();
			}
		}

		/// <summary>Pushes queued on the writer thread for a client that has since disconnected are dropped
		/// without touching the disposed stream.</summary>
		[Test]
		public void SendMessage_AfterClientDisconnected_DropsQuietly()
		{
			TcpServer = new ImpunityServer(new List<GameStateServer>(), Options);
			var context = new FakeClientContext();
			TcpServer.ClientConnected(context);

			var proxy = (ServerSideNetworkConnectionProxy)context.OnClientDisconnected.Target;
			proxy.SendMessage((ushort)ServerActionType.CLIENT_REPLY, 1, true, new BsonDocument());
			Assert.AreEqual(1, context.Sends, "send before disconnect should go through");

			context.Disconnect();
			proxy.SendMessage((ushort)ServerActionType.CLIENT_REPLY, 2, true, new BsonDocument());
			proxy.SendMessage((ushort)ServerActionType.CLIENT_REPLY, 3, false, new BsonDocument());

			Assert.AreEqual(1, context.Sends, "sends after disconnect should be dropped");
			CollectionAssert.IsEmpty(Log.At("error"));
			Assert.IsTrue(Log.At("debug").Any(m => m.Contains("closed connection")));

			// A server-requested close after the peer already went away is a no-op, not a second disconnect.
			proxy.ProcessCloseConnection();
			CollectionAssert.IsEmpty(Log.At("error"));
		}

		/// <summary>The stream can be disposed a moment before the transport reports the disconnect; a send landing
		/// in that window is still a closed-connection drop, not an error.</summary>
		[Test]
		public void SendMessage_StreamDisposedBeforeDisconnectReported_LogsAtDebug()
		{
			TcpServer = new ImpunityServer(new List<GameStateServer>(), Options);
			var context = new FakeClientContext();
			TcpServer.ClientConnected(context);
			var proxy = (ServerSideNetworkConnectionProxy)context.OnClientDisconnected.Target;

			context.StreamDisposed = true;
			proxy.SendMessage((ushort)ServerActionType.CLIENT_REPLY, 1, true, new BsonDocument());

			CollectionAssert.IsEmpty(Log.At("error"));
			Assert.IsTrue(Log.At("debug").Any(m => m.Contains("closed connection")));

			// The send lock was released: later sends aren't blocked.
			context.StreamDisposed = false;
			proxy.SendMessage((ushort)ServerActionType.CLIENT_REPLY, 2, true, new BsonDocument());
			Assert.AreEqual(1, context.Sends);
		}
	}
}
