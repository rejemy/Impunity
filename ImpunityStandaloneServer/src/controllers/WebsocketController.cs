#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Impunity.Networking;
using System.Net;

namespace Impunity.StandaloneServer;

// Binary WebSocket messages are Impunity frames. Text messages are the transport's own business and never reach
// Impunity: they carry the WebRTC signaling ({"t":"offer","sdp":...} answered by {"t":"answer","sdp":...} or
// {"t":"unavailable","r":...}). Once the browser's data channel is open this context reports SupportsUnguaranteed
// and unguaranteed messages go over it; when it closes they go back to the WebSocket. See
// docs/designs/WebRtcTransport.md.
public class WebSocketController(ILogger<WebSocketController> logger, ConnectionService connectionService, WebRtcService webRtc) : ControllerBase, IImpunityNetworkServerClientContext
{
	private readonly CancellationTokenSource ReadCancelSource = new();
	private WebSocket? Socket;
	private readonly ILogger<WebSocketController> Logger = logger;
	private readonly ConnectionService Connections = connectionService;
	private readonly WebRtcService WebRtc = webRtc;

	// Every write to the socket takes this. Impunity never overlaps its own reliable writes, but a signaling reply
	// is sent from another task and WebSocket.SendAsync must not run twice at once.
	private readonly SemaphoreSlim SendLock = new(1, 1);

	// The current WebRTC session, if the client asked for one. Replaced when the client offers again.
	private WebRtcSession? RtcSession;
	private int OfferInProgress;
	private volatile bool Disconnected;
	private IPAddress RemoteIPAddress = IPAddress.Any;

	private const int ConnectEstablishTimeout = 2500;
	private const int CloseHandshakeTimeout = 2000;

	private readonly byte[] ReceiveBuffer = new byte[ImpunityConstants.MaxMessageSize];

	public bool SupportsUnguaranteed { get => RtcSession?.IsOpen == true; }
	public int MaxUnguaranteedMessageBytes { get => ImpunityConstants.MaxUnguaranteedMessageSize; }
	public string RemoteAddress { get => RemoteIPAddress.ToString(); }

	public string ConnectionId { get; private set; } = "";

	public ImpunityServerMessageHandler? OnMessageRecieved { get; set; }
	public ImpunityServerMessageHandler? OnUnguaranteedMessageRecieved { get; set; }
	public ImpunityServerErrorCallback? OnNetworkError { get; set; }
	public ImpunityServerClientContextCallback? OnClientDisconnected { get; set; }



	[Route("/ws")]
	public async Task Connect()
	{
		if (HttpContext.Connection.RemoteIpAddress == null)
		{
			throw new Exception("Null connection address");
		}

		RemoteIPAddress = HttpContext.Connection.RemoteIpAddress;

		if (HttpContext.WebSockets.IsWebSocketRequest)
		{
			Socket = await HttpContext.WebSockets.AcceptWebSocketAsync();

			this.ConnectionId = "ws_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
			Connections.NetworkServer.ClientConnected(this);

			await Listen();
		}
		else
		{
			HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
		}
	}

	public async Task Listen()
	{
		using (Socket)
		{
			await ReadLoop();
		}
		Socket = null;
	}

	private async Task ReadLoop()
	{
		if (Socket == null)
		{
			return;
		}

		int bytesBuffered = 0;
		bool firstMessage = true;

		// One deadline for the first Impunity message, however many signaling messages arrive before it.
		using var establishTimeout = CancellationTokenSource.CreateLinkedTokenSource(ReadCancelSource.Token);
		establishTimeout.CancelAfter(ConnectEstablishTimeout);

		while (!ReadCancelSource.IsCancellationRequested)
		{
			try
			{
				CancellationToken token = firstMessage ? establishTimeout.Token : ReadCancelSource.Token;

				var receiveResult = await Socket.ReceiveAsync(
					new ArraySegment<byte>(ReceiveBuffer, bytesBuffered, ReceiveBuffer.Length - bytesBuffered),
					token);

				if (receiveResult.CloseStatus.HasValue)
				{
					break;
				}

				bytesBuffered += receiveResult.Count;

				if (!receiveResult.EndOfMessage)
				{
					// Fragment received — continue accumulating
					continue;
				}

				if (receiveResult.MessageType == WebSocketMessageType.Text)
				{
					HandleSignal(Encoding.UTF8.GetString(ReceiveBuffer, 0, bytesBuffered));
					bytesBuffered = 0;
					continue;
				}

				firstMessage = false;

				try
				{
					OnMessageRecieved?.Invoke(this, new ArraySegment<byte>(ReceiveBuffer, 0, bytesBuffered));
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in websocket message handler", e);
				}

				bytesBuffered = 0;
			}
			catch (OperationCanceledException)
			{
				if (firstMessage)
				{
					ImpunityLogger.LogWarning("Closed connection because it took too long to send establish");
				}
				break;
			}
			catch (Exception ex)
			{
				Logger.LogError("Exception in websocket read: {ex}", ex.ToString());

				try
				{
					OnNetworkError?.Invoke(this, new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, ex));
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in websocket error handler", e);
				}

				await CloseSocketAsync(Socket, WebSocketCloseStatus.EndpointUnavailable);

				OnDisconnected();
				return;
			}
		}

		await CloseSocketAsync(Socket, WebSocketCloseStatus.NormalClosure);

