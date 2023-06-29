using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Impunity.Networking
{
	public class ImpunityTCPClient : IImpunityClient
	{
		IPEndPoint ServerEndpoint;
		ImpunityOptions Options;

		TcpClient ClientSocket;
		Thread ClientSocketThread;

		NetworkStream SocketStream;

		ImpunityCallback OnConnectCallback;

		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		public ImpunityCallback OnNetworkError { get; set; }

		public static IImpunityClient MakeTCPClient(IPEndPoint serverEndpoint, ImpunityOptions options = null)
		{
			ImpunityTCPClient client = new ImpunityTCPClient(serverEndpoint, options);
			return client;
		}

		private ImpunityTCPClient(IPEndPoint serverEndpoint, ImpunityOptions options = null)
		{
			ServerEndpoint = serverEndpoint;
			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;

			ClientSocket = new TcpClient();
		}

		public void Connect(ImpunityCallback onComplete)
		{
			OnConnectCallback = onComplete;

			ClientSocketThread = new Thread(new ThreadStart(SocketListenerThread));
			ClientSocketThread.IsBackground = true;
			ClientSocketThread.Name = "TCPClient socket reader";
			ClientSocketThread.Start();
		}

		public void Disconnect()
		{
			if (ClientSocket != null)
			{
				TcpClient client = ClientSocket;
				ClientSocket = null;
				client.Close();

			}
		}

		public bool SupportsUnguaranteed()
		{
			return false;
		}

		private void SocketListenerThread()
		{
			byte[] receiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
			bool connected = false;

			try
			{
				ClientSocket.Connect(ServerEndpoint);

				SocketStream = ClientSocket.GetStream();

				connected = true;
				OnConnectCallback?.Invoke(null);
				OnConnectCallback = null;

				int bytesReceived = 0;
				int maxBytesToReceive = ImpunityConstants.MaxMessageSize;

				while (ClientSocket != null)
				{
					int bytesRead = SocketStream.Read(receiveBuffer, bytesReceived, maxBytesToReceive);
					if (bytesRead == 0)
					{
						// Socket closed
						break;
					}

					bytesReceived += bytesRead;
					if (bytesReceived < 4)
					{
						// Didn't read enough to get a size, this is probably some kind of error
						ImpunityLogger.LogWarning("Only read " + bytesReceived + " from the TCP socket");
						maxBytesToReceive = ImpunityConstants.MaxMessageSize - bytesReceived;
						continue;
					}

					int messageLength = ImpunityNetworkingUtil.GetMessageLength(receiveBuffer);
					if (bytesReceived < messageLength)
					{
						ImpunityLogger.LogDebug("Got partial message: " + bytesReceived + " / " + messageLength);
						maxBytesToReceive = messageLength - bytesReceived;
						continue;
					}

					try
					{
						OnMessageRecieved.Invoke(new ArraySegment<byte>(receiveBuffer, 0, messageLength));
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError(e, "Error in OnMessageReceieved handler");
					}

					bytesReceived = 0;
					maxBytesToReceive = ImpunityConstants.MaxMessageSize;
				}
			}
			catch (Exception e)
			{
				if (!connected)
                {
					OnConnectCallback?.Invoke(new ImpunityError(e.Message));
					OnConnectCallback = null;
				}
				else if (ClientSocket != null)
				{
					// Client socket is null on regular requested disonnect
					OnNetworkError?.Invoke(new ImpunityError(e.Message));

					ImpunityLogger.LogError(e, "Client socket error");
				}
			}
			finally
			{
				if (ClientSocket != null)
				{
					ClientSocket.Close();
					ClientSocket.Dispose();
					ClientSocket = null;
				}
			}

			ImpunityLogger.LogInformation("Client socket closed");
		}

		public void SendGuaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (ClientSocket == null || !ClientSocket.Connected)
			{
				return;
			}

			SocketStream.Write(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
		}

		public void SendUnguaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (ClientSocket == null || !ClientSocket.Connected)
			{
				return;
			}

			SocketStream.Write(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
		}

		public void Dispose()
		{
			Disconnect();
		}


	}

}