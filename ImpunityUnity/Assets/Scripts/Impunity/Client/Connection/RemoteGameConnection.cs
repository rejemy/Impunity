using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
		/// Invoked once (on the main thread, via <see cref="Update"/>) when an established connection is lost, with a
		/// <see cref="ImpunityErrorCode.ClientConnectionBrokenError"/> describing why: a socket error, the server
		/// closing the connection (including dropping this client for going silent), or the server going silent for
		/// longer than <see cref="ConnectionTimeoutMillis"/>. By the time it runs <see cref="BaseGameConnection.Connected"/>
		/// is false, every action still awaiting a reply has failed with the same code, and further actions fail
		/// without being sent. Not raised for <see cref="Dispose"/>, nor for a failure during <see cref="Connect"/>
		/// (that is reported to the connect callback instead).
		/// </summary>
		public ImpunityCallback? OnNetworkError { get; set; }

		/// <summary>
		/// The silence window adopted from the server during the handshake (see
		/// <see cref="ImpunityOptions.ConnectionTimeoutMillis"/>): the server drops this client after hearing nothing
		/// from it for this long, and this client gives up on a server it hasn't heard from for this long. 0 before the
		/// handshake completes, or when the server has keepalives disabled.
		/// </summary>
		public int ConnectionTimeoutMillis { get => ServerSilenceTimeoutMillis; }

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

		// Keepalive state (see ImpunityKeepAlive). Until the handshake reply says otherwise, keepalives go out at the
		// interval for the minimum timeout, which is safe against any server setting. LastReceived is stamped on the
		// socket thread for every inbound message; LastGuaranteedSend on the send path, counting guaranteed sends only
		// since a UDP send may never arrive.
		private volatile int KeepAliveIntervalMillis;
		private volatile int ServerSilenceTimeoutMillis;
		private long LastReceivedTimestamp;
		private long LastGuaranteedSendTimestamp;
		// Set once by whichever of Dispose (EndedByDispose) or a lost connection (EndedByLoss) gets there first.
		private int ConnectionEnded;
		private const int EndedByDispose = 1;
		private const int EndedByLoss = 2;

		/// <summary>Test seam: when set, this client stops sending keepalives, so a test can make it look dead to the
		/// server without closing its socket.</summary>
		internal volatile bool KeepAlivesSuppressed = false;

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

			KeepAliveIntervalMillis = ImpunityKeepAlive.IntervalMillis(Options.ConnectionTimeoutFloorMillis);

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

				long now = ImpunityKeepAlive.Now();
				Interlocked.Exchange(ref LastReceivedTimestamp, now);
				Interlocked.Exchange(ref LastGuaranteedSendTimestamp, now);

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
		/// the transport. Does not flush actions still queued in <c>PendingSend</c>. Actions submitted afterwards are not
		/// sent: their callbacks (and any conditional action's) fire on the next <see cref="Update"/> with
		/// <see cref="ImpunityErrorCode.ClientConnectionBrokenError"/>.
		/// </summary>
		public override void Dispose()
		{
			// A deliberate close is not a lost connection: keep the transport's close notification from reporting one.
			Interlocked.CompareExchange(ref ConnectionEnded, EndedByDispose, 0);
			PendingSend.CompleteAdding();

			NetworkClient.Dispose();
		}


