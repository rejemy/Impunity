using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using UltraLiteDB;

using Impunity.GameState;
using System.Net;


namespace Impunity.Networking
{

	/// <summary>Sentinel action queued to the network writer thread to close a client connection.</summary>
	public class CloseClientConnectionAction : LocalGameStateAction
	{
		public override bool IsDBOperation() { return false; }

		protected override void DoAction(GameStateServer game)
		{

		}
	}

	/// <summary>Server-side proxy for a remote client connection. Handles message parsing, send serialization (via semaphore), and connection lifecycle. One instance per connected client.</summary>
	public class ServerSideNetworkConnectionProxy : IServerSideConnectionProxy, IDisposable
	{
		IImpunityNetworkServerClientContext ClientContext;
		ImpunityServer NetworkServer;
		GameStateServer? GameServer;

		byte[] SendBuffer;
		ByteWriter SendBufferWriter;
		Semaphore SendLock;

		/// <summary>Server-assigned unique ID for this connection.</summary>
		public string ConnectionId { get => ClientContext.ConnectionId; }
		/// <summary>Client-provided or auto-generated key for reconnection.</summary>
		public string ConnectionKey { get; set; } = null!;
		/// <summary>The replicant state tracker for this connection's subscribed entities.</summary>
		public GameStateReplicant ConnectionReplicant { get; set; } = null!;

		/// <summary>Always true for network connections.</summary>
		public bool IsRemote { get { return true; } }

		/// <summary>Whether the client supports UDP delivery.</summary>
		public bool SupportsUnguaranteed { get => ClientContext.SupportsUnguaranteed; }


		public ServerSideNetworkConnectionProxy(ImpunityServer server, IImpunityNetworkServerClientContext clientContext)
		{
			NetworkServer = server;
			ClientContext = clientContext;
			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
			SendBufferWriter = new ByteWriter(SendBuffer);
			SendLock = new Semaphore(1, 1);

			ClientContext.OnMessageRecieved = ClientMessageReceived;
			ClientContext.OnNetworkError = ClientNetworkError;
			ClientContext.OnClientDisconnected = ClientDisconnected;

		}

		// Called on socket thread
		private void ClientMessageReceived(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes)
		{
			MessageHeaderStruct msg;

			int bodyOffset = messageBytes.Offset + ImpunityNetworkingUtil.ReadMessageHeader(messageBytes, out msg);

			if (GameServer == null && msg.MessageType != (ushort)ClientActionType.ESTABLISH_CONNECTION)
			{
				// Before a connection is established, only the "Establish Connection" action is allowed
				ImpunityLogger.LogInformation("Connection " + context.RemoteAddress + " didn't establish connection before trying to do an action");
				context.Disconnect();
				return;
			}

			Type messageActionClassType = ClientActionFactory.GetActionClassType(msg.MessageType);

			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			GameStateActionBase action = (GameStateActionBase)mapper.DeserializeFromBytes(messageActionClassType, messageBytes.Array, bodyOffset);
			action.Origin = this;
			action.ResultsExpected = (msg.Flags & ImpunityMessageFlags.NO_REPLY) == 0;

			// Special handling for first request. If an error happens here, we report the result inline and disconnect,
			// since the GameServer doesn't even know about this connection yet.
			if (GameServer == null)
			{
				// Establish connection is only legal action here
				if (action is not EstablishConnectionAction)
				{
					ImpunityLogger.LogInformation("Connection " + context.RemoteAddress + " sent " + action.GetActionType() + " on connection");
					action.CloseConnectionOnComplete = true;
					action.Error = new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Failed to establish connection");
					ReportActionResult(action);
					return;
				}

				EstablishConnectionAction establish = (EstablishConnectionAction)action;
				ConnectionKey = establish.ConnectionKey;
				if (ConnectionKey == null)
				{
					ConnectionKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 8);
				}
				
				GameServer = NetworkServer.GetGameStateServer(establish.GameId);
				if (GameServer == null)
				{
					ImpunityLogger.LogInformation("Connection " + context.RemoteAddress + " tried to get invalid game id " + establish.GameId);
					action.CloseConnectionOnComplete = true;
					action.Error = new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Invalid game id");
					ReportActionResult(action);
					return;
				}

				if (GameServer.GamePasswordHash != null)
				{
					if (GameServer.GamePasswordHash != establish.PasswordHash)
					{
						ImpunityLogger.LogInformation("Connection " + context.RemoteAddress + " tried to get into a game with an invalid password");
						action.CloseConnectionOnComplete = true;
						action.Error = new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Invalid password");
						ReportActionResult(action);
						return;
					}
				}
			}

