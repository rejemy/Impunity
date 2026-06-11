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

	/// <summary>Server-side representation of a single TCP client connection. Handles async reading, message framing, and partial message reassembly.</summary>
	public class ImpunityTCPServerClientContext : IImpunityNetworkServerClientContext
	{
		/// <inheritdoc/>
		public ImpunityServerMessageHandler? OnMessageRecieved { get; set; }
		/// <inheritdoc/>
		public ImpunityServerErrorCallback? OnNetworkError { get; set; }
		/// <inheritdoc/>
		public ImpunityServerClientContextCallback? OnClientDisconnected { get; set; }

		/// <inheritdoc/>
		public string ConnectionId { get; private set;}
		/// <inheritdoc/>
		public string RemoteAddress { get => RemoteEndpoint.ToString(); }

		/// <summary>Set to true when the client responds to a UDP ping, indicating UDP delivery is available.</summary>
		public bool SupportsUnguaranteed { get; set; } = false;

		private const int ConnectEstablishTimeout = 1000;

		ImpunityTCPServer Server;
		TcpClient? Client;
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
			RemoteEndpoint = (IPEndPoint)Client.Client.RemoteEndPoint!;
			ConnectionId = connectionId;
		}

		/// <summary>Begins async reading from the client socket. The first read has a timeout to ensure the client sends the connection-establish message promptly.</summary>
		public void Listen()
		{
			CancellationTokenSource timeoutSource = new CancellationTokenSource();
			timeoutSource.CancelAfter(ConnectEstablishTimeout);

			try
			{
				ClientStream.ReadAsync(ReceiveBuffer, 0, ImpunityConstants.MaxMessageSize, timeoutSource.Token)
					.ContinueWith( t => {
						timeoutSource.Dispose();
						OnDataRead(t);
					});
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception reading client socket: ", e);
			}
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

				// Else socket was disconencted by peer
				Disconnect();
				return;
			}

			BytesReceived += readTask.Result;

			if (BytesReceived > 0)
			{
				HandleDataRead();
			}

			if (Client == null || !ClientStream.CanRead || !Client.Connected)
			{
				if (Client == null)
				{
					return;
				}

				Disconnect();
				return;
			}

			try
			{
				// And finish by going back to reading
				ClientStream.ReadAsync(ReceiveBuffer, BytesReceived, ReceiveBuffer.Length - BytesReceived)
					.ContinueWith(OnDataRead);
			}
			catch (Exception)
			{
				// Socket closed on us
				Disconnect();
				return;
			}

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
			if (messageLength < 12 || messageLength >= ImpunityConstants.MaxMessageSize)
			{
				// A length below the 12-byte header or beyond our fixed receive buffer can never
				// be satisfied, so it would stall this connection forever (or spin on zero-length
				// reads once the buffer fills). Drop the connection instead.
				ImpunityLogger.LogWarning("Closing connection, received message with invalid length: " + messageLength);
				Disconnect();
				return;
			}
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

		/// <inheritdoc/>
		public Task SendGuaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			return ClientStream.WriteAsync(messageBytes.Array!, messageBytes.Offset, messageBytes.Count);
		}

		/// <inheritdoc/>
		public Task SendUnguaranteedMessageAsync(ArraySegment<byte> messageBytes)
		{
			if (!SupportsUnguaranteed)
			{
				return SendGuaranteedMessageAsync(messageBytes);
			}

			return this.Server.SendUdpSessionData(this.RemoteEndpoint, messageBytes);
		}


		/// <summary>Closes and disposes the TCP connection, notifies the server, and fires the disconnect callback.</summary>
		public void Disconnect()
		{
			try
			{
				TcpClient? client = Client;
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
		public string GameTypeCode = null!;
		public string GameId = null!;
		public int GameStateFormatVersion;
		public string GameStateFormatChecksum = null!;
		public BsonDocument? CurrGameSummary = null;
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

	/// <summary>TCP/UDP server that listens for client connections, handles LAN discovery broadcasts, and manages per-game announce packets. Each accepted TCP connection creates an <see cref="ImpunityTCPServerClientContext"/>.</summary>
	public class ImpunityTCPServer
	{
		/// <summary>Called on the TCP listener thread when a new client connects, before reading begins.</summary>
		public ImpunityServerClientContextCallback? OnClientConnected { get; set; }

		ImpunityOptions Options;

		Thread? TCPListenerThread;
		TcpListener? TCPSocket;

		Thread? UDPListenerThread;
		UdpClient? ServerUdpSocket;
		CancellationTokenSource ShutdownToken;

		bool Running;
		byte[] SearchPacket;
		byte[] SessionDataPacket;
		byte[] PingPacket;
		byte[] PongPacket;

		/// <summary>Number of currently connected TCP clients.</summary>
		public int ClientsConnected { get => ClientsByRemoteEndpoint.Count; }
		ConcurrentDictionary<IPEndPoint, ImpunityTCPServerClientContext> ClientsByRemoteEndpoint;
		Dictionary<string, PerGameTCPServerData> PerGameData;

		/// <summary>The local endpoint the TCP server is listening on, or null if not started.</summary>
		public IPEndPoint? ServerEndpoint { get { return TCPSocket?.LocalEndpoint as IPEndPoint; } }

		public ImpunityTCPServer(ImpunityOptions options)
		{
			Options = options;

			PerGameData = new Dictionary<string, PerGameTCPServerData>();
			ClientsByRemoteEndpoint = new ConcurrentDictionary<IPEndPoint, ImpunityTCPServerClientContext>();
			ShutdownToken = new CancellationTokenSource();

			SearchPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSearchPacketHeader + Options.GameTypeCode + ":");
			SessionDataPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerSessionDataPacketHeader + Options.GameTypeCode + ":");
			PingPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPingPacketHeader + Options.GameTypeCode + ":");
			PongPacket = Encoding.UTF8.GetBytes(ImpunityConstants.ServerPongPacketHeader + Options.GameTypeCode + ":");

			Running = true;
		}

		/// <summary>Registers a game world with the TCP server so it can be included in LAN discovery announce packets.</summary>
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

		/// <summary>Updates the game summary for a world and rebuilds its announce packet. Called on the live thread.</summary>
		public void SetGameSummary(string gameId, BsonDocument? summary)
		{
			// Make copy so we can edit it without it being accessed by another thread
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Clone();
			tcpGameData.CurrGameSummary = summary;
			MakeAnnouncePacket(tcpGameData);
			PerGameData[gameId] = tcpGameData;
		}

		/// <summary>Updates the game state format version and checksum for a world. Called on the live thread.</summary>
		public void SetGameStateFormat(string gameId, int version, string dataChecksum)
		{
			// Make copy so we can edit it without it being accessed by another thread
			PerGameTCPServerData tcpGameData = PerGameData[gameId].Clone();
			tcpGameData.GameStateFormatVersion = version;
			tcpGameData.GameStateFormatChecksum = dataChecksum;

			MakeAnnouncePacket(tcpGameData);
			PerGameData[gameId] = tcpGameData;
		}

		/// <summary>Rebuilds the UDP announce packet for a game world (e.g., after player count changes).</summary>
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

			tcpGameData.AnnouncePacket = ImpunityNetworkingUtil.MakeBroadcastPacket(tcpGameData.AnnouncePacket.Array!,
				ImpunityConstants.ServerAnnouncePacketHeader + tcpGameData.GameTypeCode + ":", body);
		}


		/// <summary>Starts the TCP listener, UDP listener, and LAN discovery. Returns the loopback endpoint for the TCP server.</summary>
		public IPEndPoint Listen()
		{
			TCPSocket = new TcpListener(IPAddress.Any, Options.ServerPort);
			//TCPSocket.AllowNatTraversal(true);
			TCPSocket.Start();

			StartTcpListener();



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

				while (TCPSocket != null && !ImpunityLifecycle.ShuttingDown)
				{
					if (!TCPSocket.Pending())
					{
						Thread.Sleep(100);
						continue;
					}

					TcpClient client = TCPSocket.AcceptTcpClient();

					if (ClientsConnected >= Options.MaxConnections)
					{
						// Server is full — reject before allocating the per-connection receive buffer
						ImpunityLogger.LogWarning("Rejecting connection, server is full (" + Options.MaxConnections + " max connections)");
						try
						{
							client.Close();
							client.Dispose();
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError("Exception closing rejected connection", e);
						}
						continue;
					}

					string connectionId = "tcp_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
					ImpunityTCPServerClientContext context = new ImpunityTCPServerClientContext(this, client, connectionId);
					ClientsByRemoteEndpoint[context.RemoteEndpoint] = context;

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

					context.Listen();

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
			if (ServerUdpSocket == null || UDPListenerThread == null)
			{
				return;
			}

			UdpClient socket = ServerUdpSocket;
			ServerUdpSocket = null;

			// Send a ping to ourselves to the Udp receive will hang forever
			socket.Send(PingPacket, PingPacket.Length, new IPEndPoint(IPAddress.Loopback, Options.ServerPort));
			
			socket.Close();
			
			UDPListenerThread.Join();
			UDPListenerThread = null;
		}

		private void UDPListener()
		{
			ServerUdpSocket = null;

			ServerUdpSocket = new UdpClient(Options.ServerPort);
			ServerUdpSocket.EnableBroadcast = true;
			//ServerUdpSocket.AllowNatTraversal(true); // Breaks things, not sure why

			ImpunityLogger.LogInformation("Server UDP Socket listener started");

			SendServerAnnounce();

			while (ServerUdpSocket != null && !ImpunityLifecycle.ShuttingDown)
			{
				try
				{
					if (!ServerUdpSocket.Client.Poll(1_000_000, SelectMode.SelectRead))
					{
						continue;
					}

					if (ServerUdpSocket == null)
					{
						break;
					}

					IPEndPoint senderEndpoint = null!;
					byte[] packet = ServerUdpSocket.Receive(ref senderEndpoint);
					
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
				catch (SocketException e)
				{
					if (ServerUdpSocket != null)
					{
						ImpunityLogger.LogError("UDP Socket error:", e);
					}
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Got some other exception in UDP loop:", e);
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
				ServerUdpSocket?.Send(gameData.AnnouncePacket.Array!, gameData.AnnouncePacket.Count, broadcastEp);
			}
			
		}

		private void OnPingPacket(IPEndPoint sender)
		{
			if(this.ClientsByRemoteEndpoint.TryGetValue(sender, out var context))
			{
				try
				{
					// Return the ping
					ServerUdpSocket?.Send(PingPacket, PingPacket.Length, sender);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception sending UDP packet", e);
				}
			}
			else if(sender.Address != IPAddress.Loopback)
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

		/// <summary>Sends a session data packet via UDP to a specific client endpoint. The message is prefixed with the session data header.</summary>
		public Task SendUdpSessionData(IPEndPoint destination, ArraySegment<byte> messageBytes)
		{
			byte[] buffer = new byte[messageBytes.Count + SessionDataPacket.Length];
			Buffer.BlockCopy(SessionDataPacket, 0, buffer, 0, SessionDataPacket.Length);
			Buffer.BlockCopy(messageBytes.Array!, messageBytes.Offset, buffer, SessionDataPacket.Length, messageBytes.Count);

			return ServerUdpSocket!.SendAsync(buffer, buffer.Length, destination);
		}

		/// <summary>Shuts down the server: stops UDP, disconnects all clients, and stops the TCP listener.</summary>
		public void Dispose()
		{
			Running = false;
			StopUDPListen();

			foreach (ImpunityTCPServerClientContext client in ClientsByRemoteEndpoint.Values)
			{
				client.Dispose();
			}

			TcpListener? listener = TCPSocket;
			TCPSocket = null;
			listener?.Stop();
		}
	}

}