#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;


namespace Impunity.StandaloneServer;

public class WebSocketController(ILogger<WebSocketController> logger) : ControllerBase
{
	private readonly CancellationTokenSource ReadCancelSource = new();
	private WebSocket? Socket;
	private readonly ILogger<WebSocketController> Logger = logger;

    [Route("/ws")]
    public async Task Connect()
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            Socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
			using(Socket)
			{
				await ReadLoop();
			}
            
        }
        else
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

	public void Close()
	{
		ReadCancelSource.Cancel();
	}

	public void SendMessage()
	{
		
	}

	private async Task ReadLoop()
	{
		if (Socket == null)
		{
			// Will never happen but makes nullable checking happy
			return;
		}

		var buffer = new byte[1024 * 8];
		
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

				Logger.LogInformation("Read bytes: {bytes}", receiveResult.Count);
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
			}
		}

		await Socket.CloseAsync(
			WebSocketCloseStatus.NormalClosure,
			"Closed",
			CancellationToken.None);
	}
}