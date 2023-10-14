
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	public class LocalGameConnection : BaseGameConnection, IGameStateListener, IServerSideConnectionProxy
	{
		public string ConnectionId { get; private set; }
		public GameStateReplicant ConnectionReplicant { get; set; }

		private static int NextLocalConnectionId = 1;

        private GameStateServer State;


		public LocalGameConnection(GameStateServer gameState, GameStateFormat format, ClientEntityManager em = null)
			: base(format, em)
		{
			State = gameState;
			
			int id = NextLocalConnectionId++;
			ConnectionId = "LocalConnection_" + id;
		}

		public override void Connect(ImpunityCallback onComplete)
		{
			State.AddListener(this);
			State.ConnectionOpened(this);

			EstablishConnection(null, null, LocalFormat, onComplete);
		}


		public void OnGameMetadataChanged(GameStateServer game)
		{
			// Doesn't need to do anything
        }

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
			CompletedActions.Enqueue(action);
		}

		public bool IsRemote { get { return false; } }

		public bool SupportsUnguaranteed() { return false; }


		// Called on background thread, this is a message to us, the local client
		public void SendMessageToClient(ServerActionBase message)
        {
			OnServerMessage(message);
		}

		public override void DoAction(GameStateActionBase action)
        {
			action.Origin = this;
			action.ResultsExpected = action.HasCallback();

			State.QueueAction(action);
        }

		public void CloseConnectionRequest()
		{
			State.RemoveListener(this);
			State.ConnectionClosed(this);
		}
	}

}