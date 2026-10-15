// Per-connection send queues on the server: a client that stops reading (dead peer, half-open TCP, full socket buffer)
// must never delay sends to anyone else. Its messages wait in its own queue, in order; a server-requested close still
// flushes them first; and a client whose queue grows past ImpunityOptions.MaxSendBacklogBytes is dropped.
// dotnet-only: drives ServerSideNetworkConnectionProxy through internal seams (InternalsVisibleTo in
// ImpunityRuntime.csproj) and swaps the global logger. Keepalives are disabled throughout, so the silence sweep can't
// rescue a stalled writer and hide the bug.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;

using UltraLiteDB;

namespace Impunity.Tests
{
	/// <summary>
	/// A server-side client context whose writes can be held pending, like a socket whose peer stopped reading: while
	/// <see cref="Stalled"/> is set, every guaranteed write returns a task that only completes when the test releases it
	/// or the connection is closed (which faults it, as closing a socket does). Unguaranteed (UDP) sends always complete
	/// at once. Each write may carry several framed messages; they are recorded individually.
	/// </summary>
	internal class StallableClientContext : IImpunityNetworkServerClientContext
	{
		public ImpunityServerMessageHandler OnMessageRecieved { get; set; }
		public ImpunityServerErrorCallback OnNetworkError { get; set; }
		public ImpunityServerClientContextCallback OnClientDisconnected { get; set; }

		public string ConnectionId { get; }
		public string RemoteAddress => ConnectionId;
		public bool SupportsUnguaranteed => Udp;

		public volatile bool Stalled;
		/// <summary>Whether the client has negotiated UDP.</summary>
		public volatile bool Udp;

		readonly object Gate = new object();
		// Broadcast message type of every message in each guaranteed write started, in order (-1 for anything that isn't
		// a broadcast), and the array each write was handed.
		readonly List<List<int>> Writes = new List<List<int>>();
		readonly List<byte[]> WriteArrays = new List<byte[]>();
		readonly List<int> Unguaranteed = new List<int>();
		readonly Queue<TaskCompletionSource<bool>> Pending = new Queue<TaskCompletionSource<bool>>();
		int Disconnects;
		bool Closed;
		int Concurrent;
		int MaxConcurrent;

		public StallableClientContext(string connectionId)
		{
			ConnectionId = connectionId;
		}

		public ServerSideNetworkConnectionProxy Proxy => (ServerSideNetworkConnectionProxy)OnClientDisconnected.Target;

		/// <summary>Every message in every guaranteed write started so far, in order.</summary>
		public List<int> StartedMessages { get { lock (Gate) { return Writes.SelectMany(w => w).ToList(); } } }
		/// <summary>The messages of each guaranteed write started so far.</summary>
		public List<List<int>> StartedWrites { get { lock (Gate) { return Writes.Select(w => w.ToList()).ToList(); } } }
		public List<byte[]> StartedWriteArrays { get { lock (Gate) { return WriteArrays.ToList(); } } }
		public List<int> UnguaranteedMessages { get { lock (Gate) { return Unguaranteed.ToList(); } } }
		public int DisconnectCount { get { lock (Gate) { return Disconnects; } } }
		/// <summary>Most writes ever pending at once. Transports aren't thread-safe; the proxy must keep this at 1.</summary>
		public int MaxConcurrentWrites { get { lock (Gate) { return MaxConcurrent; } } }

		public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			lock (Gate)
			{
				if (Closed)
				{
					throw new ObjectDisposedException("System.Net.Sockets.NetworkStream");
				}

				Writes.Add(BroadcastTypes(messageBytes));
				WriteArrays.Add(messageBytes.Array);
				if (++Concurrent > MaxConcurrent)
				{
					MaxConcurrent = Concurrent;
				}

				var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				if (Stalled)
				{
					Pending.Enqueue(tcs);
				}
				else
				{
					Concurrent--;
					tcs.SetResult(true);
				}
				return tcs.Task;
			}
		}

