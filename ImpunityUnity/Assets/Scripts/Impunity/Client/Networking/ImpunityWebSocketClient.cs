#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;

namespace Impunity.Networking
{

	/// <summary>Client-side WebSocket network transport for WebGL platform.
	/// Uses JavaScript WebSocket via jslib interop. All callbacks fire on the main thread.</summary>
	public class ImpunityWebSocketClient : IImpunityNetworkClient
	{
		/// <inheritdoc/>
		public string ConnectionId { get; private set; }

		/// <inheritdoc/>
		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		/// <inheritdoc/>
		public ImpunityCallback OnNetworkError { get; set; }
		/// <inheritdoc/>
		public Action<int> OnDisconnectedByServer { get; set; }

		/// <inheritdoc/>
		public bool SupportsUnguaranteed { get; private set; } = false;

		private string Url;
		private int InstanceId = -1;
		private bool Connected;

		private ImpunityCallback OnConnectCallback;

		private static bool Initialized;
		private static Dictionary<int, ImpunityWebSocketClient> Instances = new Dictionary<int, ImpunityWebSocketClient>();


		/// <summary>Creates a WebSocket client that connects to a server at the given URL (e.g. ws://host:port/ws).</summary>
		public static IImpunityNetworkClient MakeWebSocketClient(string host, int port, ImpunityOptions options = null)
		{
			string url = $"ws://{host}:{port}/ws";
			return new ImpunityWebSocketClient(url, options);
		}

		private ImpunityWebSocketClient(string url, ImpunityOptions options)
		{
			Url = url;
			InitializeJsBridge();
		}

		private static void InitializeJsBridge()
		{
			if (Initialized)
			{
				return;
			}
			Initialized = true;

			ImpunityWsInitialize();
			ImpunityWsSetOpenCallback(OnJsOpen);
			ImpunityWsSetMessageCallback(OnJsMessage);
			ImpunityWsSetErrorCallback(OnJsError);
			ImpunityWsSetCloseCallback(OnJsClose);
		}