			GameServer.QueueAction(action);
		}

		/// <summary>Serializes and sends a message to the client. Acquires the send lock to serialize access to the shared send buffer. Called on the network writer thread.</summary>
		public void SendMessage(ushort messageType, bool guaranteed, object results)
		{
			ArraySegment<byte> encodedMessage;

			// Lock send buffer (or wait for it to be available)
			SendLock.WaitOne();
			// Lock is released in the OnDataWritten continuation

			try
			{
				encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBufferWriter, 0, 0, messageType, results);

				if (guaranteed)
				{
					ClientContext.SendGuaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
				}
				else
				{
					ClientContext.SendUnguaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
				}
			}
			catch (Exception e)
			{
				SendLock.Release();
				ImpunityLogger.LogError("Exception encoding message: " + e.ToString());
			}
		}

		/// <summary>Waits for any pending send to complete, then disconnects the client.</summary>
		public void ProcessCloseConnection()
		{
			SendLock.WaitOne();

			ClientContext.Disconnect();

			SendLock.Release();
		}

		// Called on TCP socket thread
		private void OnDataWritten(Task writeTask)
		{
			// Unlock send buffer
			SendLock.Release();

			if (!writeTask.IsCompletedSuccessfully)
			{
				ImpunityLogger.LogError("Error writing to socket: ", writeTask.Exception);

				// Close socket or something?

				return;
			}
			
		}

		/// <summary>Queues an action result to be sent back to the client. Called on the game server thread.</summary>
		public void ReportActionResult(GameStateActionBase action)
		{
			if (!action.ResultsExpected)
			{
				return;
			}

			// Don't send on server thread, queue for send on network writer thread
			NetworkServer.QueueNetworkAction(action);
		}

		/// <summary>Queues a server-originated message to be sent to the client. Called on the game server thread.</summary>
		public void SendMessageToClient(ServerActionBase message)
		{
			// Don't send on server thread, queue for send on network writer thread
			message.Origin = this;
			NetworkServer.QueueNetworkAction(message);
		}

		/// <summary>Requests the connection be closed by queuing a <see cref="CloseClientConnectionAction"/> to the network writer thread.</summary>
		public void CloseConnectionRequest()
		{
			CloseClientConnectionAction action = new CloseClientConnectionAction();
			action.Origin = this;
			NetworkServer.QueueNetworkAction(action);
		}

		// Called on socket thread
		private void ClientNetworkError(IImpunityNetworkServerClientContext client, ImpunityErrorResponse err)
		{
			ImpunityLogger.LogError("client network error: " + err.Message);

			// Todo - close connection?
		}

		// Called on socket thread
		private void ClientDisconnected(IImpunityNetworkServerClientContext client)
		{
			NetworkServer.ClientDisconnected(this);
			GameServer?.ConnectionClosed(this);
		}

		public void Dispose()
		{
			if (SendLock != null)
			{
				SendLock.Dispose();
			}

			if (ClientContext != null)
			{
				ClientContext.OnMessageRecieved = null;
				ClientContext.OnNetworkError = null;
				ClientContext.OnClientDisconnected = null;
			}
		}
	}

	/// <summary>Top-level server that manages TCP connections, routes messages between clients and game state servers, and handles the network writer thread for outbound messages.</summary>
	public class ImpunityServer : IDisposable, IGameStateListener
	{
		ImpunityTCPServer TCPServer;
		/// <summary>Server configuration options.</summary>
		public ImpunityOptions Options { get; private set; }

		Dictionary<string, GameStateServer> GameServers;
		ConcurrentDictionary<string, ServerSideNetworkConnectionProxy> ClientsByConnectionId;

		BlockingCollection<GameStateActionBase> PendingWrite;

		Thread? NetworkWriterThread;
		bool Running;

		/// <summary>The local TCP endpoint the server is listening on.</summary>
		public IPEndPoint TCPEndpoint { get; private set; } = null!;

		/// <summary>Number of currently connected clients.</summary>
		public int NumConnections { get {return ClientsByConnectionId.Count; }}

		public ImpunityServer(GameStateServer gameState, ImpunityOptions options) : this(new List<GameStateServer>{gameState}, options)
		{
		}

		public ImpunityServer(IEnumerable<GameStateServer> gameStates, ImpunityOptions options)
		{
			ClientsByConnectionId = new ConcurrentDictionary<string, ServerSideNetworkConnectionProxy>();
			GameServers = new Dictionary<string, GameStateServer>();

			Options = options ?? new ImpunityOptions();

			TCPServer =  new ImpunityTCPServer(Options);
			TCPServer.OnClientConnected = ClientConnected;
			
			PendingWrite = new BlockingCollection<GameStateActionBase>();

			foreach(GameStateServer game in gameStates)
			{
				GameServers.Add(game.GameId, game);

				TCPServer.AddGameServer(game);

				game.AddListener(this);
			}
			
		}

		// Called on live thread
		public void OnGameMetadataChanged(GameStateServer game)
		{
			GameMetadata md = game.GetGameMetadata();
			if (md != null)
			{
				TCPServer.SetGameStateFormat(game.GameId, md.Version, md.DataFormatChecksum);
			}
		}

		// Called on Live thread
		public void OnGameSummaryChanged(GameStateServer game)
		{
			TCPServer.SetGameSummary(game.GameId, game.GetGameSummary());
		}

		/// <summary>Returns the game state server for the given game ID. If gameId is null and there's only one game, returns that one.</summary>
		public GameStateServer? GetGameStateServer(string? gameId)
		{
			if (gameId == null)
			{
				if (GameServers.Count == 1)
				{
					// if there's only a single game, return it
					using (var enumerator = GameServers.Values.GetEnumerator())
					{
						enumerator.MoveNext();
						return enumerator.Current;
					}
				}
				else
				{
					// Don't know which one to pick
					return null;
				}
			}

			return GameServers.GetValueOrDefault(gameId);
		}


		/// <summary>Starts the network writer thread and TCP/UDP listeners.</summary>
		public void Start()
		{
			Running = true;
			NetworkWriterThread = new Thread(new ThreadStart(WriterThreadMain));
			NetworkWriterThread.IsBackground = false;
			NetworkWriterThread.Name = "Network write";
			NetworkWriterThread.Start();

			TCPEndpoint = TCPServer.Listen();
		}

		private void WriterThreadMain()
		{
			while (Running)
			{
				GameStateActionBase action;

				try
				{
					action = PendingWrite.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					break;
				}

				try
				{
					SendActionResults(action);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception sending action result over network", e);
				}

			}

			PendingWrite.Dispose();
		}

		private void SendActionResults(GameStateActionBase action)
		{
			ServerSideNetworkConnectionProxy clientInfo = (ServerSideNetworkConnectionProxy)action.Origin;

			if (action is CloseClientConnectionAction)
			{
				clientInfo.ProcessCloseConnection();
			}
			else if (action is ServerActionBase)
			{
				// Server originated message
				clientInfo.SendMessage(action.GetActionType(), action.Guaranteed, action);
			}
			else
			{
				// Reply to client action
				clientInfo.SendMessage((ushort)ServerActionType.CLIENT_REPLY, action.Guaranteed, action.GetResult());
			}

			if(action.CloseConnectionOnComplete)
			{
				clientInfo.ProcessCloseConnection();
			}
		}

		// Called on socket thread
		public void ClientConnected(IImpunityNetworkServerClientContext client)
		{
			ServerSideNetworkConnectionProxy proxy = new ServerSideNetworkConnectionProxy(this, client);
			ClientsByConnectionId.TryAdd(proxy.ConnectionId, proxy);
		}

		// Called on socket thread
		public void ClientDisconnected(ServerSideNetworkConnectionProxy proxy)
		{
			if (!ClientsByConnectionId.TryRemove(proxy.ConnectionId, out _))
			{
				ImpunityLogger.LogError("Got a client disconnect for a client we haven't heard about: " + proxy.ConnectionId);
				return;
			}
		}

		// called on game server thread
		internal void QueueNetworkAction(GameStateActionBase action)
		{
			PendingWrite.Add(action);
		}

		

		/// <summary>Shuts down the server: disposes TCP, stops the writer thread, and removes game state listeners.</summary>
		public void Dispose()
		{
			TCPServer.Dispose();

			Running = false;
			PendingWrite?.CompleteAdding();
			NetworkWriterThread?.Join();

			foreach(GameStateServer game in GameServers.Values)
			{
				game.RemoveListener(this);
			}
			GameServers.Clear();
			ClientsByConnectionId.Clear();
		}
	}

}

