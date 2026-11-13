// ───────── Update notes ─────────
//
// A note says why an update happened ("hit from the left with an axe"), so a receiver can present the change from the
// old value, the new value and its cause together, instead of the cause travelling as a separate event that can arrive
// out of step with the state it explains. It rides one update, is relayed to every listener with that update, and is
// never stored: a later subscriber sees the state, not the note.
//
// Sending mirrors conditional actions: AddUpdateNote parks the note on the entity until whichever send path builds the
// entity's next update takes it (the per-frame sweep, UpdateIfUnchanged, a transaction's update, the flush before an
// unlock), and queues the entity for the sweep so a note with no field change still goes out, on an empty update.
// Notes accumulate: every note added before that send travels with it, in order, as a BSON array on the wire (the
// server relays the value untouched). A note forces a reliable send.
//
// Receiving: while a noted update is being applied, CurrentUpdateNotes holds its notes for that entity, so field
// OnChanged handlers can read them; afterwards OnUpdateNote is raised once per note, in order, which is also how a note
// on an update that changed no field is seen. The writer of a non-client-authoritative entity gets its own notes back
// on the echo, marked as its own. A client-authoritative entity's writer gets no echo, so it presents locally as it
// already does.

using System;
using System.Collections.Generic;

using UltraLiteDB;

namespace Impunity.Connection
{
	/// <summary>A note on a received update: why it happened (see <see cref="IDistributedEntity.AddUpdateNote"/>).</summary>
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
		private static readonly UpdateNote[] NoUpdateNotes = new UpdateNote[0];

		private readonly Dictionary<IDistributedEntity, BsonArray> PendingUpdateNotes = new Dictionary<IDistributedEntity, BsonArray>();

		/// <summary>The entity whose noted update is being applied right now, and its notes.</summary>
		private IDistributedEntity? NotedEntity;
		private UpdateNote[] NotedUpdates = NoUpdateNotes;

		/// <summary>Adds a note to the entity's next update. Backs <see cref="IDistributedEntity.AddUpdateNote"/>; see
		/// there for the semantics.</summary>
		/// <param name="entity">The registered entity whose next update carries the note.</param>
		/// <param name="note">The note. Must not be null or BSON null.</param>
		public void AddUpdateNote(IDistributedEntity entity, BsonValue note)
		{
			if (note == null || note.IsNull)
			{
				throw new ArgumentNullException(nameof(note), "An update note needs a payload");
			}

			if (Connection == null || entity.Manager != this || entity.DistributedEntityId == 0 || !DistributedObjects.ContainsKey(entity.DistributedEntityId))
			{
				// Nobody to tell: a note only exists to travel with an update.
				return;
			}

			if (!PendingUpdateNotes.TryGetValue(entity, out BsonArray? pending))
			{
				pending = new BsonArray();
				PendingUpdateNotes[entity] = pending;
			}
			pending.Add(note);

			// Make sure the next sweep sends an update for this entity even if no field changes.
			DirtyObjects.Add(entity);
		}

		/// <summary>True when the entity has something other than dirty fields waiting to ride its next update: a
		/// conditional action or a note.</summary>
		private bool HasPendingUpdateExtras(IDistributedEntity entity)
		{
			return PendingConditionalActions.ContainsKey(entity) || PendingUpdateNotes.ContainsKey(entity);
		}

		/// <summary>Removes and returns the notes that should ride the update being built for this entity (in the order
		/// they were added), or null.</summary>
		private BsonArray? TakeUpdateNotes(IDistributedEntity entity)
		{
			if (!PendingUpdateNotes.TryGetValue(entity, out BsonArray? notes))
			{
				return null;
			}

			PendingUpdateNotes.Remove(entity);
			return notes;
		}

		/// <summary>The notes on the update being applied to <paramref name="entity"/> right now; empty when it has
		/// none. Backs <see cref="IDistributedEntity.CurrentUpdateNotes"/>.</summary>
		internal IReadOnlyList<UpdateNote> GetCurrentUpdateNotes(IDistributedEntity entity)
		{
			return NotedEntity != null && ReferenceEquals(NotedEntity, entity) ? NotedUpdates : NoUpdateNotes;
		}

		/// <summary>Applies a noted update: its notes are current for the entity while its fields are applied (so their
		/// <c>OnChanged</c> handlers can read them) and while <see cref="IDistributedEntity.OnUpdateNote"/> runs
		/// afterwards, once per note, in the order the writer added them.</summary>
		private void ApplyNotedUpdate(IDistributedEntity entity, ArraySegment<byte> updateData, uint seq, BsonValue notes, long sentServerMs, bool isOwn)
		{
			// The writer sends an array, one element per AddUpdateNote. Anything else is treated as a single note.
			UpdateNote[] received;
			if (notes.IsArray)
			{
				BsonArray array = notes.AsArray!;
				received = new UpdateNote[array.Count];
				for (int i = 0; i < received.Length; i++)
				{
					received[i] = new UpdateNote(array[i], sentServerMs, isOwn);
				}
			}
			else
			{
				received = new[] { new UpdateNote(notes, sentServerMs, isOwn) };
			}

			IDistributedEntity? outerEntity = NotedEntity;
			UpdateNote[] outerNotes = NotedUpdates;
			NotedEntity = entity;
			NotedUpdates = received;
			try
			{
				SetPropertyBytes(entity, updateData, false, seq);

				foreach (UpdateNote note in received)
				{
					try
					{
						entity.OnUpdateNote(note);
					}
					catch (Exception e)
					{
						ImpunityLogger.LogError("Exception in OnUpdateNote: ", e);
					}
				}
			}
			finally
			{
				NotedEntity = outerEntity;
				NotedUpdates = outerNotes;
			}
		}
	}
}