		/// <inheritdoc/>
		public void Connect(ImpunityCallback onComplete)
		{
			OnConnectCallback = onComplete;

			InstanceId = ImpunityWsCreate(Url);
			Instances[InstanceId] = this;

			int result = ImpunityWsConnect(InstanceId);
			if (result < 0)
			{
				Instances.Remove(InstanceId);
				OnConnectCallback?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError,
					new Exception("WebSocket connect failed: " + result)));
				OnConnectCallback = null;
			}
		}

		/// <inheritdoc/>
		public void SendGuaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (InstanceId < 0 || !Connected)
			{
				return;
			}

			ImpunityWsSend(InstanceId, messageBytes.Array, messageBytes.Count);
		}

		/// <inheritdoc/>
		public void SendUnguaranteedMessage(ArraySegment<byte> messageBytes)
		{
			SendGuaranteedMessage(messageBytes);
		}

		/// <inheritdoc/>
		public void Disconnect()
		{
			if (InstanceId >= 0)
			{
				Connected = false;
				int id = InstanceId;
				InstanceId = -1;
				Instances.Remove(id);
				ImpunityWsClose(id, 1000);
				ImpunityWsDelete(id);
			}
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			Disconnect();
		}


		// JS callback handlers — static because MonoPInvokeCallback requires static methods

		[MonoPInvokeCallback(typeof(JsCallback))]
		private static void OnJsOpen(int instanceId)
		{
			if (!Instances.TryGetValue(instanceId, out var client))
			{
				return;
			}

			client.Connected = true;

			try
			{
				client.OnConnectCallback?.Invoke(null);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in WebSocket connect callback", e);
			}
			client.OnConnectCallback = null;
		}

		[MonoPInvokeCallback(typeof(JsMessageCallback))]
		private static void OnJsMessage(int instanceId, IntPtr bufferPtr, int bufferLength)
		{
			if (!Instances.TryGetValue(instanceId, out var client))
			{
				return;
			}

			byte[] data = new byte[bufferLength];
			Marshal.Copy(bufferPtr, data, 0, bufferLength);

			try
			{
				client.OnMessageRecieved?.Invoke(new ArraySegment<byte>(data, 0, bufferLength));
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error in OnMessageReceived handler", e);
			}
		}

		[MonoPInvokeCallback(typeof(JsCallback))]
		private static void OnJsError(int instanceId)
		{
			if (!Instances.TryGetValue(instanceId, out var client))
			{
				return;
			}

			if (!client.Connected)
			{
				// Error during connection attempt
				try
				{
					client.OnConnectCallback?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError,
						new Exception("WebSocket connection error")));
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in WebSocket connect callback", e);
				}
				client.OnConnectCallback = null;
			}
			else
			{
				try
				{
					client.OnNetworkError?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError,
						new Exception("WebSocket error")));
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in WebSocket error callback", e);
				}
			}
		}

		[MonoPInvokeCallback(typeof(JsCloseCallback))]
		private static void OnJsClose(int instanceId, int code)
		{
			if (!Instances.TryGetValue(instanceId, out var client))
			{
				return;
			}

			Instances.Remove(instanceId);
			client.Connected = false;
			client.InstanceId = -1;

			try
			{
				client.OnDisconnectedByServer?.Invoke(code);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error in OnDisconnectedByServer handler", e);
			}
		}


		// JS interop declarations

		private delegate void JsCallback(int instanceId);
		private delegate void JsMessageCallback(int instanceId, IntPtr bufferPtr, int bufferLength);
		private delegate void JsCloseCallback(int instanceId, int code);

		[DllImport("__Internal")]
		private static extern void ImpunityWsInitialize();
		[DllImport("__Internal")]
		private static extern int ImpunityWsCreate(string url);
		[DllImport("__Internal")]
		private static extern void ImpunityWsDelete(int instanceId);
		[DllImport("__Internal")]
		private static extern int ImpunityWsConnect(int instanceId);
		[DllImport("__Internal")]
		private static extern int ImpunityWsClose(int instanceId, int code);
		[DllImport("__Internal")]
		private static extern int ImpunityWsSend(int instanceId, byte[] data, int length);
		[DllImport("__Internal")]
		private static extern int ImpunityWsGetState(int instanceId);

		[DllImport("__Internal")]
		private static extern void ImpunityWsSetOpenCallback(JsCallback callback);
		[DllImport("__Internal")]
		private static extern void ImpunityWsSetMessageCallback(JsMessageCallback callback);
		[DllImport("__Internal")]
		private static extern void ImpunityWsSetErrorCallback(JsCallback callback);
		[DllImport("__Internal")]
		private static extern void ImpunityWsSetCloseCallback(JsCloseCallback callback);
	}
}
#else


using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;

namespace Impunity.Networking
{
	/// <summary>Client-side WebSocket network transport. Connects to an Impunity server's /ws endpoint using System.Net.WebSockets.ClientWebSocket.
	/// Matches the ImpunityTCPClient pattern: background thread with blocking read loop.</summary>
	public class ImpunityWebSocketClient : IImpunityNetworkClient
	{
		/// <inheritdoc/>
		public string ConnectionId { get; private set; }

		/// <inheritdoc/>
		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		/// <inheritdoc/>
		public ImpunityCallback OnNetworkError { get; set; }
		/// <inheritdoc/>
		public Action<int> OnDisconnectedByServer { get; set; }

		/// <inheritdoc/>
		public bool SupportsUnguaranteed { get; private set; } = false;

		private string Url;
		private ImpunityOptions Options;

		private ClientWebSocket Socket;
		private Thread SocketThread;
		private CancellationTokenSource CancelSource;

		private ImpunityCallback OnConnectCallback;


