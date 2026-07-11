using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Net;

using UltraLiteDB;

using Impunity.GameState;
using Impunity.Networking;

namespace Impunity.Connection
{

	/// <summary>
	/// Client connection that talks to a server over a network transport (TCP by default, optionally WebSocket).
	/// <para>
	/// Threading model: outbound actions are buffered in <c>PendingSend</c> and serialized on a dedicated background
	/// writer thread (<c>NetworkWriterThreadMain</c>); inbound bytes are read on the transport's socket thread,
	/// deserialized there, and enqueued onto <c>CompletedActions</c> so that callbacks and server-push handlers run
	/// on the main thread when <see cref="Update"/> is called. Reliable actions go over TCP; actions flagged
	/// unguaranteed go over UDP when the transport negotiated it (otherwise they fall back to TCP).
	/// </para>
	/// <para>
	/// Request/reply correlation is by id: every reply-expecting action is assigned a header correlation id and tracked
	/// in <c>AwaitingReceive</c> keyed by that id. The server echoes the id on its reply, so each reply is matched to the
	/// exact action it belongs to regardless of arrival order — a dropped, late, or timed-out reply cannot cause later
	/// replies to be mis-matched.
	/// </para>
	/// <para>Under WebGL there is no background writer thread; the send queue is pumped synchronously from <see cref="Update"/>.</para>
	/// </summary>
	public class RemoteGameConnection : BaseGameConnection
	{
		/// <summary>
		/// Invoked (on the main thread, via <see cref="Update"/>) when the transport reports a network-level failure
		/// such as a broken or unreachable connection. Note this fires for socket <em>errors</em>; a clean,
		/// server-initiated disconnect currently only logs and does not raise this callback (see
		/// <see cref="OnDisconnectedByServer"/>).
		/// </summary>
		public ImpunityCallback? OnNetworkError { get; set; }

		private BlockingCollection<GameStateActionBase> PendingSend;
		// Reply-expecting actions awaiting a server reply, keyed by the correlation id written into each message
		// header. Replies are matched to the exact action by id (not by arrival order), so a dropped, late, or
		// timed-out reply can no longer desync the matching of later replies. Touched by the send path (writer thread,
		// or Update on WebGL), the socket read thread (reply matching), and the main thread (timeout sweep).
		private ConcurrentDictionary<ushort, GameStateActionBase> AwaitingReceive;
		// Monotonic source of header correlation ids; assigned only on the (single-threaded) send path. 0 is reserved
		// for "untracked" — no-reply actions and server-originated pushes.
		private ushort NextReplyId;

		private string GameId;
		private string GamePassword;
		private ImpunityOptions Options;
		private IImpunityNetworkClient NetworkClient;
#if !UNITY_WEBGL
		private Thread? NetworkWriterThread;
#endif
		private bool Running;

		private byte[] SendBuffer;
		private ByteWriter SendBufferWriter;

		/// <summary>
		/// Constructs a remote connection over an already-built transport. Prefer the <c>MakeTCPRemoteConnection</c> /
		/// <c>MakeWebsocketRemoteConnection</c> factory helpers, which create the transport for you.
		/// </summary>
		/// <param name="networkClient">The transport used to send/receive framed messages.</param>
		/// <param name="gameId">Identifier of the game world to join, sent during the handshake.</param>
		/// <param name="gamePassword">Plaintext password for the world; hashed before being sent. Pass an empty string for an unprotected world.</param>
		/// <param name="format">The game state format (schema + entity types) this client uses.</param>
		/// <param name="options">Connection options (timeouts, ports, …); defaults are used when null.</param>
		/// <param name="em">Optional pre-built entity manager; a fresh one is created when null.</param>
		public RemoteGameConnection(IImpunityNetworkClient networkClient, string gameId, string gamePassword, GameStateFormat format, ImpunityOptions? options, ClientEntityManager? em) : base(format, em)
		{
			PendingSend = new BlockingCollection<GameStateActionBase>();
			AwaitingReceive = new ConcurrentDictionary<ushort, GameStateActionBase>();

			GameId = gameId;
			GamePassword = gamePassword;

			if (options == null)
			{
				options = new ImpunityOptions();
			}
			Options = options;
			NetworkClient = networkClient;
			NetworkClient.OnNetworkError = OnNetworkErrorReceived!;
			NetworkClient.OnMessageRecieved = OnNetworkMessageReceived;
			NetworkClient.OnDisconnectedByServer = OnDisconnectedByServer;

			SendBuffer = new byte[ImpunityConstants.MaxMessageSize];
			SendBufferWriter = new ByteWriter(SendBuffer);

			ConnectionId = "unconnected";
		}

