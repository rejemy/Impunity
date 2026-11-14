// A client transport that does what a browser will: the Impunity connection over a WebSocket, plus a WebRTC data
// channel (unordered, no retransmits) negotiated over that socket's text frames and used for unguaranteed messages.
// The peer is libdatachannel through the same bindings the server uses, so the server's WebRTC path can be tested
// headlessly. It is the reference for the Unity WebGL bridge: offer as soon as the socket is open, send unguaranteed
// messages on the channel only while it is open, keep the channel alive with KEEPALIVE frames, and give up on it
// after five seconds of silence.
// dotnet-only (P/Invoke); never compiled by Unity.
#nullable disable

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Impunity.GameState;
using Impunity.Networking;
using Impunity.StandaloneServer;

namespace Impunity.Tests
{
	public class RtcWebSocketTestClient : IImpunityNetworkClient
	{
		const long KeepAliveAfterMillis = 1000;
		const long SilenceLimitMillis = 5000;

		// The client's header-only KEEPALIVE frame: length 12, type ClientActionType.KEEPALIVE (104).
		static readonly byte[] KeepAliveFrame = { 12, 0, 0, 0, 104, 0, 0, 0, 0, 0, 0, 0 };

		// Native callbacks carry only ids; these find the client. The delegates are static so they outlive every peer.
		static readonly ConcurrentDictionary<int, RtcWebSocketTestClient> ByPeer = new ConcurrentDictionary<int, RtcWebSocketTestClient>();
		static readonly ConcurrentDictionary<int, RtcWebSocketTestClient> ByChannel = new ConcurrentDictionary<int, RtcWebSocketTestClient>();
		static readonly LibDataChannelNative.StateCallback GatheringHandler = OnGatheringState;
		static readonly LibDataChannelNative.ChannelCallback OpenHandler = OnOpen;
		static readonly LibDataChannelNative.ChannelCallback ClosedHandler = OnClosed;
		static readonly LibDataChannelNative.MessageCallback MessageHandler = OnMessage;

		public string ConnectionId { get; private set; }
		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		public ImpunityClientMessageHandler OnUnguaranteedMessageRecieved { get; set; }
		public ImpunityCallback OnNetworkError { get; set; }
		public Action<int> OnDisconnectedByServer { get; set; }

		public bool SupportsUnguaranteed => ChannelOpen;
		public int MaxUnguaranteedMessageBytes => ImpunityConstants.MaxUnguaranteedMessageSize;

		// ----- What the tests look at

		/// <summary>Why the server refused WebRTC, or null if it hasn't.</summary>
		public volatile string UnavailableReason;
		/// <summary>Why the channel was closed from this end, or null.</summary>
		public volatile string ChannelClosedReason;
		/// <summary>Impunity messages (not keepalives) sent and received on the data channel.</summary>
		public int ChannelSent, ChannelReceived;
		/// <summary>Server keepalives received on the data channel.</summary>
		public int ChannelKeepAlivesReceived;
		/// <summary>Entity updates pushed over the WebSocket.</summary>
		public int SocketEntityUpdatesReceived;
		/// <summary>Messages sent over the WebSocket that carry the UNGUARANTEED header flag.</summary>
		public int SocketUnguaranteedSent;

		/// <summary>Stands in for a dead UDP path: nothing more is sent on the channel (keepalives included) and what
		/// arrives on it is ignored, while both ends still believe it is open.</summary>
		public volatile bool Blackhole;

		readonly string Url;
		readonly string RuntimesDirectory;
		readonly CancellationTokenSource CancelSource = new CancellationTokenSource();
		readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);

		ClientWebSocket Socket;
		ImpunityCallback OnConnectCallback;
		Timer Ticker;

		int Peer = -1;
		int Channel = -1;
		int OfferSent;
		volatile bool ChannelOpen;
		long LastChannelSent, LastChannelReceived;

		public RtcWebSocketTestClient(string host, int port, string runtimesDirectory)
		{
			Url = $"ws://{host}:{port}/ws";
			RuntimesDirectory = runtimesDirectory;
		}

		public void Connect(ImpunityCallback onComplete)
		{
			OnConnectCallback = onComplete;
			var thread = new Thread(() =>
			{
				try { ReadLoop().Wait(); }
				catch (Exception e) { ImpunityLogger.LogError("Error in RTC test client thread: ", e); }
			});
			thread.IsBackground = true;
			thread.Name = "RtcWebSocketTestClient reader";
			thread.Start();
		}

