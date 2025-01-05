#nullable enable

using System;
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
	private IPAddress RemoteAddress = IPAddress.Any;

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

		RemoteAddress = HttpContext.Connection.RemoteIpAddress;

        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            Socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
			
			Connections.NetworkServer.ClientConnected(this);

			await Listen();
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }


    public string GetAddress()
    {
        return RemoteAddress.ToString();
    }

    public bool SupportsUnguaranteed()
    {
        return false;
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
			// Will never happen but makes nullable checking happy
			return;
		}

		var buffer = new byte[ImpunityConstants.MaxMessageSize];
		
		while (!ReadCancelSource.IsCancellationRequested)
		{
			try
			{
				var receiveResult = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), ReadCancelSource.Token);

				if (receiveResult.CloseStatus.HasValue)
				{
					break;
				}
				
				if(!receiveResult.EndOfMessage)
				{
					Logger.LogInformation("Got message fragment");
				}

				try
				{
					OnMessageRecieved?.Invoke(this, new ArraySegment<byte>(buffer, 0, receiveResult.Count));
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in websocket message handler", e);
				}
			}
			catch(OperationCanceledException)
			{
				break;
			}
			catch(Exception ex)
			{
				Logger.LogError("Exception in websocket read: {ex}", ex.ToString());
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
		if (Socket == null)
		{
			return Task.CompletedTask;
		}

        return Socket.SendAsync(messageBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
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
    }
}