		/// <summary>Creates a WebSocket client that connects to a server at the given URL (e.g. ws://host:port/ws).</summary>
		public static IImpunityNetworkClient MakeWebSocketClient(string host, int port, ImpunityOptions options = null)
		{
			string url = $"ws://{host}:{port}/ws";
			return new ImpunityWebSocketClient(url, options);
		}

		private ImpunityWebSocketClient(string url, ImpunityOptions options)
		{
			Url = url;
			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;
		}

		/// <inheritdoc/>
		public void Connect(ImpunityCallback onComplete)
		{
			OnConnectCallback = onComplete;
			CancelSource = new CancellationTokenSource();

			SocketThread = new Thread(new ThreadStart(WebSocketListenerThreadMain));
			SocketThread.IsBackground = true;
			SocketThread.Name = "WebSocketClient reader";
			SocketThread.Start();
		}

		private void WebSocketListenerThreadMain()
		{
			try
			{
				WebSocketListener().Wait();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error in websocket listener thread: ", e);
			}
		}

		private async Task WebSocketListener()
		{
			ImpunityLogger.LogInformation("Started websock listener thread");

			byte[] receiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
			bool connected = false;

			try
			{
				Socket = new ClientWebSocket();
				await Socket.ConnectAsync(new Uri(Url), CancelSource.Token);

				connected = true;
				OnConnectCallback?.Invoke(null);
				OnConnectCallback = null;

				int bytesReceived = 0;

				while (Socket.State == WebSocketState.Open)
				{
					var segment = new ArraySegment<byte>(receiveBuffer, bytesReceived, receiveBuffer.Length - bytesReceived);
					var result = await Socket.ReceiveAsync(segment, CancelSource.Token);

					if (result.CloseStatus.HasValue)
					{
						break;
					}

					bytesReceived += result.Count;

					if (!result.EndOfMessage)
					{
						// Fragment received — continue accumulating
						continue;
					}

					try
					{
						OnMessageRecieved?.Invoke(new ArraySegment<byte>(receiveBuffer, 0, bytesReceived));
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Error in OnMessageReceived handler", e);
					}

					bytesReceived = 0;
				}
			}
			catch (Exception e)
			{
				// Unwrap AggregateException from .Wait()/.Result
				Exception inner = e is AggregateException ae ? ae.InnerException : e;

				if (!connected)
				{
					OnConnectCallback?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError, inner));
					OnConnectCallback = null;
				}
				else if (Socket != null)
				{
					// Socket is null on intentional disconnect
					OnNetworkError?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, inner));
					ImpunityLogger.LogError("WebSocket client error", inner);
				}
			}
			finally
			{
				if (Socket != null)
				{
					try
					{
						Socket.Dispose();
					}
					catch (Exception) { }
					Socket = null;

					try
					{
						OnDisconnectedByServer?.Invoke(0);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Error in OnDisconnectedByServer handler: ", e);
					}
				}
			}

			ImpunityLogger.LogInformation("WebSocket client closed");
		}

		/// <inheritdoc/>
		public void SendGuaranteedMessage(ArraySegment<byte> messageBytes)
		{
			ClientWebSocket socket = Socket;
			if (socket == null || socket.State != WebSocketState.Open)
			{
				return;
			}

			socket.SendAsync(messageBytes, WebSocketMessageType.Binary, true, CancellationToken.None).Wait();
		}

		/// <inheritdoc/>
		public void SendUnguaranteedMessage(ArraySegment<byte> messageBytes)
		{
			SendGuaranteedMessage(messageBytes);
		}

		/// <inheritdoc/>
		public void Disconnect()
		{
			if (Socket != null)
			{
				ClientWebSocket socket = Socket;
				Socket = null;

				try
				{
					if (socket.State == WebSocketState.Open)
					{
						socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None).Wait(1000);
					}
				}
				catch (Exception) { }

				try
				{
					socket.Dispose();
				}
				catch (Exception) { }
			}

		}

		/// <inheritdoc/>
		public void Dispose()
		{
			Disconnect();
		}
	}
}

#endif