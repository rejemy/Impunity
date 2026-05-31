

using System;
using Impunity.GameState;

namespace Impunity.Connection
{

	/// <summary>
	/// In-process game connection for single-player or local hosting. Actions are queued directly to the
	/// <see cref="GameStateServer"/> without network serialization. Implements both the client connection
	/// and the server-side connection proxy interfaces.
	/// </summary>
	public class LocalGameConnection : BaseGameConnection, IGameStateListener, IServerSideConnectionProxy
	{
		public GameStateReplicant? ConnectionReplicant { get; set; }

		private static int NextLocalConnectionId = 1;

        private GameStateServer State;


		public LocalGameConnection(GameStateServer gameState, GameStateFormat format, ClientEntityManager? em = null)
			: base(format, em)
		{
			State = gameState;
			ConnectionKey = "local_key";

			ConnectionId = "unconnected";
		}

		public override void Connect(ImpunityCallback onComplete)
		{
			State.AddListener(this);
			State.ConnectionOpened(this);

			int id = NextLocalConnectionId++;
			ConnectionId = "local_" + id;
			
			EstablishConnection(null, null, LocalFormat, onComplete);
		}

		// Called on Live thread
		public void OnGameMetadataChanged(GameStateServer game)
		{
			// Doesn't need to do anything
        }

		// Called on Live thread
		public void OnGameSummaryChanged(GameStateServer game)
		{
			// Doesn't need to do anything
		}

		public override void Dispose()
		{
			State.RemoveListener(this);
			State.ConnectionClosed(this);
		}

		// Called on background thread
		public void ReportActionResult(GameStateActionBase action)
        {
			if (!action.ResultsExpected)
			{
				return;
			}

			CompletedActions.Enqueue(action);
		}

		public bool IsRemote { get => false; }

		public bool SupportsUnguaranteed { get => false; }


		// Called on background thread, this is a message to us, the local client
		public void SendMessageToClient(ServerActionBase message)
        {
			OnServerMessage(message);
		}

		public override void DoAction(GameStateActionBase action)
        {
			action.Origin = this;
			action.ResultsExpected = action.HasCallback();
			action.SentAt = DateTimeOffset.UtcNow;
			
			State.QueueAction(action);
        }

		public void CloseConnectionRequest()
		{
			State.RemoveListener(this);
			State.ConnectionClosed(this);
		}
	}

}