#if !UNITY_WEBGL
		// Background writer loop: waits on the send queue and serializes each action onto the transport until the
		// queue is closed (Dispose, or the connection being lost). The wait is bounded by when the next keepalive falls
		// due, so an idle connection still sends one. Exceptions per action are logged and skipped so one bad send
		// can't kill the thread.
		private void NetworkWriterThreadMain()
		{

			while (Running)
			{
				GameStateActionBase? action = null;
				bool gotAction;

				try
				{
					gotAction = PendingSend.TryTake(out action, MillisUntilKeepAliveDue());
				}
				catch (Exception e) when (e is InvalidOperationException || e is ObjectDisposedException)
				{
					// Pending actions queue was closed
					return;
				}

				if (gotAction)
				{
					try
					{
						SendMessage(action!);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in remote connection send attempt", e);
					}
				}
				else if (PendingSend.IsCompleted)
				{
					return;
				}

				// Also checked after a send: a stream of unguaranteed sends never lets the wait time out.
				SendKeepAliveIfDue();

				// Let the transport batch whatever is queued up right now, but never hold anything back while idle.
				if (PendingSend.Count == 0)
				{
					FlushTransport();
				}
			}
		}

		// How long the writer may wait for work before it has to send a keepalive. Infinite when keepalives are off.
		private int MillisUntilKeepAliveDue()
		{
			int interval = KeepAliveIntervalMillis;
			if (KeepAlivesSuppressed)
			{
				// Test seam: keep re-checking so un-suppressing takes effect.
				return 1000;
			}
			if (interval <= 0)
			{
				// The server has keepalives disabled; the interval never changes again after the handshake.
				return Timeout.Infinite;
			}

			long remaining = interval - ImpunityKeepAlive.MillisSince(ref LastGuaranteedSendTimestamp, ImpunityKeepAlive.Now());
			return (int)Math.Max(0, Math.Min(remaining, interval));
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

			SendKeepAliveIfDue();
			FlushTransport();
		}
