using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;

using UltraLiteDB;
using Impunity.GameState;

namespace Impunity.Networking
{

	public class ImpunityTCPServerClientContext : IImpunityNetworkServerClientContext
	{
		public ImpunityServerMessageHandler OnMessageRecieved { get; set; }
		public ImpunityServerErrorCallback OnNetworkError { get; set; }
		public ImpunityServerClientContextCallback OnClientDisconnected { get; set; }
		public object ClientInfo { get; set; }
		private const int ConnectEstablishTimeout = 1000;

		ImpunityTCPServer Server;
		TcpClient Client;
		NetworkStream ClientStream;

		int MaxBytesToReceive = ImpunityConstants.MaxMessageSize;
		byte[] ReceiveBuffer;
		int BytesReceived = 0;

		public EndPoint RemoteEndpoint { get; private set; }


		public ImpunityTCPServerClientContext(ImpunityTCPServer server, TcpClient client)
		{
			Server = server;
			Client = client;
			ClientStream = client.GetStream();
			ReceiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
			RemoteEndpoint = Client.Client.RemoteEndPoint;
		}

		public bool SupportsUnguaranteed()
		{
			return false;
		}

		public string GetAddress()
		{
			return RemoteEndpoint.ToString();
		}


		public void Listen()
		{
			CancellationTokenSource timeoutSource = new CancellationTokenSource();
			timeoutSource.CancelAfter(ConnectEstablishTimeout);

			ClientStream.ReadAsync(ReceiveBuffer, 0, ImpunityConstants.MaxMessageSize, timeoutSource.Token)
				.ContinueWith( t => {
					timeoutSource.Dispose();
					OnDataRead(t);
				});
		}


		// On socket thread
		private void OnDataRead(Task<int> readTask)
		{
			if (readTask.IsCanceled)
			{
				ImpunityLogger.LogWarning("Closed connection because it took too long to send establish");
				Disconnect();
				return;
			}

			if (!readTask.IsCompletedSuccessfully)
			{
				if (Client == null)
				{
					// Socket was closed by server, causing ReadAsync to throw an exception
					return;
				}

				ImpunityLogger.LogError("Error reading socket", readTask.Exception);

				try
				{
					OnNetworkError?.Invoke(this, new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, readTask.Exception));
				}
				catch(Exception e)
				{
					ImpunityLogger.LogError("Exception in TCP socket error handler", e);
				}

				return;
			}

			int bytesRead = readTask.Result;

			if (bytesRead > 0)
			{
				HandleDataRead(bytesRead);
			}

			if (!ClientStream.CanRead || !Client.Connected)
			{
				if (Client == null)
				{
					return;
				}

				Disconnect();
				return;
			}

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
			try
			{
				OnMessageRecieved?.Invoke(this, new ArraySegment<byte>(ReceiveBuffer, 0, messageLength));
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in TCP socket message handler", e);
			}
		}

		public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			return ClientStream.WriteAsync(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
		}

		public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			return ClientStream.WriteAsync(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
		}


		public void Disconnect()
		{
			try
			{
				TcpClient client = Client;
				Client = null;
				client?.Close();
				client?.Dispose();
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError("Exception closing TCP socket", e);
			}

			Server.ClientDisconnected(this);

			try
			{
				OnClientDisconnected?.Invoke(this);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in TCP socket disconnect handler", e);
			}
		}

