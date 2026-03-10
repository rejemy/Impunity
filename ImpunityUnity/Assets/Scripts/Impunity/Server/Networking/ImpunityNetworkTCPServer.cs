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
using System.Linq;

namespace Impunity.Networking
{

	public class ImpunityTCPServerClientContext : IImpunityNetworkServerClientContext
	{
		public ImpunityServerMessageHandler OnMessageRecieved { get; set; }
		public ImpunityServerErrorCallback OnNetworkError { get; set; }
		public ImpunityServerClientContextCallback OnClientDisconnected { get; set; }

		public string ConnectionId { get; private set;}
		public string RemoteAddress { get => RemoteEndpoint.ToString(); }

		public bool SupportsUnguaranteed { get; set; } = false;

		private const int ConnectEstablishTimeout = 1000;

		ImpunityTCPServer Server;
		TcpClient Client;
		NetworkStream ClientStream;

		byte[] ReceiveBuffer;
		int BytesReceived = 0;

		internal IPEndPoint RemoteEndpoint { get; private set; }


		public ImpunityTCPServerClientContext(ImpunityTCPServer server, TcpClient client, string connectionId)
		{
			Server = server;
			Client = client;
			ClientStream = client.GetStream();
			ReceiveBuffer = new byte[ImpunityConstants.MaxMessageSize];
			RemoteEndpoint = Client.Client.RemoteEndPoint as IPEndPoint;
			ConnectionId = connectionId;
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

			BytesReceived += readTask.Result;

			if (BytesReceived > 0)
			{
				HandleDataRead();
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
			ClientStream.ReadAsync(ReceiveBuffer, BytesReceived, ReceiveBuffer.Length - BytesReceived)
				.ContinueWith(OnDataRead);

		}

		private void HandleDataRead()
		{
			if (BytesReceived < 4)
			{
				// Didn't read enough to get a size, this is probably some kind of error
				ImpunityLogger.LogWarning("Only read " + BytesReceived + " from the TCP socket");
				return;
			}

			int messageLength = ImpunityNetworkingUtil.GetMessageLength(ReceiveBuffer);
			if (BytesReceived < messageLength)
			{
				ImpunityLogger.LogDebug("Got partial message: " + BytesReceived + " / " + messageLength);
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

			if (BytesReceived > messageLength)
			{
				// If there is any data left over, compact it down and continue reading
				int extraData = BytesReceived - messageLength;
				ImpunityLogger.LogDebug("Got part of next message: " + extraData);
				Buffer.BlockCopy(ReceiveBuffer, messageLength, ReceiveBuffer, 0, extraData);
				BytesReceived = extraData;
			}
			else
			{
				BytesReceived = 0;
			}
		}

		public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			return ClientStream.WriteAsync(messageBytes.Array, messageBytes.Offset, messageBytes.Count);
		}

		public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			if (!SupportsUnguaranteed)
			{
				return SendGuaranteedMessageAsync(messageBytes);
			}

			return this.Server.SendUdpSessionData(this.RemoteEndpoint, messageBytes);
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

	public class ImpunityTCPServer
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
		byte[] SessionDataPacket;
		byte[] PingPacket;
		byte[] PongPacket;

		public int ClientsConnected { get => ClientsByRemoteEndpoint.Count; }
		ConcurrentDictionary<IPEndPoint, ImpunityTCPServerClientContext> ClientsByRemoteEndpoint;
		Dictionary<string, PerGameTCPServerData> PerGameData;

		public IPEndPoint ServerEndpoint { get { return TCPSocket?.LocalEndpoint as IPEndPoint; } }

		public ImpunityTCPServer(ImpunityOptions options)
		{
			Options = options;

			PerGameData = new Dictionary<string, PerGameTCPServerData>();
			ClientsByRemoteEndpoint = new ConcurrentDictionary<IPEndPoint, ImpunityTCPServerClientContext>();
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

		public void UpdateAnnouncePacket(string gameId)
		{
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Clone();

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
			body["mc"] = Options.MaxConnections;
			body["cc"] = ClientsConnected <= Options.MaxConnections ? ClientsConnected : Options.MaxConnections;

			tcpGameData.AnnouncePacket = ImpunityNetworkingUtil.MakeBroadcastPacket(tcpGameData.AnnouncePacket.Array,
				ImpunityConstants.ServerAnnouncePacketHeader + tcpGameData.GameTypeCode + ":", body);
		}


		public IPEndPoint Listen()
		{
			TCPSocket = new TcpListener(IPAddress.Any, Options.ServerPort);
			//TCPSocket.AllowNatTraversal(true);
			TCPSocket.Start();

			StartTcpListener();

			SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");
			SessionDataPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSessionDataPacketHeader + Options.GameTypeCode + ":");
			PingPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPingPacketHeader + Options.GameTypeCode + ":");
			PongPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPongPacketHeader + Options.GameTypeCode + ":");

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

