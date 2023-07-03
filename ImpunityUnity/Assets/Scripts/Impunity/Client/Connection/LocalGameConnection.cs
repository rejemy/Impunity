using System;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	public class LocalGameConnection : BaseGameConnection, IGameStateListener, IServerSideConnectionProxy
	{
		private static int NextLocalConnectionId = 1;

		string LocalConnectionId;
        GameStateServer State;
		ConcurrentQueue<GameStateActionBase> CompletedActions;

		public string ConnectionId { get { return LocalConnectionId; } }

		public LocalGameConnection(GameStateServer gameState, ImpunityOptions options = null)
		{
			State = gameState;
			CompletedActions = new ConcurrentQueue<GameStateActionBase>();
			int id = NextLocalConnectionId++;
			LocalConnectionId = "LocalConnection_" + id;

			State.AddListener(this);
		}

		public override void Connect(ImpunityCallback onComplete)
		{
			CompletedActions.Enqueue(new NoOpAction(onComplete));
		}

		public override void Update()
		{

			while (CompletedActions.TryDequeue(out GameStateActionBase action))
			{
				try
				{
					action.InvokeOnCompleteCallback();
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError(e, "Exception in action results callback");
				}
			}
		}

		public void OnGameSummaryChanged(BsonDocument summary)
        {

        }

		public override void Dispose()
		{
			State.RemoveListener(this);
		}

		// Called on background thread
		public void ReportActionResult(GameStateActionBase action)
        {
			CompletedActions.Enqueue(action);
		}

		public bool SupportsUnguaranteed() { return false; }

		// Called on background thread
		public void SendGuaranteedMessage()
        {

        }

		// Called on background thread
		public void SendUnguaranteedMessage()
        {
			SendGuaranteedMessage();
		}

		public override void DoAction(GameStateActionBase action)
        {
			action.Origin = this;
			action.ResultsExpected = action.HasCallback();

			State.QueueAction(action);
        }

	}

}