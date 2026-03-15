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

	private const int ConnectEstablishTimeout = 1000;

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
        using(Socket)
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
			catch(OperationCanceledException)
			{
				if (firstMessage)
				{
					ImpunityLogger.LogWarning("Closed connection because it took too long to send establish");
				}
				break;
			}
			catch(Exception ex)
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

				await Socket.CloseAsync(
					WebSocketCloseStatus.EndpointUnavailable,
					"Closed",
					CancellationToken.None);
				OnDisconnected();
				return;
			}
		}

		await Socket.CloseAsync(
			WebSocketCloseStatus.NormalClosure,
			"Closed",
			CancellationToken.None);
		OnDisconnected();
	}

    public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
    {
		var socket = Socket;
		if (socket == null)
		{
			return Task.CompletedTask;
		}

        return socket.SendAsync(messageBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
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
        if(Socket != null)
		{
			Socket.Dispose();
			Socket = null;
		}
		ReadCancelSource.Dispose();
    }
}