#endif

		// Pushes out whatever the transport buffered. Called on the send path whenever the send queue runs empty.
		private void FlushTransport()
		{
			try
			{
				NetworkClient.Flush();
			}
			catch (Exception e)
			{
				ImpunityLogger.LogError("Exception in remote connection send attempt", e);
			}
		}

		// Sends a header-only keepalive if nothing guaranteed has gone out for the keepalive interval. Called only on the
		// send path (writer thread, or Update on WebGL), which owns SendBufferWriter.
		private void SendKeepAliveIfDue()
		{
			int interval = KeepAliveIntervalMillis;
			if (interval <= 0 || KeepAlivesSuppressed || Volatile.Read(ref ConnectionEnded) != 0)
			{
				return;
			}

			long now = ImpunityKeepAlive.Now();
			if (ImpunityKeepAlive.MillisSince(ref LastGuaranteedSendTimestamp, now) < interval)
			{
				return;
			}

			try
			{
				ArraySegment<byte> encodedMessage = ImpunityNetworkingUtil.WriteMessage(SendBufferWriter, 0, ImpunityMessageFlags.NO_REPLY, (ushort)ClientActionType.KEEPALIVE, null!);
				Interlocked.Exchange(ref LastGuaranteedSendTimestamp, now);
				NetworkClient.SendGuaranteedMessage(encodedMessage);
			}
			catch (Exception e)
			{
				// A broken socket is reported by the reader thread; nothing more to do here.
				ImpunityLogger.LogDebug("Couldn't send keepalive: " + e.Message);
			}
		}

		/// <summary>Adopts the server's silence window from the handshake reply.</summary>
		protected override void OnHandshakeAccepted(EstablishConnectResult result)
		{
			ServerSilenceTimeoutMillis = Math.Max(0, result.ConnectionTimeoutMillis);
			KeepAliveIntervalMillis = ImpunityKeepAlive.IntervalMillis(ServerSilenceTimeoutMillis);
		}

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
			int ended = Volatile.Read(ref ConnectionEnded);
			if (ended == 0)
			{
				// Checked here rather than on a background thread so it works on WebGL too. The timestamp is stamped by
				// the socket thread, so a long main-thread stall (a level load) doesn't count as server silence.
				int silenceTimeout = ServerSilenceTimeoutMillis;
				if (silenceTimeout > 0)
				{
					long silentFor = ImpunityKeepAlive.MillisSince(ref LastReceivedTimestamp, ImpunityKeepAlive.Now());
					if (silentFor >= silenceTimeout)
					{
						ConnectionLost(new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError,
							"Nothing received from the server for " + silentFor + "ms (timeout " + silenceTimeout + "ms)"));
					}
				}
			}
			else if (ended == EndedByLoss)
			{
				// After a lost connection, catch anything the writer thread registered while ConnectionLost was draining.
				FailAwaitingReplies();
			}

			var tooOld = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(this.Options.ActionTimeoutMillis);

			// Iterating a ConcurrentDictionary tolerates concurrent adds (send path) and removes (reply matching).
			List<GameStateActionBase>? expired = null;
			foreach (var pending in AwaitingReceive)
			{
				if (pending.Value.SentAt >= tooOld)
				{
					continue;
				}

				if (AwaitingReceive.TryRemove(pending.Key, out var timedOut))
				{
					expired ??= new List<GameStateActionBase>();
					expired.Add(timedOut);
				}
			}

			if (expired != null)
			{
				QueueTimeouts(expired);
			}

			base.Update();
		}

		// Completes timed-out actions with a TimeoutError.
		private void QueueTimeouts(List<GameStateActionBase> expired)
		{
			QueueFailures(expired, action => new ImpunityErrorResponse(ImpunityErrorCode.TimeoutError, "Action " + action.GetType().Name + " took too long to complete"));
		}

		// Completes actions with an error. A parent and its conditional are failed together (they share SentAt, so they
		// expire in the same sweep, and a lost connection fails everything), and the dictionary hands them over in no
		// particular order: each conditional is queued right after its parent to keep the documented "parent's callback
		// first" order.
		private void QueueFailures(List<GameStateActionBase> expired, Func<GameStateActionBase, ImpunityErrorResponse> makeError)
		{
			HashSet<GameStateActionBase>? heldForParent = null;
			foreach (GameStateActionBase action in expired)
			{
				if (action is IHasConditionalAction parent && parent.ConditionalAction != null && expired.Contains(parent.ConditionalAction))
				{
					heldForParent ??= new HashSet<GameStateActionBase>();
					heldForParent.Add(parent.ConditionalAction);
				}
			}

			foreach (GameStateActionBase action in expired)
			{
				if (heldForParent != null && heldForParent.Contains(action))
				{
					continue;
				}

				QueueFailure(action, makeError(action));
				if (heldForParent != null && action is IHasConditionalAction parent && parent.ConditionalAction != null && heldForParent.Contains(parent.ConditionalAction))
				{
					QueueFailure(parent.ConditionalAction, makeError(parent.ConditionalAction));
				}
			}
		}

		private void QueueFailure(GameStateActionBase action, ImpunityErrorResponse error)
		{
			action.Error = error;
			CompletedActions.Enqueue(action);
		}

		// Ends a connection that broke on its own (socket error, server close, server silence). Safe to call from any
		// thread and more than once; only the first call (and only if Dispose hasn't run) does anything. Stops further
		// sends, fails every action still waiting for a reply, closes the transport, and queues the main-thread half
		// (OnConnectionLostOnMainThread) behind those failures.
		private void ConnectionLost(ImpunityErrorResponse error)
		{
			if (Interlocked.CompareExchange(ref ConnectionEnded, EndedByLoss, 0) != 0)
			{
				return;
			}

			ImpunityLogger.LogWarning("Connection " + ConnectionId + " lost: " + error.Message);

			// Later DoAction calls fail locally with ClientConnectionBrokenError, and the writer thread exits.
			PendingSend.CompleteAdding();
			FailAwaitingReplies();
			CloseTransport();

			CompletedActions.Enqueue(new NoOpAction(_ => OnConnectionLostOnMainThread(error)));
		}

		private void FailAwaitingReplies()
		{
			List<GameStateActionBase>? orphaned = null;
			foreach (var pending in AwaitingReceive)
			{
				if (AwaitingReceive.TryRemove(pending.Key, out var action))
				{
					orphaned ??= new List<GameStateActionBase>();
					orphaned.Add(action);
				}
			}

			if (orphaned != null)
			{
				QueueFailures(orphaned, action => new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError,
					"Connection lost before " + action.GetType().Name + " was answered; it may or may not have run"));
			}
		}

		// Closes the transport without blocking the caller: on desktop WebSocket a close handshake against a dead peer
		// waits up to a second, which would be a visible hitch when the loss is detected from Update().
		private void CloseTransport()
		{
#if UNITY_WEBGL
			NetworkClient.Disconnect();
#else
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try
				{
					NetworkClient.Disconnect();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogDebug("Exception closing lost connection: " + e.Message);
				}
			});
