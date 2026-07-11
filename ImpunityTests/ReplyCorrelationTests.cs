using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using NUnit.Framework;
using UltraLiteDB;

using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;

namespace Impunity.Tests
{
    /// <summary>
    /// Verifies that <see cref="RemoteGameConnection"/> matches each server reply to its originating action by the
    /// header correlation id, not by arrival order. A raw fake server completes the handshake, then replies to two
    /// in-flight actions in REVERSE order with id-specific payloads; correct id matching means each action's callback
    /// still receives its own payload. Under the old positional (FIFO) scheme the first action would wrongly receive
    /// the second reply.
    /// </summary>
    [TestFixture]
    public class ReplyCorrelationTests
    {
        [Test]
        public void OutOfOrderReplies_MatchByCorrelationId()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverThread = new Thread(() => RunFakeServer(listener)) { IsBackground = true };
            serverThread.Start();

            var format = new GameStateFormat(1, Array.Empty<GameStateCollection>(), Array.Empty<Type>());
            RemoteGameConnection conn = RemoteGameConnection.MakeTCPRemoteConnection(
                new IPEndPoint(IPAddress.Loopback, port), "test", "", format, new ImpunityOptions());

            try
            {
                bool connected = false;
                ImpunityErrorResponse? connectError = null;
                conn.Connect(err => { connectError = err; connected = true; });
                PumpUntil(conn, () => connected, 5000);
                Assert.IsTrue(connected, "connection never completed the handshake");
                Assert.IsNull(connectError, "connect returned an error");

                // Two reply-expecting actions in flight at once. The fake server replies to the SECOND before the
                // FIRST, tagging each reply with a payload derived from that action's own correlation id.
                List<string>? resultA = null;
                List<string>? resultB = null;
                conn.ListActiveChannels((err, r) => resultA = r);
                conn.ListActiveChannels((err, r) => resultB = r);

                PumpUntil(conn, () => resultA != null && resultB != null, 5000);

                Assert.IsNotNull(resultA, "first action never got a reply");
                Assert.IsNotNull(resultB, "second action never got a reply");
                // Correct id matching: the first action's reply ("reply-A") reaches the first callback even though it
                // arrived on the wire second.
                Assert.AreEqual("reply-A", resultA![0], "first action received the wrong reply (positional match?)");
                Assert.AreEqual("reply-B", resultB![0], "second action received the wrong reply");
            }
            finally
            {
                conn.Dispose();
                listener.Stop();
            }
        }

        private static void PumpUntil(BaseGameConnection conn, Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                conn.Update();
                if (condition())
                {
                    return;
                }
                Thread.Sleep(2);
            }
            conn.Update();
        }

        // Minimal server that speaks just enough of the wire protocol: it completes the establish + clock-sync
        // handshake, then answers two LIST_ACTIVE_CHANNELS actions out of order to exercise id-based correlation.
        private static void RunFakeServer(TcpListener listener)
        {
            try
            {
                using TcpClient conn = listener.AcceptTcpClient();
                var stream = conn.GetStream();

                var replyWriter = new ByteWriter(new byte[ImpunityConstants.MaxMessageSize]);
                var pendingListIds = new List<ushort>();

                byte[] buf = new byte[ImpunityConstants.MaxMessageSize];
                int have = 0;

                while (true)
                {
                    int n = stream.Read(buf, have, buf.Length - have);
                    if (n <= 0)
                    {
                        return;
                    }
                    have += n;

                    // Drain every complete framed message currently buffered.
                    while (have >= 12)
                    {
                        int len = BitConverter.ToInt32(buf, 0);
                        if (len < 12 || have < len)
                        {
                            break;
                        }

                        ImpunityNetworkingUtil.ReadMessageHeader(new ArraySegment<byte>(buf, 0, len), out var header);
                        HandleClientMessage(stream, replyWriter, header.MessageType, header.MessageId, pendingListIds);

                        int extra = have - len;
                        if (extra > 0)
                        {
                            Buffer.BlockCopy(buf, len, buf, 0, extra);
                        }
                        have = extra;
                    }
                }
            }
            catch (Exception)
            {
                // Connection torn down at end of test; nothing to do.
            }
        }

        private static void HandleClientMessage(NetworkStream stream, ByteWriter writer, ushort type, ushort id, List<ushort> pendingListIds)
        {
            switch ((ClientActionType)type)
            {
                case ClientActionType.ESTABLISH_CONNECTION:
                    SendReply(stream, writer, id, new ActionResult<EstablishConnectResult>
                    {
                        Result = new EstablishConnectResult { ConnectionId = "fake", MigrationRequired = false }
                    });
                    break;

                case ClientActionType.GET_TIME:
                    SendReply(stream, writer, id, new ActionResult<long> { Result = 0 });
                    break;

                case ClientActionType.LIST_ACTIVE_CHANNELS:
                    pendingListIds.Add(id);
                    if (pendingListIds.Count == 2)
                    {
                        // Reply to the SECOND action first, then the FIRST — each payload keyed to its own action.
                        SendReply(stream, writer, pendingListIds[1], new ActionResult<List<string>> { Result = new List<string> { "reply-B" } });
                        SendReply(stream, writer, pendingListIds[0], new ActionResult<List<string>> { Result = new List<string> { "reply-A" } });
                    }
                    break;
            }
        }

        private static void SendReply(NetworkStream stream, ByteWriter writer, ushort correlationId, object result)
        {
            ArraySegment<byte> encoded = ImpunityNetworkingUtil.WriteMessage(
                writer, correlationId, 0, (ushort)ServerActionType.CLIENT_REPLY, result);
            stream.Write(encoded.Array!, encoded.Offset, encoded.Count);
            stream.Flush();
        }
    }
}
