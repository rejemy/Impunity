using System;
using System.Buffers;
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

	/// <summary>Server-side proxy for a remote client connection. Handles message parsing, the per-connection outbound queue, and connection lifecycle. One instance per connected client.</summary>
	/// <remarks>
	/// <para>Outbound messages are encoded on the shared network writer thread and appended to this connection's own
	/// queue. The writer thread doesn't start a socket write until it has run out of other work (see
	/// <see cref="ImpunityServer"/>), so a burst of pushes reaches the transport as one batched write. While a write is in
	/// flight, its continuation keeps draining the queue in further batches. The writer thread never waits for a
	/// client's socket, so a client that stops reading (dead peer, half-open TCP) can only back up its own queue, never
	/// delay sends to anyone else. The queue is capped at <see cref="ImpunityOptions.MaxSendBacklogBytes"/>; a client
	/// that exceeds it is dropped.</para>
	/// <para>Unguaranteed messages for a client with UDP skip the queue and go out immediately: they have no ordering
	/// promise, and stale movement data shouldn't wait behind a reliable backlog.</para>
	/// </remarks>
	public class ServerSideNetworkConnectionProxy : IServerSideConnectionProxy, IDisposable
	{
		/// <summary>The most bytes gathered into one transport write. Any single message fits (they are all under
		/// <see cref="ImpunityConstants.MaxMessageSize"/>).</summary>
		internal const int MaxBatchBytes = 64 * 1024;

		IImpunityNetworkServerClientContext ClientContext;
		ImpunityServer NetworkServer;
		GameStateServer? GameServer;

		// The outbound queue of encoded messages (each an exact-size array, possibly shared with other recipients of the
		// same push), guarded by OutboundLock. WriteInFlight is true from the moment a write is started until the queue
		// is found empty, so at most one transport write is ever pending: transports are not thread-safe, and
		// one-at-a-time keeps this connection's messages in order. WriteScheduled means the writer thread will start
		// writing when it next runs out of work. BacklogBytes counts queued and in-flight bytes. CloseWhenFlushed is a
		// server-requested close waiting for the queue to drain.
		readonly object OutboundLock = new object();
		readonly Queue<byte[]> Outbound = new Queue<byte[]>();
		bool WriteInFlight;
		bool WriteScheduled;
		bool CloseWhenFlushed;
		long BacklogBytes;

		// Set once the connection is closed (by the peer, a transport error, or a server-requested close). Pushes
		// queued for the writer thread before the close was noticed are then dropped instead of hitting a disposed stream.
		volatile bool Closed;
		// Set by whichever server-side path (requested close, silence, backlog overflow) tears the transport down first.
		int CloseStarted;

		// Keepalive bookkeeping (see ImpunityKeepAlive). LastReceived is stamped on the socket thread for every inbound
		// message, keepalives included; LastGuaranteedSend whenever a reliable write is started. Only reliable sends
		// count as proof the client is hearing from us: UDP can be silently dropped, which would otherwise suppress the
		// keepalives the client needs. KeepAliveQueued keeps the sweep from stacking keepalives behind a backed-up
		// writer queue.
		long LastReceivedTimestamp;
		long LastGuaranteedSendTimestamp;
		int KeepAliveQueued;

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

		/// <summary>Bytes encoded for this client but not yet written to its transport, including any write in progress.</summary>
		internal long SendBacklogBytes
		{
			get
			{
				lock (OutboundLock)
				{
					return BacklogBytes;
				}
			}
		}


		public ServerSideNetworkConnectionProxy(ImpunityServer server, IImpunityNetworkServerClientContext clientContext)
		{
			NetworkServer = server;
			ClientContext = clientContext;

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

		/// <summary>Encodes a message and queues it for this client. Called on the network writer thread.</summary>
		/// <param name="messageType">The action/message type id written to the header.</param>
		/// <param name="messageId">Correlation id echoed in the header (the request's id for replies; 0 for server-originated pushes).</param>
		/// <param name="guaranteed">Whether to send reliably (TCP) or over the unguaranteed channel when available.</param>
		/// <param name="results">The object serialized into the message body.</param>
		public void SendMessage(ushort messageType, ushort messageId, bool guaranteed, object results)
		{
			if (Closed || CloseWhenFlushed)
			{
				ImpunityLogger.LogDebug("Dropping message type " + messageType + " for closed connection " + ConnectionId);
				return;
			}

			byte[]? message = NetworkServer.EncodeMessage(messageType, messageId, results);
			if (message != null)
			{
				QueueMessage(message, guaranteed);
			}
		}

		/// <summary>
		/// Queues an encoded message for this client. Never waits for the client's socket: the write is started when the
		/// writer thread runs out of other work, or by the continuation of a write already in progress. An unguaranteed
		/// message for a client with UDP is sent right away instead. Called on the network writer thread.
		/// </summary>
		/// <param name="message">One complete framed message. Not modified, so it can be shared between recipients.</param>
		/// <param name="guaranteed">Whether the message must be delivered reliably.</param>
		internal void QueueMessage(byte[] message, bool guaranteed)
		{
			if (Closed || CloseWhenFlushed)
			{
				ImpunityLogger.LogDebug("Dropping message for closed connection " + ConnectionId);
				return;
			}

			if (!guaranteed && ClientContext.SupportsUnguaranteed)
			{
				SendUnguaranteedNow(message);
				return;
			}

			long limit = NetworkServer.Options.MaxSendBacklogBytes;
			bool queued = false;
			bool schedule = false;
			long backlog;
			lock (OutboundLock)
			{
				backlog = BacklogBytes;
				// A single message is always accepted into an empty backlog, whatever the limit.
				bool overLimit = limit > 0 && backlog > 0 && backlog + message.Length > limit;
				if (!Closed && !CloseWhenFlushed && !overLimit)
				{
					Outbound.Enqueue(message);
					BacklogBytes += message.Length;
					queued = true;
					if (!WriteInFlight && !WriteScheduled)
					{
						WriteScheduled = true;
						schedule = true;
					}
				}
			}

			if (queued)
			{
				NetworkServer.NoteQueued(message.Length);
				if (schedule)
				{
					NetworkServer.ScheduleWrite(this);
				}
				return;
			}

			if (Closed || CloseWhenFlushed)
			{
				// Lost the race with a close on another thread.
				ImpunityLogger.LogDebug("Dropping message for closed connection " + ConnectionId);
			}
			else if (CloseTransport())
			{
				ImpunityLogger.LogWarning("Dropping connection " + ConnectionId + ": " + backlog + " bytes of outbound messages backed up, over the " + limit + " byte limit (client not reading?)");
			}
		}

		// UDP has no ordering promise to keep and no backlog worth waiting behind. The transport allows this alongside a
		// reliable write in flight (see IImpunityNetworkServerClientContext.SendUnguaranteedMessageAsync).
		private void SendUnguaranteedNow(byte[] message)
		{
			Task send;
			try
			{
				send = ClientContext.SendUnguaranteedMessageAsync(new ArraySegment<byte>(message));
			}
			catch (Exception e)
			{
				send = Task.FromException(e);
			}

			if (send.IsCompleted)
			{
				ReportUnguaranteedFailure(send);
			}
			else
			{
				send.ContinueWith(ReportUnguaranteedFailure, TaskScheduler.Default);
			}
		}

		private void ReportUnguaranteedFailure(Task send)
		{
			if (send.IsCompletedSuccessfully)
			{
				return;
			}

			Exception error = send.Exception ?? (Exception)new OperationCanceledException();
			if (IsClosedConnectionError(error))
			{
				ImpunityLogger.LogDebug("Unguaranteed send to closed connection " + ConnectionId + " failed: " + error.GetBaseException().Message);
				return;
			}
			ImpunityLogger.LogError("Error sending unguaranteed message: ", error);
		}

		/// <summary>Starts writing this connection's queue, if a write isn't already in progress. Called on the network
		/// writer thread once it has drained its own queue (or tests, standing in for it).</summary>
		internal void StartScheduledWrite()
		{
			lock (OutboundLock)
			{
				WriteScheduled = false;
				if (WriteInFlight || Outbound.Count == 0)
				{
					return;
				}
				WriteInFlight = true;
			}

			WriteQueued();
		}

		/// <summary>
		/// Writes queued messages, in batches of up to <see cref="MaxBatchBytes"/>, until the queue is empty or a write
		/// has to wait for the client. Runs on the writer thread to start a write, then on whichever thread completes
		/// each pending write. Only ever runs on one thread at a time for a connection (guarded by <c>WriteInFlight</c>).
		/// </summary>
		private void WriteQueued()
		{
			while (TakeNextBatch(out ArraySegment<byte> batch, out bool pooled))
			{
				Task write;
				try
				{
					Interlocked.Exchange(ref LastGuaranteedSendTimestamp, ImpunityKeepAlive.Now());
					write = ClientContext.SendGuaranteedMessageAsync(batch);
				}
				catch (Exception e)
				{
					// e.g. the stream was disposed out from under us; reported the same way as a failed write.
					write = Task.FromException(e);
				}

				if (!write.IsCompleted)
				{
					// The client's socket buffer is full (or the transport is just slow): pick up where we left off when
					// this write finishes, on the thread that finishes it. The writer thread is free to move on.
					write.ContinueWith(t =>
					{
						OnDataWritten(t, batch, pooled);
						WriteQueued();
					}, TaskScheduler.Default);
					return;
				}

				OnDataWritten(write, batch, pooled);
			}
		}

		// Takes the next batch to write, or returns false and clears WriteInFlight when there is none. A lone message is
		// written from its own array; several are gathered into a pooled buffer. A close request waiting on the flush is
		// carried out here, once the queue is found empty.
		private bool TakeNextBatch(out ArraySegment<byte> batch, out bool pooled)
		{
			lock (OutboundLock)
			{
				if (Closed)
				{
					// Whatever is still queued can never be delivered.
					DiscardBacklog();
					WriteInFlight = false;
					batch = default;
					pooled = false;
					return false;
				}

				if (Outbound.Count > 0)
				{
					byte[] first = Outbound.Dequeue();
					if (Outbound.Count == 0 || first.Length + Outbound.Peek().Length > MaxBatchBytes)
					{
						batch = new ArraySegment<byte>(first);
						pooled = false;
						return true;
					}

					byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxBatchBytes);
					Buffer.BlockCopy(first, 0, buffer, 0, first.Length);
					int length = first.Length;
					while (Outbound.Count > 0 && length + Outbound.Peek().Length <= MaxBatchBytes)
					{
						byte[] next = Outbound.Dequeue();
						Buffer.BlockCopy(next, 0, buffer, length, next.Length);
						length += next.Length;
					}

					batch = new ArraySegment<byte>(buffer, 0, length);
					pooled = true;
					return true;
				}

				WriteInFlight = false;
				batch = default;
				pooled = false;
				if (!CloseWhenFlushed)
				{
					return false;
				}
			}

			// Everything queued before the close request has been written.
			CloseTransport();
			return false;
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

		/// <summary>Milliseconds since a reliable write to this client was last started.</summary>
		internal long MillisSinceGuaranteedSend(long now)
		{
			return ImpunityKeepAlive.MillisSince(ref LastGuaranteedSendTimestamp, now);
		}

		/// <summary>
		/// Drops a client that has gone silent for longer than the connection timeout. Returns false if the connection
		/// was already closed or being closed. Called on the keepalive sweep thread.
		/// </summary>
		/// <remarks>
		/// Doesn't wait for this client's queue to drain the way <see cref="ProcessCloseConnection"/> does: a dead peer
		/// stops draining its socket, so a write to it can stay pending indefinitely. Closing the transport out from under
		/// that write faults it, and the rest of the queue is discarded. The transport's disconnect then runs the normal
		/// close path, so the game server cleans up exactly as for a clean disconnect.
		/// </remarks>
		internal bool DropForSilence()
		{
			return CloseTransport();
		}

		/// <summary>Closes the connection once every message queued before this call has been written. Called on the
		/// network writer thread; returns immediately (the close happens once the queue drains, if anything is still
		/// queued).</summary>
		public void ProcessCloseConnection()
		{
			lock (OutboundLock)
			{
				if (Closed)
				{
					return;
				}

				if (WriteInFlight || Outbound.Count > 0)
				{
					// TakeNextBatch closes the transport when it finds the queue empty (a non-empty queue with no write in
					// flight already has one scheduled). Later sends are dropped.
					CloseWhenFlushed = true;
					return;
				}
			}

			CloseTransport();
		}

		// Tears down the transport from the server side and discards anything still queued. Only the first caller (of a
		// requested close, a silence drop, or a backlog overflow) does anything, and not at all if the peer is already
		// gone. Returns whether this call closed it.
		private bool CloseTransport()
		{
			if (Closed || Interlocked.Exchange(ref CloseStarted, 1) != 0)
			{
				return false;
			}

			MarkClosed();
			ClientContext.Disconnect();
			return true;
		}

		private void MarkClosed()
		{
			Closed = true;
			lock (OutboundLock)
			{
				DiscardBacklog();
			}
		}

		// Caller holds OutboundLock. A write already in flight keeps its bytes until it completes.
		private void DiscardBacklog()
		{
			while (Outbound.Count > 0)
			{
				BacklogBytes -= Outbound.Dequeue().Length;
			}
		}

		// Called on whichever thread completed the write (possibly the writer thread, for a write that finished at once)
		private void OnDataWritten(Task writeTask, ArraySegment<byte> batch, bool pooled)
		{
			lock (OutboundLock)
			{
				BacklogBytes -= batch.Count;
			}
			if (pooled)
			{
				ArrayPool<byte>.Shared.Return(batch.Array!);
			}

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
			MarkClosed();
			NetworkServer.ClientDisconnected(this);
			GameServer?.ConnectionClosed(this);
		}

		public void Dispose()
		{
			MarkClosed();

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

		// Writer-thread state. Every message is encoded into this scratch buffer and copied out to an exact-size array.
		// WritesToStart lists connections that have had messages queued since the writer last ran out of work; their
		// socket writes are started together then, so a burst of messages reaches each client as one batched write.
		readonly byte[] EncodeBuffer = new byte[ImpunityConstants.MaxMessageSize];
		readonly ByteWriter EncodeWriter;
		readonly List<ServerSideNetworkConnectionProxy> WritesToStart = new List<ServerSideNetworkConnectionProxy>();
		long BytesQueuedSinceWriting;

		/// <summary>How many queued sends the writer thread handles before it starts socket writes even though more work
		/// is waiting. Bounds the latency batching can add when the queue never runs dry.</summary>
		internal const int MaxSendsPerBatch = 64;
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
			EncodeWriter = new ByteWriter(EncodeBuffer);

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
		// keepalive to any client we haven't sent to for a third of it. Runs on its own thread so its timing doesn't
		// depend on how busy the writer thread is.
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
			int sendsSinceWriting = 0;

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

				// Hold the socket writes until the queue runs dry, so everything the game threads produced together goes
				// out together. Nothing waits on a socket here, so this only ever delays by the time to encode. Once a
				// batch's worth of bytes is waiting there's nothing more to gain, and holding more would only inflate
				// every client's backlog (which MaxSendBacklogBytes counts) while the writes haven't started.
				if (++sendsSinceWriting >= MaxSendsPerBatch
					|| BytesQueuedSinceWriting >= ServerSideNetworkConnectionProxy.MaxBatchBytes
					|| PendingWrite.Count == 0)
				{
					StartScheduledWrites();
					sendsSinceWriting = 0;
				}
			}

			PendingWrite.Dispose();
		}

		/// <summary>Records that a connection has messages waiting for <see cref="StartScheduledWrites"/>. Called on the
		/// network writer thread.</summary>
		internal void ScheduleWrite(ServerSideNetworkConnectionProxy proxy)
		{
			WritesToStart.Add(proxy);
		}

		/// <summary>Counts bytes queued for any connection since writes were last started. Called on the network writer
		/// thread.</summary>
		internal void NoteQueued(int bytes)
		{
			BytesQueuedSinceWriting += bytes;
		}

		/// <summary>Starts socket writes for every connection with newly queued messages. Called on the network writer
		/// thread (tests that drive a proxy directly call it in its place).</summary>
		internal void StartScheduledWrites()
		{
			for (int i = 0; i < WritesToStart.Count; i++)
			{
				try
				{
					WritesToStart[i].StartScheduledWrite();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception starting write to " + WritesToStart[i].ConnectionId, e);
				}
			}
			WritesToStart.Clear();
			BytesQueuedSinceWriting = 0;
		}

		/// <summary>Encodes one framed message into a new exact-size array, or returns null (having logged why) if it
		/// can't be encoded. Called on the network writer thread.</summary>
		internal byte[]? EncodeMessage(ushort messageType, ushort messageId, object body)
		{
			try
			{
				ArraySegment<byte> encoded = ImpunityNetworkingUtil.WriteMessage(EncodeWriter, messageId, 0, messageType, body);
				byte[] message = new byte[encoded.Count];
				Buffer.BlockCopy(encoded.Array!, encoded.Offset, message, 0, encoded.Count);
				return message;
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception encoding message: " + e.ToString());
				return null;
			}
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
			else if (action is ServerActionBase push)
			{
				// Server originated push — not a reply, so it carries no correlation id, and its bytes are the same for
				// every recipient: encode it once and share the array among all the listeners it was fanned out to.
				byte[]? message = push.EncodedMessage;
				if (message == null)
				{
					message = EncodeMessage(push.GetActionType(), 0, push);
					if (message == null)
					{
						return;
					}
					push.EncodedMessage = message;
				}
				clientInfo.QueueMessage(message, push.Guaranteed);
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

