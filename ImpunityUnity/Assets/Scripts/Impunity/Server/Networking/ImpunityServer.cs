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

	public class CloseClientConnectionAction : LocalGameStateAction
	{
		public override bool IsDBOperation() { return false; }

		protected override void DoAction(GameStateServer game)
		{

		}
	}

	internal class ServerSideNetworkConnectionProxy : IServerSideConnectionProxy
	{
		IImpunityNetworkServerClientContext ClientContext;
		ImpunityServer NetworkServer;
		GameStateServer GameServer;

		byte[] SendBuffer;
		Semaphore SendLock;

		public string ConnectionId { get { return "NetworkConnection_" + ClientContext.GetAddress(); } }
		public GameStateReplicant ConnectionReplicant { get; set; }

		public bool IsRemote { get { return true; } }

		public bool SupportsUnguaranteed()
		{
			return ClientContext.SupportsUnguaranteed();
		}

		public ServerSideNetworkConnectionProxy(ImpunityServer server, IImpunityNetworkServerClientContext clientContext)
		{
			NetworkServer = server;
			ClientContext = clientContext;
			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
			SendLock = new Semaphore(1, 1);

			clientContext.OnMessageRecieved = ClientMessageReceived;
			clientContext.OnNetworkError = ClientNetworkError;
			clientContext.OnClientDisconnected = ClientDisconnected;

		}

		// Called on socket thread
		private void ClientMessageReceived(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes)
		{
			MessageStruct msg;

			ImpunityNetworkingUtil.ReadMessage(messageBytes, out msg);

			if (GameServer == null && msg.MessageType != (ushort)ClientActionType.ESTABLISH_CONNECTION)
			{
				// Before a connection is established, only the "Establish Connection" action is allowed
				ImpunityLogger.LogInformation("Connection " + context.GetAddress() + " didn't establish connection before trying to do an action");
				context.Disconnect();
				return;
			}

			Type messageActionClassType = ClientActionFactory.GetActionClassType(msg.MessageType);

			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			GameStateActionBase action = (GameStateActionBase)mapper.ToObject(messageActionClassType, msg.Body);
			action.Origin = this;
			action.ResultsExpected = (msg.Flags & ImpunityMessageFlags.NO_REPLY) == 0;

			if (GameServer == null)
			{
				// Establish connection is only legal action here
				EstablishConnectionAction establish = (EstablishConnectionAction)action;
				GameServer = NetworkServer.GetGameStateServer(establish.GameId);
				if (GameServer == null)
				{
					ImpunityLogger.LogInformation("Connection " + context.GetAddress() + " tried to get invalid game id " + establish.GameId);
					context.Disconnect();
					return;
				}

				if (GameServer.GamePasswordHash != null)
				{
					if (GameServer.GamePasswordHash != establish.PasswordHash)
					{
						ImpunityLogger.LogInformation("Connection " + context.GetAddress() + " tried to get into a game with an invalid password");
						context.Disconnect();
						return;
					}
				}
			}

			GameServer.QueueAction(action);
		}

		// Called on network writer thread
		public void SendMessage(ushort messageType, bool guaranteed, BsonDocument results)
		{
			ArraySegment<byte> encodedMessage;

			// Lock send buffer (or wait for it to be available)
			SendLock.WaitOne();

			encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBuffer, 0, 0, messageType, results);

			if (guaranteed)
			{
				ClientContext.SendGuaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
			}
			else
			{
				ClientContext.SendUnguaranteedMessageAsync(encodedMessage).ContinueWith(OnDataWritten);
			}
			
		}

		public void ProcessCloseConnnection()
		{
			ClientContext.Disconnect();
		}

		// Called on TCP socket thread
		private void OnDataWritten(Task writeTask)
		{
			if (!writeTask.IsCompletedSuccessfully)
			{
				ImpunityLogger.LogError("Error writing to socket: " + writeTask.Exception?.Message);
				writeTask.Dispose();

				// Close socket or something?

				return;
			}

			writeTask.Dispose();

			// Unlock send buffer
			SendLock.Release();
		}

		// Called on server thread
		public void ReportActionResult(GameStateActionBase action)
		{
			if (!action.ResultsExpected)
			{
				return;
			}

			// Don't send on server thread, queue for send on network writer thread
			NetworkServer.QueueNetworkAction(action);
		}

		// Called on server thread
		public void SendMessageToClient(ServerActionBase message)
		{
			// Don't send on server thread, queue for send on network writer thread
			message.Origin = this;
			NetworkServer.QueueNetworkAction(message);
		}

		// Called on server thread
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
			GameServer?.ConnectionClosed(this);
		}
	}

	public class ImpunityServer : IDisposable, IGameStateListener
	{
		IImpunityNetworkServer NetworkServer;
		public ImpunityOptions Options { get; private set; }

		Dictionary<string, GameStateServer> GameServers;

		BlockingCollection<GameStateActionBase> PendingWrite;

		Thread NetworkWriterThread;
		bool Running;

		public IPEndPoint TCPEndpoint { get; private set; }


		public ImpunityServer(IEnumerable<GameStateServer> gameStates, IImpunityNetworkServer networkServer, ImpunityOptions options)
		{
			GameServers = new Dictionary<string, GameStateServer>();

			Options = options;

			NetworkServer = networkServer;
			NetworkServer.OnClientConnected = ClientConnected;
			
			PendingWrite = new BlockingCollection<GameStateActionBase>();

			foreach(GameStateServer game in gameStates)
			{
				GameServers.Add(game.GameId, game);

				NetworkServer.AddGameServer(game);

				game.AddListener(this);
			}
			
		}

		public static ImpunityServer MakeTCPServer(GameStateServer gameState, ImpunityOptions options = null)
		{
			return MakeTCPServer(new GameStateServer[] { gameState }, options);
		}

		public static ImpunityServer MakeTCPServer(IEnumerable<GameStateServer> gameStates, ImpunityOptions options = null)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}

			ImpunityTCPServer tcpserver = new ImpunityTCPServer(options);
			ImpunityServer server = new ImpunityServer(gameStates, tcpserver, options);

			return server;
		}

		// Called on live thread
		public void OnGameMetadataChanged(GameStateServer game)
		{
			GameMetadata md = game.GetGameMetadata();
			if (md != null)
			{
				NetworkServer.SetGameStateFormat(game.GameId, md.Version, md.DataFormatChecksum);
			}
		}

		// Called on Live thread
		public void OnGameSummaryChanged(GameStateServer game)
		{
			NetworkServer.SetGameSummary(game.GameId, game.GetGameSummary());
		}

		public GameStateServer GetGameStateServer(string gameId)
		{
			if (gameId == null && GameServers.Count == 1)
			{
				// if there's only a single game, return it
				using (var enumerator = GameServers.Values.GetEnumerator())
				{
					enumerator.MoveNext();
					return enumerator.Current;
				}
			}

			return GameServers.GetValueOrDefault(gameId);
		}


		public void Start()
		{
			Running = true;
			NetworkWriterThread = new Thread(new ThreadStart(WriterThreadMain));
			NetworkWriterThread.IsBackground = false;
			NetworkWriterThread.Name = "Network write";
			NetworkWriterThread.Start();

			TCPEndpoint = NetworkServer.Listen();
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
					ImpunityLogger.LogError(e, "Exception sending action result over network");
				}
				
			}

			PendingWrite.Dispose();
			PendingWrite = null;
		}

		private void SendActionResults(GameStateActionBase action)
		{
			ServerSideNetworkConnectionProxy clientInfo = (ServerSideNetworkConnectionProxy)action.Origin;

			if (action is CloseClientConnectionAction)
			{
				clientInfo.ProcessCloseConnnection();
			}
			else if (action is ServerActionBase)
			{
				// Server originated message
				BsonDocument message = action.SerializeRequest();
				clientInfo.SendMessage(action.GetActionType(), ((ServerActionBase)action).Guaranteed, message);
			}
			else
			{
				// Reply to client action
				BsonDocument results = action.SerializeResults();
				clientInfo.SendMessage((ushort)ServerActionType.CLIENT_REPLY, true, results);
			}
			
		}

		// Called on socket thread
		private void ClientConnected(IImpunityNetworkServerClientContext client)
		{
			ServerSideNetworkConnectionProxy proxy = new ServerSideNetworkConnectionProxy(this, client);
			client.ClientInfo = proxy;
		}


		// called on game server thread
		internal void QueueNetworkAction(GameStateActionBase action)
		{
			PendingWrite.Add(action);
		}

		

		public void Dispose()
		{
			NetworkServer?.Dispose();
			NetworkServer = null;

			Running = false;
			PendingWrite?.CompleteAdding();
			NetworkWriterThread?.Join();
			NetworkWriterThread = null;

			foreach(GameStateServer game in GameServers.Values)
			{
				game.RemoveListener(this);
			}
			GameServers.Clear();

		}
	}

}