		public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			lock (Gate)
			{
				Assert.IsTrue(Udp, "unguaranteed send to a client without UDP");
				List<int> types = BroadcastTypes(messageBytes);
				Assert.AreEqual(1, types.Count, "a datagram carries exactly one message");
				Unguaranteed.Add(types[0]);
			}
			return Task.CompletedTask;
		}

		/// <summary>Completes the oldest pending write, as if the peer had read enough to make room for it.</summary>
		public void ReleaseOne()
		{
			TaskCompletionSource<bool> tcs;
			lock (Gate)
			{
				tcs = Pending.Dequeue();
				Concurrent--;
			}
			tcs.SetResult(true);
		}

		/// <summary>The peer starts reading again: the pending write and everything after it go straight through.</summary>
		public void Unstall()
		{
			List<TaskCompletionSource<bool>> release;
			lock (Gate)
			{
				Stalled = false;
				release = Pending.ToList();
				Pending.Clear();
				Concurrent -= release.Count;
			}
			release.ForEach(t => t.SetResult(true));
		}

		/// <summary>Closing the transport faults pending writes, then reports the disconnect (once).</summary>
		public void Disconnect()
		{
			List<TaskCompletionSource<bool>> fail;
			lock (Gate)
			{
				Disconnects++;
				if (Closed)
				{
					return;
				}
				Closed = true;
				fail = Pending.ToList();
				Pending.Clear();
				Concurrent -= fail.Count;
			}
			fail.ForEach(t => t.SetException(new ObjectDisposedException("System.Net.Sockets.NetworkStream")));
			OnClientDisconnected?.Invoke(this);
		}

		public void Dispose() => Disconnect();

		// Splits a write into its framed messages, as the client's reader does.
		static List<int> BroadcastTypes(ArraySegment<byte> bytes)
		{
			var types = new List<int>();
			while (bytes.Count > 0)
			{
				int length = ImpunityNetworkingUtil.GetMessageLength(bytes);
				Assert.LessOrEqual(length, bytes.Count, "write ends partway through a message");
				ArraySegment<byte> message = bytes.Slice(0, length);
				int bodyOffset = message.Offset + ImpunityNetworkingUtil.ReadMessageHeader(message, out MessageHeaderStruct header);
				if (header.MessageType == (ushort)ServerActionType.BROADCAST_MESSAGE)
				{
					var action = (BroadcastMessageAction)ImpunityUtil.GetBsonMapper().DeserializeFromBytes(typeof(BroadcastMessageAction), message.Array, bodyOffset);
					types.Add(action.MessageType);
				}
				else
				{
					types.Add(-1);
				}
				bytes = bytes.Slice(length);
			}
			return types;
		}
	}

	[Category("SendBacklog")]
	public class ServerSendBacklogTests : ImpunityTestHarness
	{
		static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);

		CapturingLogger Log;
		IImpunityLogger PreviousLogger;
		readonly List<IDisposable> Cleanup = new List<IDisposable>();

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(1, new GameStateCollection[0], new Type[0]);
		}

		protected override void ConfigureOptions(ImpunityOptions options)
		{
			options.ConnectionTimeoutMillis = 0;
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
			foreach (IDisposable d in Cleanup)
			{
				try { d.Dispose(); } catch { }
			}
			Cleanup.Clear();
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
				Thread.Sleep(5);
			}
			return true;
		}

		StallableClientContext AddClient(string id)
		{
			var context = new StallableClientContext(id);
			TcpServer.ClientConnected(context);
			// Closed before the harness disposes the server, failing any write still pending: a writer thread blocked
			// behind it (the bug this fixture guards against) would otherwise hang teardown instead of failing the test.
			Cleanup.Add(context);
			return context;
		}

		/// <summary>Queues a push on the server's network writer thread, the way the live thread does.</summary>
		static void Push(StallableClientContext client, int tag, int bodyBytes = 0)
		{
			client.Proxy.SendMessageToClient(new BroadcastMessageAction
			{
				MessageType = tag,
				MessageBody = new BsonDocument { ["pad"] = new string('x', bodyBytes) },
				SentBy = "test"
			});
		}

		void StartServer()
		{
			CreateServer();
			StartTcpServer();
		}

		/// <summary>The regression: one client's write never completes. Its later pushes used to block the shared writer
		/// thread on the per-connection send lock, so nobody else got anything until the keepalive sweep dropped it (and
		/// with keepalives off, never).</summary>
		[Test]
		public void StuckClient_DoesNotDelayPushesToOtherClients()
		{
			StartServer();
			var stuck = AddClient("stuck");
			var healthy = AddClient("healthy");
			stuck.Stalled = true;

			// The first write stays pending; the pushes after it are where the writer thread used to block.
			Push(stuck, 1);
			Assert.IsTrue(WaitFor(() => stuck.StartedMessages.Count == 1, Prompt), "first write never started");
			Push(stuck, 2);
			Push(stuck, 3);
			for (int i = 0; i < 50; i++)
			{
				Push(healthy, 100 + i);
			}

			Assert.IsTrue(WaitFor(() => healthy.StartedMessages.Count == 50, Prompt),
				"Healthy client got " + healthy.StartedMessages.Count + "/50 pushes while another client was stuck. Log:\n" + Log.Dump());
			CollectionAssert.AreEqual(Enumerable.Range(100, 50).ToList(), healthy.StartedMessages);

			CollectionAssert.AreEqual(new[] { 1 }, stuck.StartedMessages, "only one write may be in flight per connection");
			Assert.Greater(stuck.Proxy.SendBacklogBytes, 0);
			Assert.AreEqual(0, healthy.Proxy.SendBacklogBytes);

			// The stuck client starts reading again: everything it missed arrives, in order, batched into one write.
			stuck.Unstall();
			Assert.IsTrue(WaitFor(() => stuck.StartedMessages.Count == 3, Prompt), "backlog never drained");
			Assert.AreEqual(new List<List<int>> { new List<int> { 1 }, new List<int> { 2, 3 } }, stuck.StartedWrites);
			Assert.IsTrue(WaitFor(() => stuck.Proxy.SendBacklogBytes == 0, Prompt));
			Assert.AreEqual(1, stuck.MaxConcurrentWrites);
			Assert.AreEqual(0, stuck.DisconnectCount);
			CollectionAssert.IsEmpty(Log.At("error"));
		}

		/// <summary>A server-requested close (CloseConnectionOnComplete, an establish failure) writes everything queued
		/// before it, then closes; anything queued after it is dropped. It doesn't block the writer thread while it waits
		/// for the flush.</summary>
		[Test]
		public void CloseRequest_FlushesQueuedMessagesInOrderFirst()
		{
			StartServer();
			var closing = AddClient("closing");
			var other = AddClient("other");
			closing.Stalled = true;

			Push(closing, 1);
			Assert.IsTrue(WaitFor(() => closing.StartedMessages.Count == 1, Prompt), "first write never started");
			for (int i = 2; i <= 5; i++)
			{
				Push(closing, i);
			}
			closing.Proxy.CloseConnectionRequest();
			Push(closing, 6);
			Push(other, 99);

			Assert.IsTrue(WaitFor(() => other.StartedMessages.Count == 1, Prompt), "the close request blocked the writer thread");
			Assert.IsTrue(WaitFor(() => Log.At("debug").Any(m => m.Contains("Dropping message")), Prompt), "push after the close request should be dropped");

			// The stuck write completes; the rest goes out as one more write, and only once that one completes too may the
			// connection close.
			closing.ReleaseOne();
			Assert.IsTrue(WaitFor(() => closing.StartedMessages.Count == 5, Prompt), "queued messages never written");
			Assert.AreEqual(0, closing.DisconnectCount, "closed before the queue was flushed");
			closing.ReleaseOne();

			Assert.IsTrue(WaitFor(() => closing.DisconnectCount == 1, Prompt), "never closed after flushing");
			Assert.AreEqual(new List<List<int>> { new List<int> { 1 }, new List<int> { 2, 3, 4, 5 } }, closing.StartedWrites);
			Assert.AreEqual(1, closing.MaxConcurrentWrites);
			Assert.AreEqual(1, TcpServer.NumConnections);
			CollectionAssert.IsEmpty(Log.At("error"));
		}

		/// <summary>A client that stays stuck can't hold unbounded memory: once its queue would pass
		/// MaxSendBacklogBytes it is dropped, which also fails the pending write and frees the queue.</summary>
		[Test]
		public void BacklogOverLimit_DropsOnlyThatClient()
		{
			Options.MaxSendBacklogBytes = 8 * 1024;
			StartServer();
			var stuck = AddClient("stuck");
			var healthy = AddClient("healthy");
			stuck.Stalled = true;

			for (int i = 0; i < 20; i++)
			{
				Push(stuck, i, bodyBytes: 1000);
				Push(healthy, i);
			}

			Assert.IsTrue(WaitFor(() => stuck.DisconnectCount == 1, Prompt), "stuck client was never dropped. Log:\n" + Log.Dump());
			Assert.IsTrue(WaitFor(() => healthy.StartedMessages.Count == 20, Prompt), "healthy client missed pushes");
			Assert.AreEqual(0, healthy.DisconnectCount);

			Assert.AreEqual(1, Log.At("warning").Count(m => m.Contains("Dropping connection stuck") && m.Contains("backed up")), "Log:\n" + Log.Dump());
			Assert.IsTrue(WaitFor(() => stuck.Proxy.SendBacklogBytes == 0, Prompt), "dropped client's queue was not freed");
			Assert.AreEqual(1, TcpServer.NumConnections);
			CollectionAssert.IsEmpty(Log.At("error"));
		}

		/// <summary>One message bigger than the limit still goes out to a client that is keeping up.</summary>
		[Test]
		public void MessageLargerThanLimit_IsAcceptedIntoAnEmptyQueue()
		{
			Options.MaxSendBacklogBytes = 100;
			StartServer();
			var client = AddClient("client");

			Push(client, 1, bodyBytes: 1000);
			Assert.IsTrue(WaitFor(() => client.StartedMessages.Count == 1, Prompt));
			Assert.IsTrue(WaitFor(() => client.Proxy.SendBacklogBytes == 0, Prompt));
			Push(client, 2, bodyBytes: 1000);

			Assert.IsTrue(WaitFor(() => client.StartedMessages.Count == 2, Prompt));
			Assert.AreEqual(0, client.DisconnectCount);
		}

		// ───────── Real sockets ─────────

		const int BroadcastCount = 300;
		const int BroadcastBodyBytes = 32 * 1024;

		/// <summary>Opens a raw TCP connection, completes the handshake, and then never reads again: from the server's
		/// side, a client whose socket buffer fills up and stays full.</summary>
		TcpClient ConnectNonReadingClient()
		{
			var raw = new TcpClient(AddressFamily.InterNetwork) { ReceiveBufferSize = 4096 };
			Cleanup.Add(raw);
			raw.Connect(IPAddress.Loopback, TcpServer.TCPEndpoint.Port);

			var establish = new EstablishConnectionAction("test", null, new GameStateFormatData(Format, EntityDefs), "non-reading");
			var writer = new ByteWriter(new byte[ImpunityConstants.MaxMessageSize]);
			ArraySegment<byte> message = ImpunityNetworkingUtil.WriteMessage(writer, 1, 0, establish.GetActionType(), establish);
			raw.GetStream().Write(message.Array, message.Offset, message.Count);

			// The handshake reply lands in the socket buffer, unread.
			Assert.IsTrue(WaitFor(() => raw.Available > 0, Prompt), "no handshake reply");
			return raw;
		}

		async Task<(RemoteGameConnection conn, Func<int> received)> ConnectBroadcaster()
		{
			var conn = Track(RemoteGameConnection.MakeTCPRemoteConnection(TcpServer.TCPEndpoint, "test", null, Format, Options));
			conn.ConnectionKey = "broadcaster";
			int received = 0;
			conn.OnBroadcastMessage = (type, body, sender) => received++;
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected);
			return (conn, () => received);
		}

		/// <summary>End to end over loopback: a client that never reads gets several megabytes of broadcasts it can't
		/// take, far more than the socket buffers hold, while another client keeps receiving every one of its own
		/// broadcasts promptly.</summary>
		[Test]
		public async Task NonReadingTcpClient_DoesNotDelayOtherClients()
		{
			StartServer();
			ConnectNonReadingClient();
			var (conn, received) = await ConnectBroadcaster();

			BsonDocument body = new BsonDocument { ["pad"] = new string('x', BroadcastBodyBytes) };
			for (int i = 0; i < BroadcastCount; i++)
			{
				conn.SendBroadcastMessage(1, body);
			}

			await PumpUntil(() => received() == BroadcastCount, TimeSpan.FromSeconds(15), conn);
			Assert.AreEqual(2, TcpServer.NumConnections, "the non-reading client is under the backlog limit and stays connected");
			CollectionAssert.IsEmpty(Log.At("error"));
		}

		/// <summary>The same non-reading client with a backlog limit well under what it is sent: it is dropped (and
		/// cleaned up like any disconnect) while the other client still gets everything.</summary>
		[Test]
		public async Task NonReadingTcpClient_OverBacklogLimit_IsDropped()
		{
			Options.MaxSendBacklogBytes = 1024 * 1024;
			StartServer();
			ConnectNonReadingClient();
			var (conn, received) = await ConnectBroadcaster();

			BsonDocument body = new BsonDocument { ["pad"] = new string('x', BroadcastBodyBytes) };
			for (int i = 0; i < BroadcastCount; i++)
			{
				conn.SendBroadcastMessage(1, body);
			}

			await PumpUntil(() => received() == BroadcastCount, TimeSpan.FromSeconds(15), conn);
			await PumpUntil(() => TcpServer.NumConnections == 1, Prompt, conn);
			Assert.IsTrue(conn.Connected);
			Assert.AreEqual(1, Log.At("warning").Count(m => m.Contains("backed up")), "Log:\n" + Log.Dump());
			CollectionAssert.IsEmpty(Log.At("error"));
		}
	}
}
