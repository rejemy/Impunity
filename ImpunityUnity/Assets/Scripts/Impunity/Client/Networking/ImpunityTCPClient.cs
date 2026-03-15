#if !UNITY_WEBGL

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Impunity.Networking
{
	/// <summary>Client-side TCP (and optional UDP) network transport. Connects to an Impunity server, reads framed messages on a background thread, and supports both guaranteed (TCP) and unguaranteed (UDP) sends.</summary>
	public class ImpunityTCPClient : IImpunityNetworkClient
	{
		/// <inheritdoc/>
		public string ConnectionId { get; private set; }

		/// <inheritdoc/>
		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		/// <inheritdoc/>
		public ImpunityCallback OnNetworkError { get; set; }
		/// <inheritdoc/>
		public Action<int> OnDisconnectedByServer { get; set; }

		/// <summary>True after a successful UDP ping/pong exchange with the server.</summary>
		public bool SupportsUnguaranteed { get; private set; } = false;

		private string ServerHost;
		private int ServerPort;

		private IPEndPoint ServerEndpoint;
		private ImpunityOptions Options;

		private TcpClient ClientTCPSocket;
		private Thread ClientTCPSocketThread;
		private NetworkStream TCPSocketStream;

		private UdpClient ClientUDPSocket;
		private Thread ClientUDPSocketThread;
	
		private ImpunityCallback OnConnectCallback;


		byte[] SessionDataPacket;
		byte[] PingPacket;
		byte[] PongPacket;

		/// <summary>Creates a TCP client that connects to a server at the given IP endpoint.</summary>
		public static IImpunityNetworkClient MakeTCPClient(IPEndPoint serverEndpoint, ImpunityOptions options = null)
		{
			ImpunityTCPClient client = new ImpunityTCPClient(serverEndpoint, options);
			return client;
		}

		/// <summary>Creates a TCP client that connects to a server at the given hostname and port.</summary>
		public static IImpunityNetworkClient MakeTCPClient(string hostname, int port, ImpunityOptions options = null)
		{
			ImpunityTCPClient client = new ImpunityTCPClient(hostname, port, options);
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

			ClientTCPSocket = new TcpClient();
		}

		private ImpunityTCPClient(string hostname, int port, ImpunityOptions options = null)
		{
			ServerHost = hostname;
			ServerPort = port;

			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;

			ClientTCPSocket = new TcpClient();
		}

		/// <summary>Initiates connection on a background thread. Calls <paramref name="onComplete"/> with null on success or an error response on failure.</summary>
		public void Connect(ImpunityCallback onComplete)
		{
			OnConnectCallback = onComplete;

			ClientTCPSocketThread = new Thread(new ThreadStart(TCPSocketListenerThread));
			ClientTCPSocketThread.IsBackground = true;
			ClientTCPSocketThread.Name = "TCPClient socket reader";
			ClientTCPSocketThread.Start();
		}

		private void OpenUdpClient()
		{
			ClientUDPSocketThread = new Thread(new ThreadStart(UDPSocketListenerThread));
			ClientUDPSocketThread.IsBackground = true;
			ClientUDPSocketThread.Name = "UDPClient socket reader";
			ClientUDPSocketThread.Start();
		}

		/// <summary>Closes the TCP connection. The background reader thread will exit and fire <see cref="OnDisconnectedByServer"/>.</summary>
		public void Disconnect()
		{
			if (ClientTCPSocket != null)
			{
				TcpClient client = ClientTCPSocket;
				ClientTCPSocket = null;
				client.Close();
			}

			if (ClientUDPSocket != null)
			{
				UdpClient client = ClientUDPSocket;
				ClientUDPSocket = null;
				client.Close();
			}
		}


		private void TCPSocketListenerThread()
		{
			byte[] receiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
			bool connected = false;

			try
			{
				if (ServerHost != null)
				{
					ClientTCPSocket.Connect(ServerHost, ServerPort);
				}
				else
				{
					ClientTCPSocket.Connect(ServerEndpoint);
				}

				ServerEndpoint = ClientTCPSocket.Client.RemoteEndPoint as IPEndPoint;
				TCPSocketStream = ClientTCPSocket.GetStream();

				// Open Udp socket on the same port as the TCP connection
				OpenUdpClient();
				
				connected = true;
				OnConnectCallback?.Invoke(null);
				OnConnectCallback = null;

				int bytesReceived = 0;

				while (ClientTCPSocket != null)
				{
					int bytesRead = TCPSocketStream.Read(receiveBuffer, bytesReceived, receiveBuffer.Length - bytesReceived);
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
						continue;
					}

					int messageLength = ImpunityNetworkingUtil.GetMessageLength(receiveBuffer);
					if (bytesReceived < messageLength)
					{
						ImpunityLogger.LogDebug("Got partial message: " + bytesReceived + " / " + messageLength);
						continue;
					}

					try
					{
						OnMessageRecieved.Invoke(new ArraySegment<byte>(receiveBuffer, 0, messageLength));
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Error in OnMessageReceieved handler", e);
					}

					if (bytesReceived > messageLength)
					{
						// If there is any data left over, compact it down and continue reading
						int extraData = bytesReceived - messageLength;
						ImpunityLogger.LogDebug("Got part of next message: " + extraData);
						Buffer.BlockCopy(receiveBuffer, messageLength, receiveBuffer, 0, extraData);
						bytesReceived = extraData;
					}
					else
					{
						bytesReceived = 0;
					}
				}
			}
			catch (Exception e)
			{
				if (!connected)
                {
					OnConnectCallback?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientUnableToConnectError, e));
					OnConnectCallback = null;
				}
				else if (ClientTCPSocket != null)
				{
					// Client socket is null on regular requested disonnect
					OnNetworkError?.Invoke(new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, e));

					ImpunityLogger.LogError("Client socket error", e);
				}
			}
			finally
			{
				if (ClientTCPSocket != null)
				{
					ClientTCPSocket.Close();
					ClientTCPSocket.Dispose();
					ClientTCPSocket = null;

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

			ImpunityLogger.LogInformation("Client socket closed");
		}

		private void UDPSocketListenerThread()
		{
			SessionDataPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSessionDataPacketHeader + Options.GameTypeCode + ":");
			PingPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPingPacketHeader + Options.GameTypeCode + ":");
			PongPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPongPacketHeader + Options.GameTypeCode + ":");

			try
			{
				// Open Udp socket on same port/address as established TCP session
				IPEndPoint localEndpoint = ClientTCPSocket.Client.LocalEndPoint as IPEndPoint;
				ImpunityLogger.LogInformation("Client listening for udp on " + localEndpoint.ToString());
				ClientUDPSocket = new UdpClient(localEndpoint);
				
				// See if we can reach the server and vice versa
				SendUdpPing();

				while (ClientUDPSocket != null)
				{
					IPEndPoint sender = null;
					byte[] packet = ClientUDPSocket.Receive(ref sender);
					if (!sender.Equals(ServerEndpoint))
					{
						// Some other random stray packet
						continue;
					}

					if (ImpunityUtil.StartsWith(packet, SessionDataPacket))
					{
						var packetBody = new ArraySegment<byte>(packet, SessionDataPacket.Length, packet.Length - SessionDataPacket.Length);
						OnSessionDataPacket(packetBody);
					}
					else if(ImpunityUtil.StartsWith(packet, PingPacket))
					{
						OnPingPacket();
					}
					else
					{
						ImpunityLogger.LogDebug("Got unknown UDP packet");
					}
				}
			}
			catch (SocketException e)
			{
				if (ClientUDPSocket == null)
				{
					return;
				}

				ImpunityLogger.LogError("UDP Socket error", e);
			}
			finally
			{
				if (ClientUDPSocket != null)
				{
					ClientUDPSocket.Close();
					ClientUDPSocket = null;
				}
			}
		}

		private void OnSessionDataPacket(ArraySegment<byte> data)
		{
			try
			{
				OnMessageRecieved.Invoke(data);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error in OnMessageReceieved handler", e);
			}
		}

		private void OnPingPacket()
		{
			this.SupportsUnguaranteed = true;

			try
			{
				ClientUDPSocket.Send(PongPacket, PongPacket.Length, ServerEndpoint);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception sending UDP packet", e);
			}
		}
		

		private void SendUdpPing()
		{
			try
			{
				ClientUDPSocket.Send(PingPacket, PingPacket.Length, this.ServerEndpoint);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception sending UDP packet", e);
			}
		}

		/// <summary>Sends a length-prefixed message over the TCP stream. Silently returns if not connected.</summary>
		public void SendGuaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (ClientTCPSocket == null || !ClientTCPSocket.Connected)
			{
				return;
			}

			TCPSocketStream.Write(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
		}

		/// <summary>Sends a message via UDP if available, otherwise falls back to TCP.</summary>
		public void SendUnguaranteedMessage(ArraySegment<byte> messageBytes)
		{
			if (ClientUDPSocket == null || !this.SupportsUnguaranteed)
			{
				SendGuaranteedMessage(messageBytes);
				return;
			}
			
			byte[] buffer = new byte[messageBytes.Count + SessionDataPacket.Length];
			Buffer.BlockCopy(SessionDataPacket, 0, buffer, 0, SessionDataPacket.Length);
			Buffer.BlockCopy(messageBytes.Array, messageBytes.Offset, buffer, SessionDataPacket.Length, messageBytes.Count);

			ClientUDPSocket.Send(buffer, buffer.Length, this.ServerEndpoint);
		}

		public void Dispose()
		{
			Disconnect();
		}

	}
}