		OnDisconnected();
	}

	// Runs the close handshake, but never lets it keep the disconnect from being reported. A peer that stopped reading
	// leaves one of our sends pending forever, and the close frame would queue behind it; the server drops such a client
	// (silence, send backlog) precisely so its locks and entities get cleaned up, which only happens once
	// OnClientDisconnected fires. So the close is bounded, and anything that goes wrong aborts the socket instead.
	private static async Task CloseSocketAsync(WebSocket socket, WebSocketCloseStatus status)
	{
		if (socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted)
		{
			return;
		}

		using var timeout = new CancellationTokenSource(CloseHandshakeTimeout);
		try
		{
			await socket.CloseAsync(status, "Closed", timeout.Token);
		}
		catch (Exception)
		{
			// Timed out or the connection is already broken: nothing more to say to this peer.
			socket.Abort();
		}
	}

	public async Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
	{
		var socket = Socket;
		if (socket == null)
		{
			return;
		}

		await SendLock.WaitAsync();
		try
		{
			// The server batches several framed messages into one send; WebSocket clients expect exactly one per
			// WebSocket message, so split them back apart on their length prefixes.
			while (messageBytes.Count > 0)
			{
				int length = ImpunityNetworkingUtil.GetMessageLength(messageBytes);
				await socket.SendAsync(messageBytes.Slice(0, length), WebSocketMessageType.Binary, true, CancellationToken.None);
				messageBytes = messageBytes.Slice(length);
			}
		}
		finally
		{
			SendLock.Release();
		}
	}

	/// <summary>Sends over the WebRTC data channel. Only called while <see cref="SupportsUnguaranteed"/> is true; if the
	/// channel closed in the meantime the message is dropped, never rerouted to the socket from here.</summary>
	public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
	{
		RtcSession?.Send(messageBytes);
		return Task.CompletedTask;
	}

	// ----- WebRTC signaling

	private void HandleSignal(string text)
	{
		string? sdp = null;
		try
		{
			using JsonDocument message = JsonDocument.Parse(text);
			if (message.RootElement.TryGetProperty("t", out JsonElement type) && type.GetString() == "offer"
				&& message.RootElement.TryGetProperty("sdp", out JsonElement sdpElement))
			{
				sdp = sdpElement.GetString();
			}
		}
		catch (JsonException)
		{
		}

		if (sdp == null)
		{
			// Not something this server understands; nothing to answer.
			return;
		}

		// One negotiation at a time per socket. A client that offers again mid-answer is told no and may retry later.
		if (Interlocked.CompareExchange(ref OfferInProgress, 1, 0) != 0)
		{
			_ = SendSignalAsync("unavailable", "r", "busy");
			return;
		}

		_ = AnswerOfferAsync(sdp);
	}

	private async Task AnswerOfferAsync(string offer)
	{
		WebRtcSession? session = null;
		try
		{
			// A new offer replaces whatever the client had before.
			Interlocked.Exchange(ref RtcSession, null)?.Close("replaced by a new offer");

			session = WebRtc.CreateSession(out string reason);
			if (session == null)
			{
				await SendSignalAsync("unavailable", "r", reason);
				return;
			}

			session.OnMessage = OnRtcMessage;
			session.OnClosed = OnRtcClosed;
			RtcSession = session;
			if (Disconnected)
			{
				// The socket went away while the session was being made.
				session.Close("socket closed");
				return;
			}

			string answer = await session.AnswerAsync(offer);
			await SendSignalAsync("answer", "sdp", answer);
		}
		catch (Exception e)
		{
			Logger.LogDebug("WebRTC offer from {address} failed: {error}", RemoteAddress, e.Message);
			session?.Close("offer failed: " + e.Message);
			await SendSignalAsync("unavailable", "r", "failed");
		}
		finally
		{
			Interlocked.Exchange(ref OfferInProgress, 0);
		}
	}

	private async Task SendSignalAsync(string type, string key, string value)
	{
		var socket = Socket;
		if (socket == null || Disconnected)
		{
			return;
		}

		try
		{
			byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new System.Collections.Generic.Dictionary<string, string> { ["t"] = type, [key] = value });
			await SendLock.WaitAsync();
			try
			{
				await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
			}
			finally
			{
				SendLock.Release();
			}
		}
		catch (Exception)
		{
			// The socket is closing; the read loop reports that.
		}
	}

	// Called on a libdatachannel thread.
	private void OnRtcMessage(ArraySegment<byte> message)
	{
		try
		{
			OnUnguaranteedMessageRecieved?.Invoke(this, message);
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("Exception in WebRTC message handler", e);
		}
	}

	private void OnRtcClosed(WebRtcSession session, string reason)
	{
		// Unguaranteed sends go back to the WebSocket from here on. The client offers again if it wants another try.
		Interlocked.CompareExchange(ref RtcSession, null, session);
	}

	public void Disconnect()
	{
		ReadCancelSource.Cancel();
	}

	private void OnDisconnected()
	{
		Disconnected = true;
		Interlocked.Exchange(ref RtcSession, null)?.Close("socket closed");

		try
		{
			OnClientDisconnected?.Invoke(this);
		}
		catch (Exception e)
		{
			ImpunityLogger.LogError("Exception in websocket disconnect handler", e);
		}
	}

	public void Dispose()
	{
		if (Socket != null)
		{
			Socket.Dispose();
			Socket = null;
		}
		ReadCancelSource.Dispose();
		Interlocked.Exchange(ref RtcSession, null)?.Close("disposed");
	}
}
