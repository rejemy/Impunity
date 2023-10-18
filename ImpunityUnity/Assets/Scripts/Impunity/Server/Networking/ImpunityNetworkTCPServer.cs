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

		private static async Task<int> ReadTimeout(int delayMS)
		{
			await Task.Delay(delayMS);
			return -1;
		}

		public void Listen()
		{
			Task.WhenAny(ClientStream.ReadAsync(ReceiveBuffer, 0, ImpunityConstants.MaxMessageSize),
				ReadTimeout(ConnectEstablishTimeout))
				.ContinueWith(OnFirstDataRead);
		}

		private void OnFirstDataRead(Task<Task<int>> completedTask)
		{
			OnDataRead(completedTask.Result);
			completedTask.Dispose();
		}

		// On socket thread
		private void OnDataRead(Task<int> readTask)
		{
			if (!readTask.IsCompletedSuccessfully)
			{
				ImpunityLogger.LogError(readTask.Exception, "Error reading socket");

				try
				{
					OnNetworkError?.Invoke(this, new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, readTask.Exception));
				}
				catch(Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in TCP socket error handler");
				}

				readTask.Dispose();
				return;
			}

			int bytesRead = readTask.Result;
			readTask.Dispose();

			if (bytesRead <= 0 || !Client.Connected)
			{
				if (bytesRead == -1)
				{
					ImpunityLogger.LogWarning("Closed connection because it took too long to send establish");
				}
				Disconnect();

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
			try
			{
				OnMessageRecieved?.Invoke(this, new ArraySegment<byte>(ReceiveBuffer, 0, messageLength));
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError(e, "Exception in TCP socket message handler");
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
				Client?.Close();
				Client?.Dispose();
				Client = null;
			}
			catch(Exception e)
			{
				ImpunityLogger.LogError(e, "Exception closing TCP socket");
			}

			Server.ClientDisconnected(this);

			try
			{
				OnClientDisconnected?.Invoke(this);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError(e, "Exception in TCP socket disconnect handler");
			}
		}

		public void Dispose()
		{
			Disconnect();
		}

	}

	class PerGameTCPServerData
	{
		public string GameId;
		public int GameStateFormatVersion;
		public string GameStateFormatChecksum;
		public BsonDocument CurrGameSummary = null;
		public bool PasswordProtected;

		public ArraySegment<byte> AnnouncePacket;

		public PerGameTCPServerData Copy()
		{
			PerGameTCPServerData copy = new PerGameTCPServerData();
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

		bool Running;
		byte[] SearchPacket;

		ConcurrentDictionary<EndPoint, ImpunityTCPServerClientContext> Clients;
		Dictionary<string, PerGameTCPServerData> PerGameData;

		public IPEndPoint ServerEndpoint { get { return TCPSocket?.LocalEndpoint as IPEndPoint; } }

		public ImpunityTCPServer(ImpunityOptions options = null)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;


			PerGameData = new Dictionary<string, PerGameTCPServerData>();
			Clients = new ConcurrentDictionary<EndPoint, ImpunityTCPServerClientContext>();

			Running = true;
		}

		public void AddGameServer(GameStateServer game)
		{
			PerGameTCPServerData tcpGameData = new PerGameTCPServerData();

			tcpGameData.GameId = game.GameId;
			tcpGameData.AnnouncePacket = new ArraySegment<byte>(new byte[ImpunityConstants.MaxMessageSize]);
			tcpGameData.CurrGameSummary = game.GetGameSummary();
			tcpGameData.PasswordProtected = game.GamePasswordHash != null;

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
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Copy();
			tcpGameData.CurrGameSummary = summary;
			MakeAnnouncePacket(tcpGameData);
			PerGameData[gameId] = tcpGameData;
		}

		// Called on Live thread
		public void SetGameStateFormat(string gameId, int version, string dataChecksum)
		{
			// Make copy so we can edit it without it being accessed by another thread
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Copy();
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
				ImpunityConstants.ServerAnnouncePacketHeader + Options.GameTypeCode + ":", body);
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
						ImpunityLogger.LogError(e, "Exception in TCP client connected callback");
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
			socket.Dispose();

		}

		private void UDPListener()
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
					byte[] packet = ServerUdpSocket.Receive(ref groupEP);
					ImpunityLogger.LogInformation("Got bytes");
					if (ImpunityUtil.StartsWith(packet, SearchPacket))
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