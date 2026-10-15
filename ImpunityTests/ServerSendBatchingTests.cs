// How the server turns queued messages into socket writes: a push is encoded once and shared by every recipient,
// messages queued together go out as one batched write (capped at ServerSideNetworkConnectionProxy.MaxBatchBytes),
// and unguaranteed messages for a client with UDP skip the reliable queue entirely.
// dotnet-only: drives ServerSideNetworkConnectionProxy through internal seams (InternalsVisibleTo in
// ImpunityRuntime.csproj). Uses StallableClientContext from ServerSendBacklogTests.cs.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using NUnit.Framework;

using Impunity.GameState;
using Impunity.Networking;

using UltraLiteDB;

namespace Impunity.Tests
{
	[Category("SendBatching")]
	public class ServerSendBatchingTests : ImpunityTestHarness
	{
		static readonly TimeSpan Prompt = TimeSpan.FromSeconds(2);

		readonly List<StallableClientContext> Clients = new List<StallableClientContext>();

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(1, new GameStateCollection[0], new Type[0]);
		}

		protected override void ConfigureOptions(ImpunityOptions options)
		{
			options.ConnectionTimeoutMillis = 0;
		}

		[TearDown]
		public void CloseClients()
		{
			// Before the harness disposes the server, so no pending write can hold up its writer thread.
			foreach (var client in Clients)
			{
				try { client.Dispose(); } catch { }
			}
			Clients.Clear();
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
			Clients.Add(context);
			return context;
		}

		static BroadcastMessageAction Broadcast(int tag, int bodyBytes = 0, bool guaranteed = true)
		{
			var action = new BroadcastMessageAction
			{
				MessageType = tag,
				MessageBody = new BsonDocument { ["pad"] = new string('x', bodyBytes) },
				SentBy = "test"
			};
			action.SetGuaranteed(guaranteed);
			return action;
		}

		/// <summary>A server whose writer thread isn't running: the test calls SendMessage and StartScheduledWrites in its
		/// place, which makes the batching deterministic.</summary>
		void CreateUnstartedServer()
		{
			TcpServer = new ImpunityServer(new List<GameStateServer>(), Options);
		}

		static void SendDirect(StallableClientContext client, int tag, int bodyBytes = 0)
		{
			client.Proxy.SendMessage((ushort)ServerActionType.BROADCAST_MESSAGE, 0, true, Broadcast(tag, bodyBytes));
		}

		/// <summary>Everything queued for a client while the writer was busy goes out as a single write, in order.</summary>
		[Test]
		public void MessagesQueuedTogether_GoOutAsOneWrite()
		{
			CreateUnstartedServer();
			var client = AddClient("client");

			for (int i = 0; i < 10; i++)
			{
				SendDirect(client, i);
			}
			CollectionAssert.IsEmpty(client.StartedWrites, "nothing is written until the writer runs out of work");

			TcpServer.StartScheduledWrites();

			Assert.AreEqual(new List<List<int>> { Enumerable.Range(0, 10).ToList() }, client.StartedWrites);
			Assert.AreEqual(0, client.Proxy.SendBacklogBytes);
		}

		/// <summary>A batch never grows past MaxBatchBytes; the rest follows in further writes, still in order.</summary>
		[Test]
		public void Batches_AreCappedAtMaxBatchBytes()
		{
			CreateUnstartedServer();
			var client = AddClient("client");

			// Three of these fit under the cap, four don't.
			int body = ServerSideNetworkConnectionProxy.MaxBatchBytes / 3 - 400;
			for (int i = 0; i < 10; i++)
			{
				SendDirect(client, i, body);
			}
			TcpServer.StartScheduledWrites();

			Assert.AreEqual(new List<List<int>>
			{
				new List<int> { 0, 1, 2 },
				new List<int> { 3, 4, 5 },
				new List<int> { 6, 7, 8 },
				new List<int> { 9 },
			}, client.StartedWrites);
			Assert.AreEqual(0, client.Proxy.SendBacklogBytes);
		}

		/// <summary>A push fanned out to several listeners is encoded once: every recipient is handed the same bytes.</summary>
		[Test]
		public void Push_IsEncodedOnceForAllRecipients()
		{
			CreateServer();
			StartTcpServer();
			var clients = new[] { AddClient("a"), AddClient("b"), AddClient("c") };

			BroadcastMessageAction push = Broadcast(7);
			foreach (var client in clients)
			{
				client.Proxy.SendMessageToClient(push);
			}

			foreach (var client in clients)
			{
				Assert.IsTrue(WaitFor(() => client.StartedMessages.Count == 1, Prompt), client.ConnectionId + " never got the push");
				CollectionAssert.AreEqual(new[] { 7 }, client.StartedMessages);
			}

			// A lone message is written straight from its encoded array, so one shared array means one encoding.
			Assert.IsNotNull(push.EncodedMessage);
			foreach (var client in clients)
			{
				Assert.AreSame(push.EncodedMessage, client.StartedWriteArrays.Single());
			}
		}

		/// <summary>An unguaranteed message for a client with UDP goes out right away, even while the reliable queue is
		/// stuck behind a write the client isn't reading.</summary>
		[Test]
		public void Unguaranteed_WithUdp_SkipsAStuckReliableQueue()
		{
			CreateServer();
			StartTcpServer();
			var client = AddClient("client");
			client.Udp = true;
			client.Stalled = true;

			client.Proxy.SendMessageToClient(Broadcast(1));
			Assert.IsTrue(WaitFor(() => client.StartedMessages.Count == 1, Prompt));
			client.Proxy.SendMessageToClient(Broadcast(2));
			client.Proxy.SendMessageToClient(Broadcast(3, guaranteed: false));
			client.Proxy.SendMessageToClient(Broadcast(4, guaranteed: false));

			Assert.IsTrue(WaitFor(() => client.UnguaranteedMessages.Count == 2, Prompt), "unguaranteed sends waited behind the stuck write");
			CollectionAssert.AreEqual(new[] { 3, 4 }, client.UnguaranteedMessages);
			CollectionAssert.AreEqual(new[] { 1 }, client.StartedMessages);

			client.Unstall();
			Assert.IsTrue(WaitFor(() => client.StartedMessages.Count == 2, Prompt));
			CollectionAssert.AreEqual(new[] { 1, 2 }, client.StartedMessages);
		}

		/// <summary>Without UDP, an unguaranteed message is sent reliably, in order with everything else.</summary>
		[Test]
		public void Unguaranteed_WithoutUdp_IsQueuedInOrder()
		{
			CreateUnstartedServer();
			var client = AddClient("client");

			client.Proxy.SendMessage((ushort)ServerActionType.BROADCAST_MESSAGE, 0, true, Broadcast(1));
			client.Proxy.SendMessage((ushort)ServerActionType.BROADCAST_MESSAGE, 0, false, Broadcast(2));
			client.Proxy.SendMessage((ushort)ServerActionType.BROADCAST_MESSAGE, 0, true, Broadcast(3));
			TcpServer.StartScheduledWrites();

			Assert.AreEqual(new List<List<int>> { new List<int> { 1, 2, 3 } }, client.StartedWrites);
			CollectionAssert.IsEmpty(client.UnguaranteedMessages);
		}
	}
}
