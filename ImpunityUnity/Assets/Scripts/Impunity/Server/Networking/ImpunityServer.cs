using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
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

	/// <summary>Sentinel action queued to the network writer thread to send a keepalive. Stateless (the recipient travels
	/// in the queue entry), so one instance is shared.</summary>
	internal class KeepAliveAction : LocalGameStateAction
	{
		public static readonly KeepAliveAction Instance = new KeepAliveAction();

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

		// Set once the connection is closed (by the peer, a transport error, or a server-requested close). Pushes
		// queued for the writer thread before the close was noticed are then dropped instead of hitting a disposed stream.
		volatile bool Closed;

		// Keepalive bookkeeping (see ImpunityKeepAlive). LastReceived is stamped on the socket thread for every inbound
		// message, keepalives included; LastGuaranteedSend on the writer thread. Only guaranteed sends count as proof
		// the client is hearing from us: UDP can be silently dropped, which would otherwise suppress the keepalives the
		// client needs. KeepAliveQueued keeps the sweep from stacking keepalives behind a backed-up writer queue.
		long LastReceivedTimestamp;
		long LastGuaranteedSendTimestamp;
		int KeepAliveQueued;
		int DroppedForSilence;

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

		/// <inheritdoc/>
		public int ConnectionTimeoutMillis { get => NetworkServer.ConnectionTimeoutMillis; }


		public ServerSideNetworkConnectionProxy(ImpunityServer server, IImpunityNetworkServerClientContext clientContext)
		{
			NetworkServer = server;
			ClientContext = clientContext;
			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
			SendBufferWriter = new ByteWriter(SendBuffer);
			SendLock = new Semaphore(1, 1);

			long now = ImpunityKeepAlive.Now();
			LastReceivedTimestamp = now;
			LastGuaranteedSendTimestamp = now;

			ClientContext.OnMessageRecieved = ClientMessageReceived;
			ClientContext.OnNetworkError = ClientNetworkError;
			ClientContext.OnClientDisconnected = ClientDisconnected;

		}

		// Called on socket thread
		private void ClientMessageReceived(IImpunityNetworkServerClientContext context, ArraySegment<byte> messageBytes)
		{
			MessageHeaderStruct msg;

			Interlocked.Exchange(ref LastReceivedTimestamp, ImpunityKeepAlive.Now());

			int bodyOffset = messageBytes.Offset + ImpunityNetworkingUtil.ReadMessageHeader(messageBytes, out msg);

			if (msg.MessageType == (ushort)ClientActionType.KEEPALIVE)
			{
				// Its only job was to refresh LastReceivedTimestamp. Accepted before the handshake completes too: the
				// client starts its keepalive clock as soon as the transport is up.
				return;
			}

			if (GameServer == null && msg.MessageType != (ushort)ClientActionType.ESTABLISH_CONNECTION)
			{
				// Before a connection is established, only the "Establish Connection" action is allowed
				ImpunityLogger.LogInformation("Connection " + context.RemoteAddress + " didn't establish connection before trying to do an action");
				context.Disconnect();
				return;
			}

			Type messageActionClassType = ClientActionFactory.GetActionClassType(msg.MessageType);

			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			GameStateActionBase action = (GameStateActionBase)mapper.DeserializeFromBytes(messageActionClassType, messageBytes.Array!, bodyOffset);
			action.Origin = this;
			action.ResultsExpected = (msg.Flags & ImpunityMessageFlags.NO_REPLY) == 0;
			// Remember the request's correlation id so the reply can echo it back for id-based matching.
			action.MessageId = msg.MessageId;

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
		/// <param name="messageType">The action/message type id written to the header.</param>
		/// <param name="messageId">Correlation id echoed in the header (the request's id for replies; 0 for server-originated pushes).</param>
		/// <param name="guaranteed">Whether to send reliably (TCP) or over the unguaranteed channel when available.</param>
		/// <param name="results">The object serialized into the message body.</param>
		public void SendMessage(ushort messageType, ushort messageId, bool guaranteed, object results)
		{
			ArraySegment<byte> encodedMessage;

			// Lock send buffer (or wait for it to be available)
			SendLock.WaitOne();
			// Lock is released in the OnDataWritten continuation

			if (Closed)
			{
				SendLock.Release();
				ImpunityLogger.LogDebug("Dropping message type " + messageType + " for closed connection " + ConnectionId);
				return;
			}

			try
			{
				encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBufferWriter, messageId, 0, messageType, results);

				if (guaranteed)
				{
					Interlocked.Exchange(ref LastGuaranteedSendTimestamp, ImpunityKeepAlive.Now());
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
				if (IsClosedConnectionError(e))
				{
					// Lost the race with the close: the stream was disposed after the Closed check above.
					ImpunityLogger.LogDebug("Dropping message type " + messageType + " for closed connection " + ConnectionId + ": " + e.Message);
					return;
				}
				ImpunityLogger.LogError("Exception encoding message: " + e.ToString());
			}
		}

		// True when a send failed only because the connection is (being) closed, which is routine after a disconnect
		// and not worth an error. Encoding failures and other bugs don't match and still log as errors.
		private bool IsClosedConnectionError(Exception e)
		{
			if (e is AggregateException aggregate && aggregate.InnerException != null)
			{
				e = aggregate.GetBaseException();
			}

			if (Closed || e is ObjectDisposedException)
			{
				return true;
			}

			// A peer that vanished mid-write (broken pipe, connection reset) surfaces as an IOException wrapping a
			// SocketException; the reader side notices the same failure and runs the disconnect.
			return e is SocketException || (e is IOException && e.InnerException is SocketException);
		}

		/// <summary>Sends a header-only keepalive. Called on the network writer thread.</summary>
		internal void SendKeepAlive()
		{
			Interlocked.Exchange(ref KeepAliveQueued, 0);
			SendMessage((ushort)ServerActionType.KEEPALIVE, 0, true, null!);
		}

		/// <summary>Queues a keepalive for the writer thread unless one is already waiting there. Called on the keepalive sweep thread.</summary>
		internal void QueueKeepAlive()
		{
			if (Interlocked.CompareExchange(ref KeepAliveQueued, 1, 0) == 0)
			{
				NetworkServer.QueueNetworkAction(this, KeepAliveAction.Instance);
			}
		}

		/// <summary>Milliseconds since anything at all arrived from the client.</summary>
		internal long MillisSinceReceived(long now)
		{
			return ImpunityKeepAlive.MillisSince(ref LastReceivedTimestamp, now);
		}

		/// <summary>Milliseconds since a guaranteed message was last handed to the transport for this client.</summary>
		internal long MillisSinceGuaranteedSend(long now)
		{
			return ImpunityKeepAlive.MillisSince(ref LastGuaranteedSendTimestamp, now);
		}

		/// <summary>
		/// Drops a client that has gone silent for longer than the connection timeout. Returns false if it was already
		/// dropped. Called on the keepalive sweep thread.
		/// </summary>
		/// <remarks>
		/// Deliberately does not take <c>SendLock</c> the way <see cref="ProcessCloseConnection"/> does: a dead peer stops
		/// draining its socket, so a write to it can stay pending (holding the lock) indefinitely. Closing the transport
		/// out from under that write faults it, which releases the lock and unblocks the writer thread. The transport's
		/// disconnect then runs the normal close path, so the game server cleans up exactly as for a clean disconnect.
		/// </remarks>
		internal bool DropForSilence()
		{
			if (Interlocked.Exchange(ref DroppedForSilence, 1) != 0)
			{
				return false;
			}

			Closed = true;
			ClientContext.Disconnect();
			return true;
		}

		/// <summary>Waits for any pending send to complete, then disconnects the client.</summary>
		public void ProcessCloseConnection()
		{
			SendLock.WaitOne();

			if (!Closed)
			{
				Closed = true;
				ClientContext.Disconnect();
			}

			SendLock.Release();
		}

		// Called on TCP socket thread
		private void OnDataWritten(Task writeTask)
		{
			// Unlock send buffer
			SendLock.Release();

			if (!writeTask.IsCompletedSuccessfully)
			{
				Exception error = writeTask.Exception ?? (Exception)new OperationCanceledException();
				if (IsClosedConnectionError(error))
				{
					ImpunityLogger.LogDebug("Write to closed connection " + ConnectionId + " failed: " + error.GetBaseException().Message);
					return;
				}

				ImpunityLogger.LogError("Error writing to socket: ", error);

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
			NetworkServer.QueueNetworkAction(this, action);
		}

		/// <summary>Queues a server-originated message to be sent to the client. Called on the game server thread.</summary>
		/// <remarks>
		/// The recipient is carried alongside the action in the write queue rather than stamped onto the action, because
		/// a single <see cref="ServerActionBase"/> instance is broadcast to every listener of a channel (see
		/// <c>GameStateChannel.SendToListeners</c>). Mutating shared state on the action here would be read back later,
		/// on the writer thread, after the next listener had already overwritten it.
		/// </remarks>
		public void SendMessageToClient(ServerActionBase message)
		{
			// Don't send on server thread, queue for send on network writer thread
			NetworkServer.QueueNetworkAction(this, message);
		}

		/// <summary>Requests the connection be closed by queuing a <see cref="CloseClientConnectionAction"/> to the network writer thread.</summary>
		public void CloseConnectionRequest()
		{
			CloseClientConnectionAction action = new CloseClientConnectionAction();
			NetworkServer.QueueNetworkAction(this, action);
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
			Closed = true;
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

	/// <summary>One queued outbound write: the action to send and the connection to send it to. The recipient is kept
	/// here rather than on the action because one action instance is broadcast to many listeners, and the writer thread
	/// reads the queue entry long after the game server thread enqueued it.</summary>
	internal readonly struct PendingSend
	{
		public readonly ServerSideNetworkConnectionProxy Target;
		public readonly GameStateActionBase Action;

		public PendingSend(ServerSideNetworkConnectionProxy target, GameStateActionBase action)
		{
			Target = target;
			Action = action;
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

		BlockingCollection<PendingSend> PendingWrite;

		Thread? NetworkWriterThread;
		// Read on the game server thread by QueueNetworkAction.
		volatile bool Running;

		/// <summary>The local TCP endpoint the server is listening on.</summary>
		public IPEndPoint TCPEndpoint { get; private set; } = null!;

		/// <summary>Number of currently connected clients.</summary>
		public int NumConnections { get { return ClientsByConnectionId.Count; } }

		/// <summary>The TCP/UDP transport, exposed for tests that need to reach its internal seams.</summary>
		internal ImpunityTCPServer TCPTransport { get { return TCPServer; } }

		/// <summary>
		/// The silence window this server enforces on every remote connection and reports to clients in the handshake:
		/// <see cref="ImpunityOptions.ConnectionTimeoutMillis"/> raised to <see cref="ImpunityKeepAlive.MinTimeoutMillis"/>,
		/// or 0 when keepalives are disabled.
		/// </summary>
		public int ConnectionTimeoutMillis { get; private set; }

		/// <summary>Test seam: when set, the server stops sending keepalives (it still drops silent clients), so a test can
		/// make the server look dead to an idle client.</summary>
		internal volatile bool KeepAlivesSuppressed = false;

		Thread? KeepAliveThread;
		readonly ManualResetEventSlim KeepAliveStop = new ManualResetEventSlim(false);

		public ImpunityServer(GameStateServer gameState, ImpunityOptions options) : this(new List<GameStateServer> { gameState }, options)
		{
		}

		public ImpunityServer(IEnumerable<GameStateServer> gameStates, ImpunityOptions options)
		{
			ClientsByConnectionId = new ConcurrentDictionary<string, ServerSideNetworkConnectionProxy>();
			GameServers = new Dictionary<string, GameStateServer>();

			Options = options ?? new ImpunityOptions();

			ConnectionTimeoutMillis = ImpunityKeepAlive.EffectiveTimeoutMillis(Options);
			if (ConnectionTimeoutMillis > Options.ConnectionTimeoutMillis && Options.ConnectionTimeoutMillis > 0)
			{
				ImpunityLogger.LogWarning("ConnectionTimeoutMillis " + Options.ConnectionTimeoutMillis + " is below the minimum, using " + ConnectionTimeoutMillis);
			}

			TCPServer = new ImpunityTCPServer(Options);
			TCPServer.OnClientConnected = ClientConnected;

			PendingWrite = new BlockingCollection<PendingSend>();

			foreach (GameStateServer game in gameStates)
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

			if (ConnectionTimeoutMillis > 0)
			{
				KeepAliveThread = new Thread(new ThreadStart(KeepAliveThreadMain));
				KeepAliveThread.IsBackground = true;
				KeepAliveThread.Name = "Connection keepalive";
				KeepAliveThread.Start();
			}

			TCPEndpoint = TCPServer.Listen();
		}

		// Sweeps every connection on a short period: drops any client silent for the full timeout, and queues a
		// keepalive to any client we haven't sent to for a third of it. Runs on its own thread rather than the writer's
		// because a write to a dead client can block the writer thread — the very thing the sweep has to be able to undo.
		private void KeepAliveThreadMain()
		{
			int timeout = ConnectionTimeoutMillis;
			int interval = ImpunityKeepAlive.IntervalMillis(timeout);
			// Granularity of detection: a dead client is dropped at most this long after its window expires.
			int sweepPeriod = Math.Max(10, Math.Min(1000, interval / 4));

			while (!ImpunityLifecycle.ShuttingDown)
			{
				if (KeepAliveStop.Wait(sweepPeriod))
				{
					break;
				}

				long now = ImpunityKeepAlive.Now();
				foreach (ServerSideNetworkConnectionProxy proxy in ClientsByConnectionId.Values)
				{
					try
					{
						long silentFor = proxy.MillisSinceReceived(now);
						if (silentFor >= timeout)
						{
							if (proxy.DropForSilence())
							{
								ImpunityLogger.LogWarning("Dropping connection " + proxy.ConnectionId + ": nothing received for " + silentFor + "ms (timeout " + timeout + "ms)");
							}
							continue;
						}

						if (!KeepAlivesSuppressed && proxy.MillisSinceGuaranteedSend(now) >= interval)
						{
							proxy.QueueKeepAlive();
						}
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in connection keepalive sweep for " + proxy.ConnectionId, e);
					}
				}
			}
		}

		private void WriterThreadMain()
		{
			while (Running)
			{
				PendingSend send;

				try
				{
					send = PendingWrite.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					break;
				}

				try
				{
					SendActionResults(send);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception sending action result over network", e);
				}

			}

			PendingWrite.Dispose();
		}

		private void SendActionResults(PendingSend send)
		{
			ServerSideNetworkConnectionProxy clientInfo = send.Target;
			GameStateActionBase action = send.Action;

			if (action is CloseClientConnectionAction)
			{
				clientInfo.ProcessCloseConnection();
			}
			else if (action is KeepAliveAction)
			{
				clientInfo.SendKeepAlive();
			}
			else if (action is ServerActionBase)
			{
				// Server originated push — not a reply, so it carries no correlation id.
				clientInfo.SendMessage(action.GetActionType(), 0, action.Guaranteed, action);
			}
			else
			{
				// Reply to a client action — echo the request's id so the client matches it by id.
				clientInfo.SendMessage((ushort)ServerActionType.CLIENT_REPLY, action.MessageId, action.Guaranteed, action.GetResult());
			}

			if (action.CloseConnectionOnComplete)
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
		internal void QueueNetworkAction(ServerSideNetworkConnectionProxy target, GameStateActionBase action)
		{
			// An in-process game server's live thread can still be finishing an action that relays to remote clients
			// while Dispose closes the write queue. Those sends have nowhere to go, so drop them quietly.
			try
			{
				if (!Running || PendingWrite.IsAddingCompleted)
				{
					ImpunityLogger.LogDebug("Dropping " + action.GetType().Name + " for connection " + target.ConnectionId + ": server shutting down");
					return;
				}

				PendingWrite.Add(new PendingSend(target, action));
			}
			catch (InvalidOperationException e)
			{
				// Lost the race with Dispose: the queue was completed (or, once the writer thread exited, disposed —
				// ObjectDisposedException is an InvalidOperationException) after the check above.
				ImpunityLogger.LogDebug("Dropping " + action.GetType().Name + " for connection " + target.ConnectionId + ": server shutting down: " + e.Message);
			}
		}



		/// <summary>Shuts down the server: disposes TCP, stops the writer thread, and removes game state listeners.</summary>
		public void Dispose()
		{
			KeepAliveStop.Set();
			KeepAliveThread?.Join();
			KeepAliveThread = null;

			TCPServer.Dispose();

			Running = false;
			PendingWrite?.CompleteAdding();
			NetworkWriterThread?.Join();

			foreach (GameStateServer game in GameServers.Values)
			{
				game.RemoveListener(this);
			}
			GameServers.Clear();
			ClientsByConnectionId.Clear();
		}
	}

}