#endif
		}

		// Main-thread half of ConnectionLost, dispatched by Update after the failed actions queued ahead of it. A loss
		// before Connect completed is reported to the connect callback (its establish or clock-sync action failed), so
		// only an established connection raises OnNetworkError.
		private void OnConnectionLostOnMainThread(ImpunityErrorResponse error)
		{
			bool wasConnected = Connected;
			MarkDisconnected();

			if (wasConnected)
			{
				OnNetworkError?.Invoke(error);
			}
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

			if (action.Guaranteed)
			{
				Interlocked.Exchange(ref LastGuaranteedSendTimestamp, ImpunityKeepAlive.Now());
			}

			// A create/delete carrying a conditional database action gets a second correlation id for the conditional's
			// own reply, written into the parent's body. Must happen before the parent is serialized below. It is
			// independent of the parent's NO_REPLY flag: either one can expect a reply without the other.
			if (action is IHasConditionalAction withConditional)
			{
				GameStateActionBase? conditional = withConditional.ConditionalAction;
				if (conditional != null && conditional.HasCallback())
				{
					ushort conditionalId = AllocateMessageId();
					conditional.MessageId = conditionalId;
					conditional.SentAt = action.SentAt;
					AwaitingReceive[conditionalId] = conditional;
					withConditional.ConditionalMessageId = conditionalId;
				}
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

			Interlocked.Exchange(ref LastReceivedTimestamp, ImpunityKeepAlive.Now());

			int bodyOffset = messageBytes.Offset + ImpunityNetworkingUtil.ReadMessageHeader(messageBytes, out msg);

			if (msg.MessageType == (ushort)ServerActionType.KEEPALIVE)
			{
				// Its only job was to refresh LastReceivedTimestamp.
				return;
			}

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


		// Transport error callback (socket thread). The transport only reports errors that end the connection.
		private void OnNetworkErrorReceived(ImpunityErrorResponse error)
		{
			ConnectionLost(error);
		}

		// Transport callback for the server closing the connection (socket thread). Transports don't raise it for a
		// close this client asked for. The reason code is the WebSocket close code on WebGL and always 0 otherwise.
		private void OnDisconnectedByServer(int reason)
		{
			ConnectionLost(new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, "The server closed the connection (code " + reason + ")"));
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
				// Still complete the action, so its callback fires exactly once rather than never.
				ImpunityLogger.LogError("Error deserializing reply message body for message type " + action.GetActionType() + " id " + messageId, e);
				action.Error = new ImpunityErrorResponse(ImpunityErrorCode.UnknownError, "Couldn't read the server's reply: " + e.Message);
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

			// After Dispose the send queue is closed. Fail the action (and any conditional riding on it) through the
			// normal completion queue instead of throwing, so the caller's callbacks still fire, in order, once each.
			if (PendingSend.IsAddingCompleted)
			{
				FailUnsent(action);
				return;
			}

			try
			{
				PendingSend.Add(action);
			}
			catch (InvalidOperationException)
			{
				// Lost a race with Dispose.
				FailUnsent(action);
			}
		}

		private void FailUnsent(GameStateActionBase action)
		{
			QueueLocalFailure(action, new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, "Connection is closed; " + action.GetType().Name + " was not sent"));
			if (action is IHasConditionalAction parent && parent.ConditionalAction != null)
			{
				QueueLocalFailure(parent.ConditionalAction, new ImpunityErrorResponse(ImpunityErrorCode.ClientConnectionBrokenError, "Connection is closed; the conditional action was not sent"));
			}
		}

	}

}