		public void Dispose()
		{
			Disconnect();
		}

	}

	class PerGameTCPServerData
	{
		public string GameTypeCode;
		public string GameId;
		public int GameStateFormatVersion;
		public string GameStateFormatChecksum;
		public BsonDocument CurrGameSummary = null;
		public bool PasswordProtected;

		public ArraySegment<byte> AnnouncePacket;

		public PerGameTCPServerData Clone()
		{
			PerGameTCPServerData copy = new PerGameTCPServerData();
			copy.GameTypeCode = this.GameTypeCode;
			copy.GameId = GameId;
			copy.GameStateFormatVersion = GameStateFormatVersion;
			copy.GameStateFormatChecksum = GameStateFormatChecksum;
			copy.CurrGameSummary = CurrGameSummary;
			copy.PasswordProtected = PasswordProtected;
			copy.AnnouncePacket = new ArraySegment<byte>(new byte[ImpunityConstants.MaxMessageSize]);

			return copy;
		}
	}

	public class ImpunityTCPServer : IImpunityNetworkServer
	{
		public ImpunityServerClientContextCallback OnClientConnected { get; set; }

		ImpunityOptions Options;

		Thread TCPListenerThread;
		TcpListener TCPSocket;

		Thread UDPListenerThread;
		UdpClient ServerUdpSocket;
		CancellationTokenSource ShutdownToken;

		bool Running;
		byte[] SearchPacket;

		ConcurrentDictionary<EndPoint, ImpunityTCPServerClientContext> Clients;
		Dictionary<string, PerGameTCPServerData> PerGameData;

		public IPEndPoint ServerEndpoint { get { return TCPSocket?.LocalEndpoint as IPEndPoint; } }

		public ImpunityTCPServer(ImpunityOptions options)
		{
			Options = options;

			PerGameData = new Dictionary<string, PerGameTCPServerData>();
			Clients = new ConcurrentDictionary<EndPoint, ImpunityTCPServerClientContext>();
			ShutdownToken = new CancellationTokenSource();

			Running = true;
		}

		public void AddGameServer(GameStateServer game)
		{
            PerGameTCPServerData tcpGameData = new PerGameTCPServerData
            {
                GameTypeCode = Options.GameTypeCode,
                GameId = game.GameId,
                AnnouncePacket = new ArraySegment<byte>(new byte[ImpunityConstants.MaxMessageSize]),
                CurrGameSummary = game.GetGameSummary(),
                PasswordProtected = game.GamePasswordHash != null
            };

            GameMetadata md = game.GetGameMetadata();
			if (md != null)
			{
				tcpGameData.GameStateFormatVersion = md.Version;
				tcpGameData.GameStateFormatChecksum = md.DataFormatChecksum;
			}

			MakeAnnouncePacket(tcpGameData);

			PerGameData.Add(game.GameId, tcpGameData);
		}

		// Called on Live thread
		public void SetGameSummary(string gameId, BsonDocument summary)
		{
			// Make copy so we can edit it without it being accessed by another thread
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Clone();
			tcpGameData.CurrGameSummary = summary;
			MakeAnnouncePacket(tcpGameData);
			PerGameData[gameId] = tcpGameData;
		}

		// Called on Live thread
		public void SetGameStateFormat(string gameId, int version, string dataChecksum)
		{
			// Make copy so we can edit it without it being accessed by another thread
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Clone();
			tcpGameData.GameStateFormatVersion = version;
			tcpGameData.GameStateFormatChecksum = dataChecksum;

			MakeAnnouncePacket(tcpGameData);
			PerGameData[gameId] = tcpGameData;
		}

		private void MakeAnnouncePacket(PerGameTCPServerData tcpGameData)
		{
			BsonDocument body = new BsonDocument();
			body["gid"] = tcpGameData.GameId;
			body["fv"] = tcpGameData.GameStateFormatVersion;
			body["cs"] = tcpGameData.GameStateFormatChecksum;
			body["s"] = tcpGameData.CurrGameSummary;
			body["p"] = tcpGameData.PasswordProtected;

			tcpGameData.AnnouncePacket = ImpunityNetworkingUtil.MakeBroadcastPacket(tcpGameData.AnnouncePacket.Array,
				ImpunityConstants.ServerAnnouncePacketHeader + tcpGameData.GameTypeCode + ":", body);
		}

		public IEnumerable<IImpunityNetworkServerClientContext> ConnectedClients()
		{
			return Clients.Values;
		}

		public IPEndPoint Listen()
		{
			TCPSocket = new TcpListener(IPAddress.Any, Options.ServerPort);
			//TCPSocket.AllowNatTraversal(true);
			TCPSocket.Start();

			StartTcpListener();

			SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");

			StartUDPListen();
			
			return new IPEndPoint(IPAddress.Loopback, Options.ServerPort);
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

			try
			{
				ImpunityLogger.LogInformation("Server TCP Socket listener started");

				while (TCPSocket != null)
				{
					TcpClient client = TCPSocket.AcceptTcpClient();

					ImpunityTCPServerClientContext context = new ImpunityTCPServerClientContext(this, client);
					Clients[context.RemoteEndpoint] = context;
					ImpunityLogger.LogInformation("Client connected");

					context.Listen();

					try
					{
						OnClientConnected?.Invoke(context);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in TCP client connected callback", e);
					}
				}


			}
			catch (SocketException e)
			{
				if (!Running)
				{
					ImpunityLogger.LogInformation("Server TCP Socket listener closed");
					return;
				}

				ImpunityLogger.LogError("TCP Socket error:", e);
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

		internal void ClientDisconnected(ImpunityTCPServerClientContext context)
		{
			if (!Clients.TryRemove(context.RemoteEndpoint, out _))
			{
				ImpunityLogger.LogError("Got a client disconnect for a client we haven't heard about: " + context.RemoteEndpoint.ToString());
				return;
			}

		}

		private void StartUDPListen()
		{
			UDPListenerThread = new Thread(new ThreadStart(UDPListener));
			UDPListenerThread.IsBackground = true;
			UDPListenerThread.Name = "UDP server";
			UDPListenerThread.Start();
		}

		private void StopUDPListen()
		{
			if (ServerUdpSocket == null)
			{
				return;
			}

			UdpClient socket = ServerUdpSocket;
			ServerUdpSocket = null;
			socket.Close();

			UDPListenerThread.Join();
			UDPListenerThread = null;
		}

		private async void UDPListener()
		{
			ServerUdpSocket = null;

			try
			{
				ServerUdpSocket = new UdpClient(Options.ServerPort);
				ServerUdpSocket.EnableBroadcast = true;
				//ServerUdpSocket.AllowNatTraversal(true); // Breaks things, not sure why

				ImpunityLogger.LogInformation("Server UDP Socket listener started");

				SendServerAnnounce();

				IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, Options.ServerPort);

				while (ServerUdpSocket != null)
				{
					var receiveTask = await ServerUdpSocket.ReceiveAsync();
					byte[] packet = receiveTask.Buffer;
					ImpunityLogger.LogInformation("Got bytes");
					if (ImpunityUtil.StartsWith(packet, SearchPacket))
					{
						OnSearchPacket();
					}
				}
			}
			catch (SocketException e)
			{
				ImpunityLogger.LogError("UDP Socket error:", e);
			}
			finally
			{
				if (ServerUdpSocket != null)
				{
					ImpunityLogger.LogInformation("Server UDP Socket listener closed");
					ServerUdpSocket.Close();
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
			if (!Options.LANDiscoverable)
			{
				return;
			}

			ImpunityLogger.LogDebug("Sent server announce");
			IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Any, Options.ClientPort);

			foreach(PerGameTCPServerData gameData in PerGameData.Values)
			{
				ServerUdpSocket.SendAsync(gameData.AnnouncePacket.Array, gameData.AnnouncePacket.Count, broadcastEp);
			}
			
		}

		public void Dispose()
		{
			Running = false;
			StopUDPListen();

			foreach (ImpunityTCPServerClientContext client in Clients.Values)
			{
				client.Dispose();
			}
			Clients = null;

			TcpListener listener = TCPSocket;
			TCPSocket = null;
			listener?.Stop();
		}
	}

}