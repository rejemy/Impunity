using System;
using System.Collections.Concurrent;

using UltraLiteDB;

using Impunity.GameState;

namespace Impunity.Connection
{

	public class LocalGameConnection : BaseGameConnection, IGameStateListener
	{

        GameStateServer State;
		ConcurrentQueue<GameStateActionBase> CompletedActions;

		public LocalGameConnection(GameStateServer gameState, ImpunityOptions options = null)
		{
			State = gameState;
			CompletedActions = new ConcurrentQueue<GameStateActionBase>();
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
		public void OnActionComplete(GameStateActionBase action)
		{
			CompletedActions.Enqueue(action);
		}

		public override void DoAction(GameStateActionBase action)
        {
			if (action.HasCallback())
			{
				action.OnCompleteHandler = this.OnActionComplete;
			}
			State.QueueAction(action);
        }

	}

}