using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;



namespace Impunity.Networking
{

	internal class TcpClientContext : IClientContext, IDisposable
	{
		ImpunityTCPServer Server;
		TcpClient Client;
		NetworkStream ClientStream;

		int BytesReceived = 0;
		int MaxBytesToReceive = ImpunityConstants.MaxMessageSize;
		byte[] ReceiveBuffer;
		byte[] SendBuffer;
		Semaphore SendLock;

		public EndPoint RemoteEndpoint { get { return Client.Client.RemoteEndPoint; } }

		public TcpClientContext(ImpunityTCPServer server, TcpClient client)
		{
			Server = server;
			Client = client;
			ClientStream = client.GetStream();
			ReceiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
			SendLock = new Semaphore(1, 1);
		}

		public void Read()
		{
			ClientStream.ReadAsync(ReceiveBuffer, 0, ImpunityConstants.MaxMessageSize)
				.ContinueWith(OnDataRead);
		}

		// On socket thread
		private void OnDataRead(Task<int> readTask)
		{
			if (!readTask.IsCompletedSuccessfully)
			{
				ImpunityLogger.LogError("Error reading socket");
				readTask.Dispose();
				OnSocketClosed();
				return;
			}

			int bytesRead = readTask.Result;
			readTask.Dispose();

			if (bytesRead == 0 || !Client.Connected)
			{
				OnSocketClosed();
				return;
			}

			HandleDataRead(bytesRead);

			// And finish by going back to reading
			ClientStream.ReadAsync(ReceiveBuffer, 0, MaxBytesToReceive)
				.ContinueWith(OnDataRead);

		}

		private void HandleDataRead(int bytesRead)
		{
			BytesReceived += bytesRead;

			if (BytesReceived < 4)
			{
				// Didn't read enough to get a size, this is probably some kind of error
				ImpunityLogger.LogWarning("Only read " + BytesReceived + " from the TCP socket");
				MaxBytesToReceive = ImpunityConstants.MaxMessageSize - BytesReceived;
				return;
			}

			int messageLength = ImpunityNetworkingUtil.GetMessageLength(ReceiveBuffer);
			if (BytesReceived < messageLength)
			{
				ImpunityLogger.LogDebug("Got partial message: " + BytesReceived + " / " + messageLength);
				MaxBytesToReceive = messageLength - BytesReceived;
				return;
			}

			// We have the whole message, handle it
			Server.HandleClientMessage(this, ReceiveBuffer, messageLength);
		}

		// Called on writer thread
		public void SendActionResult(ushort messageId, ImpunityError error, BsonValue result)
		{
			ArraySegment<byte> encodedMessage;

			ServerReply replyMessage = new ServerReply();
			replyMessage.Error = error;
			replyMessage.Result = result;
			BsonDocument replyDoc = ImpunityNetworkingUtil.GetBsonMapper().ToDocument(replyMessage);

			// Lock send buffer (or wait for it to be available)
			SendLock.WaitOne();

			encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBuffer, messageId, 0, ServerMessageTypes.REPLY, replyDoc);

			ClientStream.WriteAsync(encodedMessage.Array, 0, encodedMessage.Count).ContinueWith(OnDataWritten);
		}

		// Called on TCP socket thread
		private void OnDataWritten(Task writeTask)
		{
			if (!writeTask.IsCompletedSuccessfully)
			{
				ImpunityLogger.LogError("Error writing to socket");
				writeTask.Dispose();
				OnSocketClosed();
				return;
			}

			writeTask.Dispose();

			// Unlock send buffer
			SendLock.Release();
		}

		private void OnSocketClosed()
		{
			ImpunityLogger.LogInformation("A client disconnected");

			Server.OnSocketClosed(this);

			Dispose();
		}