#else

using System;
using System.Net;


namespace Impunity.Networking
{
	/// <summary>Client-side TCP (and optional UDP) network transport. Connects to an Impunity server, reads framed messages on a background thread, and supports both guaranteed (TCP) and unguaranteed (UDP) sends.</summary>
	public class ImpunityTCPClient : IImpunityNetworkClient
	{
		/// <inheritdoc/>
		public string ConnectionId { get; private set; }

		/// <inheritdoc/>
		public ImpunityClientMessageHandler OnMessageRecieved { get; set; }
		/// <inheritdoc/>
		public ImpunityCallback OnNetworkError { get; set; }
		/// <inheritdoc/>
		public Action<int> OnDisconnectedByServer { get; set; }

		/// <summary>True after a successful UDP ping/pong exchange with the server.</summary>
		public bool SupportsUnguaranteed { get; private set; } = false;


		/// <summary>Creates a TCP client that connects to a server at the given IP endpoint.</summary>
		public static IImpunityNetworkClient MakeTCPClient(IPEndPoint serverEndpoint, ImpunityOptions options = null)
		{
			throw new NotSupportedException("TCP Client not supported in WebGL");
		}

		/// <summary>Creates a TCP client that connects to a server at the given hostname and port.</summary>
		public static IImpunityNetworkClient MakeTCPClient(string hostname, int port, ImpunityOptions options = null)
		{
			throw new NotSupportedException("TCP Client not supported in WebGL");
		}


		/// <summary>Initiates connection on a background thread. Calls <paramref name="onComplete"/> with null on success or an error response on failure.</summary>
		public void Connect(ImpunityCallback onComplete)
		{
			
		}


		/// <summary>Closes the TCP connection. The background reader thread will exit and fire <see cref="OnDisconnectedByServer"/>.</summary>
		public void Disconnect()
		{
			
		}

		/// <summary>Sends a length-prefixed message over the TCP stream. Silently returns if not connected.</summary>
		public void SendGuaranteedMessage(ArraySegment<byte> messageBytes)
		{
			
		}

		/// <summary>Sends a message via UDP if available, otherwise falls back to TCP.</summary>
		public void SendUnguaranteedMessage(ArraySegment<byte> messageBytes)
		{
			
		}

		public void Dispose()
		{
			
		}
	}
}

#endif