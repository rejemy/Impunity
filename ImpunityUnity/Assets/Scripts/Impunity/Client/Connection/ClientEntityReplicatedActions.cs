// ───────── Replicated actions ─────────
//
// A database action attached to an entity rides that entity's next update to the server, in the same message, and
// the server runs it only if it applied the update — e.g. "the potion's effect lands on my player entity, and only
// then is the potion removed from my inventory". It is the update-side counterpart of the conditional action on
// CreateObject / DeleteEntity, and uses the same server path (see IHasConditionalAction, and
// docs/guides/DistributedEntities.md, "Conditional actions").
//
// Pending actions wait here, per entity, until whichever send path builds that entity's next update takes them: the
// per-frame sweep (SendUpdates), UpdateExclusive, or the flush before an unlock. Attaching one queues the entity for
// the sweep even when no field is dirty, so an action is never stranded waiting for an unrelated change — it rides
// an empty update instead. Several actions attached before one flush travel as a single CompoundDatabaseAction whose
// reply is fanned back out, so each action's own callback still fires with its own result.

using System;
using System.Collections.Generic;

using Impunity.GameState;

namespace Impunity.Connection
{
	public partial class ClientEntityManager
	{
		private readonly Dictionary<IDistributedEntity, List<GameStateActionBase>> PendingReplicatedActions = new Dictionary<IDistributedEntity, List<GameStateActionBase>>();

		/// <summary>Attaches a database action to ride this entity's next update. Backs
		/// <see cref="IDistributedEntity.AddReplicatedAction"/>; see there for the semantics.</summary>
		/// <param name="entity">The registered entity whose next update carries the action.</param>
		/// <param name="action">A document action or <see cref="CompoundDatabaseAction"/>, with its own callback.</param>
		/// <exception cref="ArgumentException">The action is not one that may ride an update.</exception>
		/// <exception cref="InvalidOperationException">The manager has no connection.</exception>
		public void AddReplicatedAction(IDistributedEntity entity, GameStateActionBase action)
		{
			if (action == null)
			{
				throw new ArgumentNullException(nameof(action));
			}

			// Checked here rather than left to the server: the server rejects the whole update over an invalid action,
			// and a plain update has no callback, so the field changes it carried would vanish without a trace.
			if (!action.IsDBOperation() || !action.CanRunConditionally())
			{
				throw new ArgumentException("Action " + action.GetType().Name + " cannot ride an entity update; only document actions and CompoundDatabaseAction can", nameof(action));
			}

			if (Connection == null)
			{
				throw new InvalidOperationException("ClientEntityManager has no connection");
			}

			if (entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				Connection.QueueLocalFailure(action, new ImpunityErrorResponse(ImpunityErrorCode.ActionBadRequest, "Entity is not registered with this connection"));
				return;
			}

			if (!PendingReplicatedActions.TryGetValue(entity, out List<GameStateActionBase>? pending))
			{
				pending = new List<GameStateActionBase>();
				PendingReplicatedActions[entity] = pending;
			}
			pending.Add(action);

			// Make sure the next sweep sends an update for this entity even if no field changes.
			DirtyObjects.Add(entity);
		}

		private bool HasPendingReplicatedAction(IDistributedEntity entity)
		{
			return PendingReplicatedActions.ContainsKey(entity);
		}

		// Removes and returns what should ride the update being built for this entity: nothing, the one pending action,
		// or several combined into a single compound.
		private GameStateActionBase? TakeReplicatedAction(IDistributedEntity entity)
		{
			if (!PendingReplicatedActions.TryGetValue(entity, out List<GameStateActionBase>? pending))
			{
				return null;
			}

			PendingReplicatedActions.Remove(entity);
			return pending.Count == 1 ? pending[0] : CombineReplicatedActions(pending);
		}

		// Wraps several pending actions in one CompoundDatabaseAction, so they share the update's single conditional
		// slot, and fans the compound's reply back out to each action's own callback. The compound runs every
		// sub-action even if one fails, so each gets its own result; if it was skipped, each gets the skip error.
		private static CompoundDatabaseAction CombineReplicatedActions(List<GameStateActionBase> actions)
		{
			bool anyCallback = false;
			foreach (GameStateActionBase action in actions)
			{
				if (action.HasCallback())
				{
					anyCallback = true;
					break;
				}
			}

			ImpunityCallback<List<ActionResult>>? fanOut = null;
			if (anyCallback)
			{
				fanOut = (err, results) =>
				{
					for (int i = 0; i < actions.Count; i++)
					{
						GameStateActionBase action = actions[i];
						if (!action.HasCallback())
						{
							continue;
						}

						if (results != null && i < results.Count)
						{
							action.ApplyResult(results[i]);
						}
						else
						{
							action.Error = err ?? new ImpunityErrorResponse(ImpunityErrorCode.InternalServerError, "No result for combined replicated action");
						}

						// Isolated so one throwing callback can't starve the rest.
						try
						{
							action.InvokeOnCompleteCallback();
						}
						catch (Exception e)
						{
							ImpunityLogger.LogError("Exception in replicated action callback", e);
						}
					}
				};
			}

			return new CompoundDatabaseAction(actions, fanOut);
		}

		// Resolves every pending action of an entity that is going away, so no callback waits forever. The update they
		// were waiting for will never be sent, so they are reported as not run.
		private void FailPendingReplicatedActions(IDistributedEntity entity)
		{
			if (!PendingReplicatedActions.TryGetValue(entity, out List<GameStateActionBase>? pending))
			{
				return;
			}

			PendingReplicatedActions.Remove(entity);

			if (Connection == null)
			{
				return;
			}

			foreach (GameStateActionBase action in pending)
			{
				Connection.QueueLocalFailure(action, new ImpunityErrorResponse(ImpunityErrorCode.ActionConditionNotMet,
					"Not run: entity " + entity.DistributedEntityId + " was removed before its next update was sent"));
			}
		}
	}
}