		async Task ReadLoop()
		{
			byte[] buffer = new byte[ImpunityConstants.MaxMessageSize];
			bool connected = false;
			try
			{
				Socket = new ClientWebSocket();
				await Socket.ConnectAsync(new Uri(Url), CancelSource.Token);
				connected = true;

				// Like the browser: start WebRTC as soon as the socket is up, without holding up the connection.
				StartPeer();

				OnConnectCallback?.Invoke(null);
				OnConnectCallback = null;

				int received = 0;
				while (Socket.State == WebSocketState.Open)
				{
					var result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer, received, buffer.Length - received), CancelSource.Token);
					if (result.CloseStatus.HasValue)
					{
						break;
					}

					received += result.Count;
					if (!result.EndOfMessage)
					{
						continue;
					}

					try
					{
						if (result.MessageType == WebSocketMessageType.Text)
						{
							HandleSignal(Encoding.UTF8.GetString(buffer, 0, received));
						}
						else
						{
							if (received >= 12 && BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(buffer, 4, 2)) == (ushort)ServerActionType.ENTITY_UPDATE_MESSAGE)
							{
								Interlocked.Increment(ref SocketEntityUpdatesReceived);
							}
							OnMessageRecieved?.Invoke(new ArraySegment<byte>(buffer, 0, received));
						}
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Error handling a message in the RTC test client", e);
					}
					received = 0;
				}
			}
			catch (Exception e)
			{
				Exception inner = e is AggregateException ae ? ae.InnerException : e;
				if (!connected)
				{
					OnConnectCallback?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError, inner));
					OnConnectCallback = null;
				}
				else if (Socket != null)
				{
					OnNetworkError?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, inner));
				}
			}
			finally
			{
				ClosePeer("socket closed");
				if (Socket != null)
				{
					try { Socket.Dispose(); } catch (Exception) { }
					Socket = null;
					try { OnDisconnectedByServer?.Invoke(0); } catch (Exception) { }
				}
			}
		}

		// ----- WebRTC

		void StartPeer()
		{
			if (!LibDataChannelNative.TryLoad(RuntimesDirectory, out string problem))
			{
				UnavailableReason = "client: " + problem;
				return;
			}

			LibDataChannelNative.Configuration config = default;
			int pc = LibDataChannelNative.rtcCreatePeerConnection(ref config);
			if (pc < 0)
			{
				UnavailableReason = "client: could not create a peer (" + pc + ")";
				return;
			}
			Peer = pc;
			ByPeer[pc] = this;
			LibDataChannelNative.rtcSetGatheringStateChangeCallback(pc, GatheringHandler);

			// What the browser bridge will ask for: { ordered: false, maxRetransmits: 0 }.
			LibDataChannelNative.DataChannelInit init = default;
			init.Reliability.Unordered = 1;
			init.Reliability.Unreliable = 1;
			init.Reliability.MaxRetransmits = 0;
			int dc = LibDataChannelNative.rtcCreateDataChannelEx(pc, "impunity", ref init);
			if (dc < 0)
			{
				UnavailableReason = "client: could not create a data channel (" + dc + ")";
				return;
			}
			Channel = dc;
			ByChannel[dc] = this;
			LibDataChannelNative.rtcSetOpenCallback(dc, OpenHandler);
			LibDataChannelNative.rtcSetClosedCallback(dc, ClosedHandler);
			LibDataChannelNative.rtcSetMessageCallback(dc, MessageHandler);

			Ticker = new Timer(Tick, null, 250, 250);
		}

		// Creating the channel made libdatachannel write an offer and gather candidates; when that is done, send it.
		static void OnGatheringState(int pc, int state, IntPtr userPointer)
		{
			if (state != LibDataChannelNative.GatheringComplete || !ByPeer.TryGetValue(pc, out var client) || Interlocked.Exchange(ref client.OfferSent, 1) != 0)
			{
				return;
			}

			// Off the native thread: sending waits on the socket.
			Task.Run(() =>
			{
				try
				{
					byte[] buffer = new byte[16 * 1024];
					int length = LibDataChannelNative.rtcGetLocalDescription(pc, buffer, buffer.Length);
					if (length <= 1)
					{
						client.UnavailableReason = "client: no offer (" + length + ")";
						return;
					}
					client.SendText(JsonSerializer.Serialize(new Dictionary<string, string> { ["t"] = "offer", ["sdp"] = Encoding.UTF8.GetString(buffer, 0, length - 1) }));
				}
				catch (Exception e)
				{
					client.UnavailableReason = "client: " + e.Message;
				}
			});
		}

		void HandleSignal(string text)
		{
			using JsonDocument message = JsonDocument.Parse(text);
			string type = message.RootElement.GetProperty("t").GetString();
			if (type == "answer")
			{
				int result = LibDataChannelNative.rtcSetRemoteDescription(Peer, message.RootElement.GetProperty("sdp").GetString(), "answer");
				if (result < 0)
				{
					UnavailableReason = "client: answer rejected (" + result + ")";
				}
			}
			else if (type == "unavailable")
			{
				UnavailableReason = message.RootElement.GetProperty("r").GetString();
				ClosePeer("server refused");
			}
		}

		static void OnOpen(int dc, IntPtr userPointer)
		{
			if (ByChannel.TryGetValue(dc, out var client))
			{
				long now = Environment.TickCount64;
				Volatile.Write(ref client.LastChannelSent, now);
				Volatile.Write(ref client.LastChannelReceived, now);
				client.ChannelOpen = true;
			}
		}

		static void OnClosed(int dc, IntPtr userPointer)
		{
			if (ByChannel.TryGetValue(dc, out var client))
			{
				client.ClosePeer("channel closed");
			}
		}

		static void OnMessage(int dc, IntPtr message, int size, IntPtr userPointer)
		{
			try
			{
				if (size < 12 || !ByChannel.TryGetValue(dc, out var client) || client.Blackhole)
				{
					return;
				}

				byte[] copy = new byte[size];
				Marshal.Copy(message, copy, 0, size);
				Volatile.Write(ref client.LastChannelReceived, Environment.TickCount64);

				if (BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(copy, 4, 2)) == (ushort)ServerActionType.KEEPALIVE)
				{
					Interlocked.Increment(ref client.ChannelKeepAlivesReceived);
					return;
				}

				Interlocked.Increment(ref client.ChannelReceived);
				client.OnUnguaranteedMessageRecieved?.Invoke(new ArraySegment<byte>(copy));
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error in the RTC test client's message callback", e);
			}
		}

		void Tick(object state)
		{
			if (!ChannelOpen || Blackhole)
			{
				// A blackholed client still watches for silence: that is the fallback being tested.
				if (ChannelOpen && Environment.TickCount64 - Volatile.Read(ref LastChannelReceived) > SilenceLimitMillis)
				{
					ClosePeer("silent");
				}
				return;
			}

			long now = Environment.TickCount64;
			if (now - Volatile.Read(ref LastChannelReceived) > SilenceLimitMillis)
			{
				ClosePeer("silent");
			}
			else if (now - Volatile.Read(ref LastChannelSent) > KeepAliveAfterMillis)
			{
				SendOnChannel(new ArraySegment<byte>(KeepAliveFrame));
			}
		}

		bool SendOnChannel(ArraySegment<byte> message)
		{
			int dc = Channel;
			if (!ChannelOpen || dc < 0)
			{
				return false;
			}
			if (LibDataChannelNative.rtcSendMessage(dc, ref message.Array[message.Offset], message.Count) < 0)
			{
				return false;
			}
			Volatile.Write(ref LastChannelSent, Environment.TickCount64);
			return true;
		}

		/// <summary>Drops the data channel from this end. Unguaranteed messages go over the WebSocket afterwards.</summary>
		public void ClosePeer(string reason)
		{
			int pc = Interlocked.Exchange(ref Peer, -1);
			if (pc < 0)
			{
				return;
			}

			ChannelOpen = false;
			ChannelClosedReason = reason;
			Ticker?.Dispose();
			int dc = Interlocked.Exchange(ref Channel, -1);
			ByPeer.TryRemove(pc, out _);
			if (dc >= 0)
			{
				ByChannel.TryRemove(dc, out _);
			}

			// Never from inside one of the peer's own callbacks.
			Task.Run(() =>
			{
				try
				{
					if (dc >= 0)
					{
						LibDataChannelNative.rtcDeleteDataChannel(dc);
					}
					LibDataChannelNative.rtcDeletePeerConnection(pc);
				}
				catch (Exception) { }
			});
		}

		// ----- Sending

		void SendOnSocket(ArraySegment<byte> bytes, WebSocketMessageType type)
		{
			ClientWebSocket socket = Socket;
			if (socket == null || socket.State != WebSocketState.Open)
			{
				return;
			}

			// One send at a time on a WebSocket; signaling comes from another thread than Impunity's writer.
			SendLock.Wait();
			try
			{
				socket.SendAsync(bytes, type, true, CancellationToken.None).Wait();
			}
			finally
			{
				SendLock.Release();
			}
		}

		void SendText(string text)
		{
			SendOnSocket(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text);
		}

		public void SendGuaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (messageBytes.Count >= 12
				&& (BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(messageBytes.Array, messageBytes.Offset + 8, 2)) & ImpunityMessageFlags.UNGUARANTEED) != 0)
			{
				Interlocked.Increment(ref SocketUnguaranteedSent);
			}
			SendOnSocket(messageBytes, WebSocketMessageType.Binary);
		}

		public void SendUnguaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (!ChannelOpen)
			{
				SendGuaranteedMessage(messageBytes);
				return;
			}

			if (Blackhole)
			{
				// Lost on the dead path, as a datagram would be.
				return;
			}

			if (SendOnChannel(messageBytes))
			{
				Interlocked.Increment(ref ChannelSent);
			}
		}

		public void Flush()
		{
		}

		public void Disconnect()
		{
			ClosePeer("disconnect");
			ClientWebSocket socket = Socket;
			if (socket != null)
			{
				Socket = null;
				try
				{
					if (socket.State == WebSocketState.Open)
					{
						socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None).Wait(1000);
					}
				}
				catch (Exception) { }
				try { socket.Dispose(); } catch (Exception) { }
			}
		}

		public void Dispose()
		{
			Disconnect();
		}
	}
}
