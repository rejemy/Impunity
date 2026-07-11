using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using NUnit.Framework;

using Impunity.Networking;

namespace Impunity.Tests
{
    /// <summary>
    /// Regression tests for the TCP reader's message framing. A single socket read can return
    /// several complete messages coalesced into one segment (Nagle / TCP batching). The reader
    /// must surface every complete message in the buffer, not just the first — otherwise the
    /// remainder is stranded until more bytes happen to arrive.
    ///
    /// This reproduces the intermittent "Action SubscribeChannelAction took too long to complete"
    /// flake: subscribing to an already-live channel makes the server emit a channel-create push
    /// immediately followed by the subscribe reply, which Nagle coalesces into one segment. Before
    /// the fix the client dispatched only the push and stranded the reply, timing out after ~5s.
    ///
    /// The client and server readers share the same drain-loop fix; this exercises the client one
    /// (<see cref="ImpunityTCPClient"/>) directly against a raw TCP server socket.
    /// </summary>
    [TestFixture]
    public class TcpFramingTests
    {
        // Builds a wire-framed message: [4 len LE][2 type][2 id][2 flags][2 pad][body...].
        // Only the length prefix drives framing; the body is filled with the type byte so each
        // delivered message can be identified.
        private static byte[] MakeFramedMessage(ushort type, int totalLength)
        {
            if (totalLength < 12)
            {
                throw new ArgumentException("Framed message must be at least the 12-byte header");
            }

            byte[] m = new byte[totalLength];
            m[0] = (byte)(totalLength & 0xff);
            m[1] = (byte)((totalLength >> 8) & 0xff);
            m[2] = (byte)((totalLength >> 16) & 0xff);
            m[3] = (byte)((totalLength >> 24) & 0xff);
            m[4] = (byte)(type & 0xff);
            m[5] = (byte)((type >> 8) & 0xff);
            for (int i = 12; i < totalLength; i++)
            {
                m[i] = (byte)type;
            }
            return m;
        }

        private static ushort TypeOf(byte[] framedMessage)
        {
            return (ushort)(framedMessage[4] | (framedMessage[5] << 8));
        }

        // Sends the given messages as a single write (one coalesced segment) and asserts the
        // client transport delivers all of them, in order.
        private static void AssertAllDelivered(params byte[][] messages)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var received = new List<byte[]>();
            var gotAll = new ManualResetEventSlim(false);

            IImpunityNetworkClient client = ImpunityTCPClient.MakeTCPClient(
                new IPEndPoint(IPAddress.Loopback, port), new ImpunityOptions());

            client.OnMessageRecieved = (seg) =>
            {
                var copy = new byte[seg.Count];
                Buffer.BlockCopy(seg.Array!, seg.Offset, copy, 0, seg.Count);
                lock (received)
                {
                    received.Add(copy);
                    if (received.Count >= messages.Length)
                    {
                        gotAll.Set();
                    }
                }
            };

            TcpClient? serverSide = null;
            try
            {
                var connected = new ManualResetEventSlim(false);
                client.Connect(_ => connected.Set());

                serverSide = listener.AcceptTcpClient();
                Assert.IsTrue(connected.Wait(TimeSpan.FromSeconds(5)), "client did not connect");

                // Concatenate every message into a single write, so they arrive coalesced.
                int total = 0;
                foreach (var m in messages)
                {
                    total += m.Length;
                }
                byte[] batch = new byte[total];
                int offset = 0;
                foreach (var m in messages)
                {
                    Buffer.BlockCopy(m, 0, batch, offset, m.Length);
                    offset += m.Length;
                }

                var ns = serverSide.GetStream();
                ns.Write(batch, 0, batch.Length);
                ns.Flush();

                bool delivered = gotAll.Wait(TimeSpan.FromSeconds(5));
                Assert.IsTrue(delivered,
                    "All coalesced messages should be delivered; a stranded trailing message indicates the framing bug. Delivered "
                    + received.Count + " of " + messages.Length);

                lock (received)
                {
                    Assert.AreEqual(messages.Length, received.Count, "unexpected number of delivered messages");
                    for (int i = 0; i < messages.Length; i++)
                    {
                        Assert.AreEqual(messages[i].Length, received[i].Length, "message " + i + " length mismatch");
                        Assert.AreEqual(TypeOf(messages[i]), TypeOf(received[i]), "message " + i + " identity/order mismatch");
                    }
                }
            }
            finally
            {
                client.Dispose();
                serverSide?.Close();
                listener.Stop();
            }
        }

        [Test]
        public void TwoCoalescedMessages_BothDelivered()
        {
            // 1001 ~ ChannelCreate push, 9999 ~ subscribe reply — the exact pair that flaked.
            AssertAllDelivered(
                MakeFramedMessage(1001, 60),
                MakeFramedMessage(9999, 30));
        }

        [Test]
        public void ThreeCoalescedMessages_AllDelivered()
        {
            AssertAllDelivered(
                MakeFramedMessage(1001, 48),
                MakeFramedMessage(1002, 200),
                MakeFramedMessage(9999, 24));
        }
    }
}
