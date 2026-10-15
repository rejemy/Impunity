#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Impunity.Networking;
using System.Net;

namespace Impunity.StandaloneServer;

public class WebSocketController(ILogger<WebSocketController> logger, ConnectionService connectionService) : ControllerBase, IImpunityNetworkServerClientContext
{
	private readonly CancellationTokenSource ReadCancelSource = new();
	private WebSocket? Socket;
	private readonly ILogger<WebSocketController> Logger = logger;
	private readonly ConnectionService Connections = connectionService;
	private IPAddress RemoteIPAddress = IPAddress.Any;

	private const int ConnectEstablishTimeout = 2500;
	private const int CloseHandshakeTimeout = 2000;

	private readonly byte[] ReceiveBuffer = new byte[ImpunityConstants.MaxMessageSize];

	public bool SupportsUnguaranteed { get => false; }
	public string RemoteAddress { get => RemoteIPAddress.ToString(); }

	public string ConnectionId { get; private set; } = "";

	public ImpunityServerMessageHandler? OnMessageRecieved { get; set; }
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

		while (!ReadCancelSource.IsCancellationRequested)
		{
			try
			{
				CancellationToken token;
				if (firstMessage)
				{
					var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ReadCancelSource.Token);
					timeoutSource.CancelAfter(ConnectEstablishTimeout);
					token = timeoutSource.Token;
				}
				else
				{
					token = ReadCancelSource.Token;
				}

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

	public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
	{
		var socket = Socket;
		if (socket == null)
		{
			return Task.CompletedTask;
		}

		if (ImpunityNetworkingUtil.GetMessageLength(messageBytes) >= messageBytes.Count)
		{
			return socket.SendAsync(messageBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
		}

		return SendEachAsync(socket, messageBytes);
	}

	// The server batches several framed messages into one send; WebSocket clients expect exactly one per WebSocket
	// message, so split them back apart on their length prefixes.
	private static async Task SendEachAsync(WebSocket socket, ArraySegment<byte> messages)
	{
		while (messages.Count > 0)
		{
			int length = ImpunityNetworkingUtil.GetMessageLength(messages);
			await socket.SendAsync(messages.Slice(0, length), WebSocketMessageType.Binary, true, CancellationToken.None);
			messages = messages.Slice(length);
		}
	}

	public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
	{
		return SendGuaranteedMessageAsync(messageBytes);
	}

	public void Disconnect()
	{
		ReadCancelSource.Cancel();
	}

	private void OnDisconnected()
	{
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
	}
}
