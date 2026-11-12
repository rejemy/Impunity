// ───────── Update notes ─────────
//
// A note says why an update happened ("hit from the left with an axe"), so a receiver can present the change from the
// old value, the new value and its cause together, instead of the cause travelling as a separate event that can arrive
// out of step with the state it explains. It rides one update, is relayed to every listener with that update, and is
// never stored: a later subscriber sees the state, not the note.
//
// Sending mirrors replicated actions: SetUpdateNote parks the note on the entity until whichever send path builds the
// entity's next update takes it (the per-frame sweep, UpdateExclusive, a transaction's update, the flush before an
// unlock), and queues the entity for the sweep so a note with no field change still goes out, on an empty update. A
// note forces a reliable send.
//
// Receiving: while a noted update is being applied, CurrentUpdateNote is set for that entity, so field OnChanged
// handlers can read it; afterwards OnUpdateNote is raised, which is also how a note on an update that changed no field
// is seen. The writer of a non-client-authoritative entity gets its own note back on the echo, marked as its own. A
// client-authoritative entity's writer gets no echo, so it presents locally as it already does.

using System;
using System.Collections.Generic;

using UltraLiteDB;

namespace Impunity.Connection
{
	/// <summary>A note on a received update: why it happened (see <see cref="IDistributedEntity.SetUpdateNote"/>).</summary>
	public readonly struct UpdateNote
	{
		/// <summary>The payload the writer attached. Never null.</summary>
		public readonly BsonValue Data;

		/// <summary>Server time (Unix ms, as <see cref="BaseGameConnection.GetServerTime"/>) at which the writer sent the
		/// update: its estimate of server time at the send.</summary>
		public readonly long SentServerMs;

		/// <summary>True when this connection wrote the update: the note is arriving on the echo of its own write. Usually
		/// a reason to skip presentation the writer already played locally.</summary>
		public readonly bool IsOwn;

		/// <summary>Builds a received note.</summary>
		public UpdateNote(BsonValue data, long sentServerMs, bool isOwn)
		{
			Data = data;
			SentServerMs = sentServerMs;
			IsOwn = isOwn;
		}
	}

	public partial class ClientEntityManager
	{
		private readonly Dictionary<IDistributedEntity, BsonValue> PendingUpdateNotes = new Dictionary<IDistributedEntity, BsonValue>();

		/// <summary>The entity whose noted update is being applied right now, and its note.</summary>
		private IDistributedEntity? NotedEntity;
		private UpdateNote NotedUpdate;

		/// <summary>Attaches a note to the entity's next update. Backs <see cref="IDistributedEntity.SetUpdateNote"/>; see
		/// there for the semantics.</summary>
		/// <param name="entity">The registered entity whose next update carries the note.</param>
		/// <param name="note">The note; null (or BSON null) removes a pending one.</param>
		public void SetUpdateNote(IDistributedEntity entity, BsonValue? note)
		{
			if (note == null || note.IsNull)
			{
				PendingUpdateNotes.Remove(entity);
				return;
			}

			if (Connection == null || entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				// Nobody to tell: a note only exists to travel with an update.
				return;
			}

			PendingUpdateNotes[entity] = note;

			// Make sure the next sweep sends an update for this entity even if no field changes.
			DirtyObjects.Add(entity);
		}

		/// <summary>True when the entity has something other than dirty fields waiting to ride its next update: a
		/// replicated action or a note.</summary>
		private bool HasPendingUpdateExtras(IDistributedEntity entity)
		{
			return PendingReplicatedActions.ContainsKey(entity) || PendingUpdateNotes.ContainsKey(entity);
		}

		/// <summary>Removes and returns the note that should ride the update being built for this entity, or null.</summary>
		private BsonValue? TakeUpdateNote(IDistributedEntity entity)
		{
			if (!PendingUpdateNotes.TryGetValue(entity, out BsonValue? note))
			{
				return null;
			}

			PendingUpdateNotes.Remove(entity);
			return note;
		}

		/// <summary>The note on the update being applied to <paramref name="entity"/> right now, if it has one. Backs
		/// <see cref="IDistributedEntity.CurrentUpdateNote"/>.</summary>
		internal UpdateNote? GetCurrentUpdateNote(IDistributedEntity entity)
		{
			return NotedEntity != null && ReferenceEquals(NotedEntity, entity) ? NotedUpdate : (UpdateNote?)null;
		}

		/// <summary>Applies a noted update: the note is current for the entity while its fields are applied (so their
		/// <c>OnChanged</c> handlers can read it) and while <see cref="IDistributedEntity.OnUpdateNote"/> runs afterwards.</summary>
		private void ApplyNotedUpdate(IDistributedEntity entity, ArraySegment<byte> updateData, ushort seq, UpdateNote note)
		{
			IDistributedEntity? outerEntity = NotedEntity;
			UpdateNote outerNote = NotedUpdate;
			NotedEntity = entity;
			NotedUpdate = note;
			try
			{
				SetPropertyBytes(entity, updateData, false, seq);

				try
				{
					entity.OnUpdateNote(note);
				}
				catch (Exception e)
				{
					ImpunityLogger.LogError("Exception in OnUpdateNote: ", e);
				}
			}
			finally
			{
				NotedEntity = outerEntity;
				NotedUpdate = outerNote;
			}
		}
	}
}
