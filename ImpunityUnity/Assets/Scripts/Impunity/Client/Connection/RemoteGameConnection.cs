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
	/// Request/reply correlation is positional: every reply-expecting action is appended to <c>AwaitingReceive</c>
	/// in send order and matched to the next reply in arrival order. This relies on the transport delivering replies
	/// in the same order requests were sent (true for the single TCP stream). See <see cref="Update"/> for the
	/// timeout caveat this creates.
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
		private ConcurrentQueue<GameStateActionBase> AwaitingReceive;

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
			AwaitingReceive = new ConcurrentQueue<GameStateActionBase>();

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
		/// Timeouts are checked from the front of <c>AwaitingReceive</c>, which is also the queue replies are matched
		/// against in FIFO order. Two consequences worth knowing:
		/// <list type="bullet">
		/// <item><c>SentAt</c> is stamped when the action is enqueued in <see cref="DoAction"/>, not when it actually
		/// leaves the writer thread, so time spent waiting behind a send backlog counts toward the timeout.</item>
		/// <item>If an action is timed out and removed here while its reply is still in flight, the late reply will be
		/// matched to the <em>next</em> waiting action instead (positional matching has no per-message id to resync on).
		/// In practice the timeout is generous relative to round-trip time, so this is rare.</item>
		/// </list>
		/// </remarks>
		public override void Update()
		{
#if UNITY_WEBGL
			SendPendingMessages();
#endif
			var tooOld = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(this.Options.ActionTimeoutMillis);

			while (AwaitingReceive.TryPeek(out var pendingAction))
			{
				if (pendingAction.SentAt >= tooOld)
				{
					break;
				}

				AwaitingReceive.TryDequeue(out var _);

				pendingAction.Error = new ImpunityErrorResponse(ImpunityErrorCode.TimeoutError, "Action " + pendingAction.GetType().Name + " took too long to complete");
				CompletedActions.Enqueue(pendingAction);
			}

			base.Update();
		}

		// Serializes one action and writes it to the transport. Called only on the writer thread (or from Update on
		// WebGL). Actions with a callback are registered in AwaitingReceive (in send order) so their reply can be
		// matched later; callback-less actions are flagged NO_REPLY so the server won't send one.
		private void SendMessage(GameStateActionBase action)
		{
			ushort flags = 0;
			if (!action.HasCallback())
			{
				flags |= ImpunityMessageFlags.NO_REPLY;
			}
			else
			{
				AwaitingReceive.Enqueue(action);
			}

			ArraySegment<byte> encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBufferWriter, 0, flags, action.GetActionType(), action);

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

		// Matches an incoming reply to the oldest reply-expecting action (FIFO), deserializes the result into it, and
		// queues it for its callback on the main thread. Correlation is positional: messageId is logged for diagnostics
		// but not used to look up the action, so this depends on replies arriving in send order (true over TCP).
		private void HandleReplyMessage(ushort messageId, ArraySegment<byte> messageBytes)
		{
			GameStateActionBase action;
			if (!AwaitingReceive.TryDequeue(out action))
			{
				ImpunityLogger.LogError("Got response with id " + messageId + " when we weren't expecting any responses");
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
