

using System;
using Impunity.GameState;

namespace Impunity.Connection
{

	/// <summary>
	/// In-process game connection for single-player or self-hosted play: the client and an embedded
	/// <see cref="GameStateServer"/> live in the same process.
	/// <para>
	/// Unlike <see cref="RemoteGameConnection"/>, actions are not serialized or sent over a socket — they are handed
	/// straight to the server's action queue, and results/pushes are handed straight back. Because nothing is
	/// serialized, request payloads (<c>BsonDocument</c>s, <see cref="System.ArraySegment{T}"/> property blobs,
	/// etc.) are shared <em>by reference</em> across the client/server boundary; avoid mutating a document after
	/// handing it to a DB call, since the server may read the same instance.
	/// </para>
	/// <para>
	/// This type plays both roles in the system: it is a <see cref="BaseGameConnection"/> (the client API) and also
	/// the server's <see cref="IServerSideConnectionProxy"/> for this client plus an <see cref="IGameStateListener"/>.
	/// The server therefore calls back into this same object (on its worker threads) to deliver results and messages.
	/// </para>
	/// </summary>
	public class LocalGameConnection : BaseGameConnection, IGameStateListener, IServerSideConnectionProxy
	{
		/// <summary>The server-side replicant tracking this connection's subscriptions. Assigned by the server when the connection opens.</summary>
		public GameStateReplicant ConnectionReplicant { get; set; } = null!;

		private static int NextLocalConnectionId = 1;

		private GameStateServer State;


		/// <summary>Creates an in-process connection to an already-running server.</summary>
		/// <param name="gameState">The embedded server to talk to.</param>
		/// <param name="format">The game state format (schema + entity types) this client uses. Registered with the entity manager by the base constructor.</param>
		/// <param name="em">Optional pre-built entity manager; a fresh one is created when null.</param>
		public LocalGameConnection(GameStateServer gameState, GameStateFormat format, ClientEntityManager? em = null)
			: base(format, em)
		{
			State = gameState;
			ConnectionKey = "local_key";

			ConnectionId = "unconnected";
		}

		/// <summary>
		/// Opens the connection: registers as a server listener, signals the server that a connection opened (which
		/// builds the <see cref="ConnectionReplicant"/>), assigns a process-unique local id, then runs the establish
		/// handshake and clock sync. No game id or password is sent — a local connection is implicitly trusted.
		/// </summary>
		/// <param name="onComplete">Invoked with null on success, or an error response if the handshake fails.</param>
		public override void Connect(ImpunityCallback onComplete)
		{
			State.AddListener(this);
			State.ConnectionOpened(this);

			int id = NextLocalConnectionId++;
			ConnectionId = "local_" + id;

			EstablishConnection(null, null, LocalFormat, onComplete);
		}

		/// <summary>Listener hook for server metadata (schema/version) changes. Local connections ignore it. Called on the server live thread.</summary>
		public void OnGameMetadataChanged(GameStateServer game)
		{
			// Doesn't need to do anything
		}

		/// <summary>Listener hook for server summary (player count, etc.) changes. Local connections ignore it. Called on the server live thread.</summary>
		public void OnGameSummaryChanged(GameStateServer game)
		{
			// Doesn't need to do anything
		}

		/// <summary>Tears down the connection: deregisters the listener and tells the server the connection closed (releasing its locks and ephemeral entities).</summary>
		public override void Dispose()
		{
			State.RemoveListener(this);
			State.ConnectionClosed(this);
		}

		/// <summary>
		/// <see cref="IServerSideConnectionProxy"/> hook: the server has finished an action and is returning the result.
		/// Queues it for main-thread dispatch via <see cref="BaseGameConnection.Update"/>. Called on a server worker thread.
		/// </summary>
		/// <param name="action">The completed action carrying its result/error. Ignored if the client expects no reply.</param>
		public void ReportActionResult(GameStateActionBase action)
		{
			if (!action.ResultsExpected)
			{
				return;
			}

			CompletedActions.Enqueue(action);
		}

		/// <summary>Always false — this is an in-process connection, not a network one.</summary>
		public bool IsRemote { get => false; }

		/// <summary>Always false — there is no unreliable transport in-process, so every action is effectively guaranteed.</summary>
		public bool SupportsUnguaranteed { get => false; }


		/// <summary>
		/// <see cref="IServerSideConnectionProxy"/> hook: the server is pushing a state message (channel/object create,
		/// entity update, event, lock, delete, broadcast, …) to this client. Queues it for main-thread dispatch.
		/// Called on a server worker thread.
		/// </summary>
		/// <param name="message">The server-originated message to deliver.</param>
		public void SendMessageToClient(ServerActionBase message)
		{
			OnServerMessage(message);
		}

		/// <summary>
		/// Submits an action to the embedded server. Stamps it with this connection as the origin, records whether a
		/// reply is expected, timestamps it, and queues it on the server's appropriate worker thread.
		/// </summary>
		/// <param name="action">The action to run server-side.</param>
		public override void DoAction(GameStateActionBase action)
		{
			action.Origin = this;
			action.ResultsExpected = action.HasCallback();
			action.SentAt = DateTimeOffset.UtcNow;

			State.QueueAction(action);
		}

		/// <summary>
		/// <see cref="IServerSideConnectionProxy"/> hook: the server is requesting this connection be closed
		/// (e.g. after a fatal action error). Deregisters the listener and notifies the server of the close.
		/// </summary>
		public void CloseConnectionRequest()
		{
			State.RemoveListener(this);
			State.ConnectionClosed(this);
		}
	}

}