		public void Dispose()
		{
			Client.Close();
			Client.Dispose();
		}
	}

	public class ImpunityTCPServer : ImpunityServerBase
	{
		ImpunityOptions Options;

		Thread TCPListenerThread;
		TcpListener TCPSocket;

		Thread UDPListenerThread;
		UdpClient ServerUdpSocket;

		bool Running;

		ArraySegment<byte> AnnouncePacket;
		byte[] SearchPacket;

		BlockingCollection<IImpunityAction> PendingReplyActions;
		ConcurrentDictionary<EndPoint, TcpClientContext> ConnectedClients;

		public ImpunityTCPServer(GameStateServer gameState, ImpunityOptions options = null) : base(gameState)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;

			Running = true;

			PendingReplyActions = new BlockingCollection<IImpunityAction>();
			ConnectedClients = new ConcurrentDictionary<EndPoint, TcpClientContext>();

		}

		public override void Start()
		{
			base.Start();

			StartTcpListener();

			if (Options.LANDiscoverable)
			{
				SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");

				AnnouncePacket = new ArraySegment<byte>(new byte[1024]);

				StartBroadcastListen();
			}
		}


		public override void Dispose()
		{
			base.Dispose();

			Running = false;

			StopBroadcastListen();

			foreach (TcpClientContext client in ConnectedClients.Values)
			{
				client.Dispose();
			}
			ConnectedClients = null;

			TcpListener listener = TCPSocket;
			TCPSocket = null;
			listener.Stop();

		}

		private void StartTcpListener()
		{
			TCPListenerThread = new Thread(new ThreadStart(TCPListener));
			TCPListenerThread.IsBackground = true;
			TCPListenerThread.Name = "TCP listener";
			TCPListenerThread.Start();
		}

		private void TCPListener()
		{
			TCPSocket = null;

			try
			{
				TCPSocket = new TcpListener(IPAddress.Any, Options.ServerPort);
				//TCPSocket.AllowNatTraversal(true);
				TCPSocket.Start();

				ImpunityLogger.LogInformation("Server TCP Socket listener started");

				while (TCPSocket != null)
				{
					TcpClient client = TCPSocket.AcceptTcpClient();

					TcpClientContext context = new TcpClientContext(this, client);
					ConnectedClients.TryAdd(client.Client.RemoteEndPoint, context);
					ImpunityLogger.LogInformation("Client connected");

					context.Read();
				}


			}
			catch (SocketException e)
			{
				if (!Running)
				{
					ImpunityLogger.LogInformation("Server TCP Socket listener closed");
					return;
				}

				ImpunityLogger.LogError(e, "TCP Socket error:");
			}
			finally
			{
				if (TCPSocket != null)
				{
					TCPSocket.Stop();
					TCPSocket = null;
				}
			}
		}

		internal void OnSocketClosed(TcpClientContext context)
		{
			TcpClientContext removedContext;
			ConnectedClients.TryRemove(context.RemoteEndpoint, out removedContext);
		}

		private void StartBroadcastListen()
		{
			UDPListenerThread = new Thread(new ThreadStart(UDPListener));
			UDPListenerThread.IsBackground = true;
			UDPListenerThread.Name = "UDP server";
			UDPListenerThread.Start();
		}

		private void StopBroadcastListen()
		{
			if (ServerUdpSocket == null)
			{
				return;
			}

			UdpClient socket = ServerUdpSocket;
			ServerUdpSocket = null;
			socket.Close();

		}

		private void UDPListener()
		{
			ServerUdpSocket = null;

			try
			{
				ServerUdpSocket = new UdpClient(Options.ServerPort);
				ServerUdpSocket.EnableBroadcast = true;
				//ServerUdpSocket.AllowNatTraversal(true);

				ImpunityLogger.LogInformation("Server UDP Socket listener started");

				SendServerAnnounce();

				IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, Options.ServerPort);

				while (ServerUdpSocket != null)
				{
					byte[] packet = ServerUdpSocket.Receive(ref groupEP);
					ImpunityLogger.LogInformation("Got bytes");
					if (ImpunityNetworkingUtil.StartsWith(packet, SearchPacket))
					{
						OnSearchPacket();
					}
				}
			}
			catch (SocketException e)
			{
				if (ServerUdpSocket == null)
				{
					ImpunityLogger.LogInformation("Server UDP Socket listener closed");
					return;
				}

				ImpunityLogger.LogError(e, "UDP Socket error:");
			}
			finally
			{
				if (ServerUdpSocket != null)
				{
					ServerUdpSocket.Dispose();
					ServerUdpSocket = null;
				}
			}

		}

		private void OnSearchPacket()
		{
			ImpunityLogger.LogDebug("Got search packet");
			SendServerAnnounce();
		}

		private void SendServerAnnounce()
		{
			ImpunityLogger.LogDebug("Sent server announce");
			IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Any, Options.ClientPort);

			AnnouncePacket = ImpunityNetworkingUtil.WriteBroadcastPacket(AnnouncePacket.Array, ImpunityConstants.ServerAnnouncePacketHeader + Options.GameTypeCode + ":", GameState.GetSummary());

			ServerUdpSocket.SendAsync(AnnouncePacket.Array, AnnouncePacket.Count, broadcastEp);
		}
	}

}