		/// <summary>Creates a connection backed by a TCP transport to a server at the given IP endpoint.</summary>
		/// <param name="serverEndpoint">The server's address and port.</param>
		/// <param name="gameId">Identifier of the game world to join.</param>
		/// <param name="gamePassword">Plaintext world password (hashed before transmission); empty for none.</param>
		/// <param name="format">The game state format this client uses.</param>
		/// <param name="options">Optional connection options.</param>
		/// <param name="em">Optional pre-built entity manager.</param>
		/// <returns>An unconnected <see cref="RemoteGameConnection"/>; call <see cref="Connect"/> to open it.</returns>
		public static RemoteGameConnection MakeTCPRemoteConnection(IPEndPoint serverEndpoint, string gameId, string gamePassword, GameStateFormat format, ImpunityOptions? options = null, ClientEntityManager? em = null)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}

			return new RemoteGameConnection(ImpunityTCPClient.MakeTCPClient(serverEndpoint, options), gameId, gamePassword, format, options, em);
		}

		/// <summary>Creates a connection backed by a TCP transport to a server at the given hostname and port.</summary>
		/// <param name="hostname">The server host to resolve and connect to.</param>
		/// <param name="port">The server's TCP port.</param>
		/// <param name="gameId">Identifier of the game world to join.</param>
		/// <param name="gamePassword">Plaintext world password (hashed before transmission); empty for none.</param>
		/// <param name="format">The game state format this client uses.</param>
		/// <param name="options">Optional connection options.</param>
		/// <param name="em">Optional pre-built entity manager.</param>
		/// <returns>An unconnected <see cref="RemoteGameConnection"/>; call <see cref="Connect"/> to open it.</returns>
		public static RemoteGameConnection MakeTCPRemoteConnection(string hostname, int port, string gameId, string gamePassword, GameStateFormat format, ImpunityOptions? options = null, ClientEntityManager? em = null)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}

			return new RemoteGameConnection(ImpunityTCPClient.MakeTCPClient(hostname, port, options), gameId, gamePassword, format, options, em);
		}

		/// <summary>Creates a connection backed by a WebSocket transport to the given hostname and port (e.g. for WebGL builds).</summary>
		/// <param name="hostname">The server host to connect to.</param>
		/// <param name="port">The server's WebSocket port.</param>
		/// <param name="gameId">Identifier of the game world to join.</param>
		/// <param name="gamePassword">Plaintext world password (hashed before transmission); empty for none.</param>
		/// <param name="format">The game state format this client uses.</param>
		/// <param name="options">Optional connection options.</param>
		/// <param name="em">Optional pre-built entity manager.</param>
		/// <returns>An unconnected <see cref="RemoteGameConnection"/>; call <see cref="Connect"/> to open it.</returns>
		public static RemoteGameConnection MakeWebsocketRemoteConnection(string hostname, int port, string gameId, string gamePassword, GameStateFormat format, ImpunityOptions? options = null, ClientEntityManager? em = null)
		{
			if (options == null)
			{
				options = new ImpunityOptions();
			}

			return new RemoteGameConnection(ImpunityWebSocketClient.MakeWebSocketClient(hostname, port, options), gameId, gamePassword, format, options, em);
		}

		/// <summary>
		/// Opens the transport, starts the background writer thread, then runs the establish handshake and clock sync.
		/// </summary>
		/// <param name="onComplete">
		/// Invoked on the main thread with null on success, or an error if the transport could not connect or the
		/// handshake failed. A transport-level failure is delivered by queuing a no-op carrying the error, so the
		/// callback always runs on the main thread during <see cref="Update"/> (never on the socket thread).
		/// </param>
		public override void Connect(ImpunityCallback onComplete)
		{
			NetworkClient.Connect((ImpunityErrorResponse? err) =>
			{
				if (err != null)
				{
					NoOpAction connectAction = new NoOpAction(onComplete);
					connectAction.Error = err;
					CompletedActions.Enqueue(connectAction);
					return;
				}

				Running = true;

#if !UNITY_WEBGL
				NetworkWriterThread = new Thread(new ThreadStart(NetworkWriterThreadMain));
				NetworkWriterThread.IsBackground = true;
				NetworkWriterThread.Name = "Network writer";
				NetworkWriterThread.Start();
#endif
				EstablishConnection(GameId, GamePassword, LocalFormat, onComplete);
			});
		}


		/// <summary>
		/// Closes the connection: stops accepting new outbound actions (which lets the writer thread exit) and disposes
		/// the transport. Does not flush actions still queued in <c>PendingSend</c>.
		/// </summary>
		public override void Dispose()
		{
			PendingSend.CompleteAdding();

			NetworkClient.Dispose();
		}