					string connectionId = "tcp_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
					ImpunityTCPServerClientContext context = new ImpunityTCPServerClientContext(this, client, connectionId);
					
					ImpunityLogger.LogInformation("Client connected");
			
					try
					{
						OnClientConnected?.Invoke(context);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in TCP client connected callback", e);
						context.Disconnect();
					}

					ClientsByRemoteEndpoint[context.RemoteEndpoint] = context;
					context.Listen();

					// See if we can communicate via UDP with new session
					SendUdpPing(context.RemoteEndpoint);

					// Re-announce after new session started so connected client count gets updated
					foreach(string gameId in PerGameData.Keys.ToList())
					{
						UpdateAnnouncePacket(gameId);
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
			if (!ClientsByRemoteEndpoint.TryRemove(context.RemoteEndpoint, out _))
			{
				ImpunityLogger.LogError("Got a client disconnect for a client we haven't heard about: " + context.RemoteEndpoint.ToString());
				return;
			}

			foreach(string gameId in PerGameData.Keys.ToList())
			{
				UpdateAnnouncePacket(gameId);
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

				while (ServerUdpSocket != null)
				{
					IPEndPoint senderEndpoint = null;
					byte[] packet = ServerUdpSocket.Receive(ref senderEndpoint);
					//ImpunityLogger.LogInformation("Got bytes");
					if (ImpunityUtil.StartsWith(packet, SessionDataPacket))
					{
						var packetBody = new ArraySegment<byte>(packet, SessionDataPacket.Length, packet.Length - SessionDataPacket.Length);
						OnSessionDataPacket(senderEndpoint, packetBody);
					}
					else if(ImpunityUtil.StartsWith(packet, PingPacket))
					{
						OnPingPacket(senderEndpoint);
					}
					else if(ImpunityUtil.StartsWith(packet, PongPacket))
					{
						OnPongPacket(senderEndpoint);
					}
					else if (ImpunityUtil.StartsWith(packet, SearchPacket))
					{
						OnSearchPacket();
					}
					else
					{
						ImpunityLogger.LogDebug("Got unknown UDP packet");
					}
				}
			}
			catch (SocketException e)
			{
				if (ServerUdpSocket != null)
				{
					ImpunityLogger.LogError("UDP Socket error:", e);
				}
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

		private void OnSessionDataPacket(IPEndPoint sender, ArraySegment<byte> data)
		{
			if(this.ClientsByRemoteEndpoint.TryGetValue(sender, out var context))
			{
				try
				{
					context.OnMessageRecieved?.Invoke(context, data);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in UDP socket message handler", e);
				}
			}
			else
			{
				ImpunityLogger.LogDebug("Got UDP session packet from unknown source: " + sender.ToString());
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
			IPEndPoint broadcastEp = new IPEndPoint(IPAddress.Broadcast, Options.ClientPort);

			foreach(PerGameTCPServerData gameData in PerGameData.Values)
			{
				ServerUdpSocket.Send(gameData.AnnouncePacket.Array, gameData.AnnouncePacket.Count, broadcastEp);
			}
			
		}

		private void OnPingPacket(IPEndPoint sender)
		{
			if(this.ClientsByRemoteEndpoint.TryGetValue(sender, out var context))
			{
				try
				{
					ServerUdpSocket.Send(PongPacket, PongPacket.Length, sender);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception sending UDP packet", e);
				}
			}
			else
			{
				ImpunityLogger.LogDebug("Got UDP ping packet from unknown source: " + sender.ToString());
			}
		}
		
		private void OnPongPacket(IPEndPoint sender)
		{
			if(this.ClientsByRemoteEndpoint.TryGetValue(sender, out var context))
			{
				context.SupportsUnguaranteed = true;
			}
			else
			{
				ImpunityLogger.LogDebug("Got UDP pong packet from unknown source: " + sender.ToString());
			}
		}

		private void SendUdpPing(IPEndPoint destination)
		{
			try
			{
				ServerUdpSocket.Send(PingPacket, PingPacket.Length, destination);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception sending UDP packet", e);
			}
		}

		public Task SendUdpSessionData(IPEndPoint destination, ArraySegment<byte> messageBytes)
		{
			byte[] buffer = new byte[messageBytes.Count + SessionDataPacket.Length];
			Buffer.BlockCopy(SessionDataPacket, 0, buffer, 0, SessionDataPacket.Length);
			Buffer.BlockCopy(messageBytes.Array, messageBytes.Offset, buffer, SessionDataPacket.Length, messageBytes.Count);

			return ServerUdpSocket.SendAsync(buffer, buffer.Length, destination);
		}

		public void Dispose()
		{
			Running = false;
			StopUDPListen();

			foreach (ImpunityTCPServerClientContext client in ClientsByRemoteEndpoint.Values)
			{
				client.Dispose();
			}
			ClientsByRemoteEndpoint = null;

			TcpListener listener = TCPSocket;
			TCPSocket = null;
			listener?.Stop();
		}
	}

}