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

		public NetworkMessageHandler OnMessageRecieved { get; set; }
		public NetworkErrorHandler OnNetworkError { get; set; }

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

		public void Connect()
		{
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
			try
			{
				ClientSocket.Connect(ServerEndpoint);

				SocketStream = ClientSocket.GetStream();

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
						OnMessageRecieved.Invoke(receiveBuffer, messageLength);
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
				if (ClientSocket != null)
				{
					OnNetworkError?.Invoke(e.Message);

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

		public void SendGuaranteedMessage(byte[] buffer, int offset, int length)
		{
			if (ClientSocket == null || !ClientSocket.Connected)
			{
				return;
			}

			SocketStream.Write(buffer, offset, length);
		}

		public void SendUnguaranteedMessage(byte[] buffer, int offset, int length)
		{
			if (ClientSocket == null || !ClientSocket.Connected)
			{
				return;
			}

			SocketStream.Write(buffer, offset, length);
		}

		public void Dispose()
		{
			Disconnect();
		}


	}

}