#if !UNITY_WEBGL
		// Background writer loop: blocks on the send queue and serializes each action onto the transport until the
		// queue is closed (Dispose). Exceptions per action are logged and skipped so one bad send can't kill the thread.
		private void NetworkWriterThreadMain()
		{

			while (Running)
			{
				GameStateActionBase? action = null;

				try
				{
					action = PendingSend.Take();
				}
				catch (InvalidOperationException)
				{
					// Pending actions queue was closed
					return;
				}

				try
				{
					SendMessage(action);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in remote connection send attempt", e);
				}
			}
		}
#else
		// WebGL has no background threads, so the send queue is drained synchronously from Update() instead.
		private void SendPendingMessages()
		{
			if (!Running)
			{
				return;
			}

			while(PendingSend.Count > 0)
			{
				GameStateActionBase action = PendingSend.Take();
				try
				{
					SendMessage(action);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in remote connection send attempt", e);
				}
			}

		}
#endif

		/// <summary>
		/// Per-frame pump. On WebGL it first drains the outbound queue (no writer thread there). It then expires any
		/// reply-expecting actions older than <see cref="ImpunityOptions.ActionTimeoutMillis"/>, completing them with a
		/// <see cref="ImpunityErrorCode.TimeoutError"/>, before running the base pump (which flushes dirty entities and
		/// dispatches completed actions / server pushes on the main thread).
		/// </summary>
		/// <remarks>
		/// Every reply-expecting action in <c>AwaitingReceive</c> is checked, and any whose <c>SentAt</c> is older than
		/// <see cref="ImpunityOptions.ActionTimeoutMillis"/> is completed with a <see cref="ImpunityErrorCode.TimeoutError"/>
		/// and removed. Note <c>SentAt</c> is stamped when the action is enqueued in <see cref="DoAction"/>, not when it
		/// actually leaves the writer thread, so time spent waiting behind a send backlog counts toward the timeout.
		/// Because replies are matched by id, a reply that arrives after its action was timed out here simply matches
		/// nothing and is dropped, without affecting any other pending action.
		/// </remarks>
		public override void Update()
		{
#if UNITY_WEBGL
			SendPendingMessages();
#endif
			var tooOld = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(this.Options.ActionTimeoutMillis);

			// Iterating a ConcurrentDictionary tolerates concurrent adds (send path) and removes (reply matching).
			foreach (var pending in AwaitingReceive)
			{
				if (pending.Value.SentAt >= tooOld)
				{
					continue;
				}

				if (AwaitingReceive.TryRemove(pending.Key, out var timedOut))
				{
					timedOut.Error = new ImpunityErrorResponse(ImpunityErrorCode.TimeoutError, "Action " + timedOut.GetType().Name + " took too long to complete");
					CompletedActions.Enqueue(timedOut);
				}
			}

			base.Update();
		}

		// Allocates the next header correlation id. Called only on the (single-threaded) send path, so it needs no
		// locking. Skips 0 (reserved for "untracked") and any id still in flight, so a wraparound can't collide with a
		// pending action.
		private ushort AllocateMessageId()
		{
			ushort id;
			do
			{
				id = ++NextReplyId;
			} while (id == 0 || AwaitingReceive.ContainsKey(id));
			return id;
		}

		// Serializes one action and writes it to the transport. Called only on the writer thread (or from Update on
		// WebGL). Actions with a callback are assigned a fresh correlation id and registered in AwaitingReceive keyed by
		// that id so their reply can be matched later; callback-less actions are flagged NO_REPLY (id 0) so the server
		// won't send one.
		private void SendMessage(GameStateActionBase action)
		{
			ushort flags = 0;
			ushort messageId = 0;
			if (!action.HasCallback())
			{
				flags |= ImpunityMessageFlags.NO_REPLY;
			}
			else
			{
				messageId = AllocateMessageId();
				action.MessageId = messageId;
				AwaitingReceive[messageId] = action;
			}

			ArraySegment<byte> encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBufferWriter, messageId, flags, action.GetActionType(), action);

			if (action.Guaranteed)
			{
				NetworkClient.SendGuaranteedMessage(encodedMessage);
			}
			else
			{
				NetworkClient.SendUnguaranteedMessage(encodedMessage);
			}


			action.Cleanup();
		}

		// Transport callback, runs on the socket reader thread. Splits a framed message into either a reply (matched
		// to a waiting action) or a server push (deserialized to its ServerActionBase). Both paths enqueue onto
		// CompletedActions so the actual work happens on the main thread in Update().
		private void OnNetworkMessageReceived(ArraySegment<byte> messageBytes)
		{
			MessageHeaderStruct msg;

			int bodyOffset = messageBytes.Offset + ImpunityNetworkingUtil.ReadMessageHeader(messageBytes, out msg);

			// Reply message
			if (msg.MessageType == (ushort)ServerActionType.CLIENT_REPLY)
			{
				HandleReplyMessage(msg.MessageId, new ArraySegment<byte>(messageBytes.Array, bodyOffset, messageBytes.Array.Length - bodyOffset));
				return;
			}

			// Server message
			Type messageActionClassType = ServerActionFactory.GetActionClassType(msg.MessageType);

			BsonMapper mapper = ImpunityUtil.GetBsonMapper();
			ServerActionBase action = (ServerActionBase)mapper.DeserializeFromBytes(messageActionClassType, messageBytes.Array, bodyOffset);

			// Ready for callback!
			CompletedActions.Enqueue(action);
		}


		// Transport error callback (socket thread). Wraps the error in a no-op targeting OnNetworkError so it is
		// reported on the main thread via Update().
		private void OnNetworkErrorReceived(ImpunityErrorResponse error)
		{
			var errorAction = new NoOpAction(OnNetworkError);
			errorAction.Error = error;
			CompletedActions.Enqueue(errorAction);
		}

		// Transport callback for a clean, server-initiated close.
		// NOTE (flagged for review): this only logs the reason — it does not raise OnNetworkError or otherwise notify
		// application code, so a graceful disconnect is currently invisible to callers (only socket *errors* surface,
		// via OnNetworkErrorReceived). The reason code is also always 0 in the current TCP transport.
		private void OnDisconnectedByServer(int reason)
		{
			ImpunityLogger.LogInformation("Disconnected by server with code " + reason);
		}

		// Matches an incoming reply to its originating action by the header correlation id, deserializes the result into
		// it, and queues it for its callback on the main thread. Matching is by id, so replies need not arrive in send
		// order and a missing/late reply can't shift the matching of others.
		private void HandleReplyMessage(ushort messageId, ArraySegment<byte> messageBytes)
		{
			GameStateActionBase action;
			if (!AwaitingReceive.TryRemove(messageId, out action))
			{
				// No pending action for this id — it most likely already timed out (and was completed with a
				// TimeoutError), or this is a duplicate/late reply. Safe to drop: id matching means this does not
				// affect any other pending action.
				ImpunityLogger.LogWarning("Got reply for message id " + messageId + " with no matching pending action (timed out or duplicate)");
				return;
			}

			try
			{
				action.DeserializeResults(messageBytes);
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Error deserializing reply message body for message type " + action.GetActionType() + " id " + messageId, e);
				return;
			}

			// Ready for callback!
			CompletedActions.Enqueue(action);
		}


		/// <summary>
		/// Queues an action to be serialized and sent by the writer thread. Timestamps it for timeout tracking. Returns
		/// immediately — the action is sent asynchronously and its result (if any) arrives later via <see cref="Update"/>.
		/// </summary>
		/// <param name="action">The action to send to the server.</param>
		public override void DoAction(GameStateActionBase action)
		{
			action.SentAt = DateTimeOffset.UtcNow;
			PendingSend.Add(action);

		}

	}

}
