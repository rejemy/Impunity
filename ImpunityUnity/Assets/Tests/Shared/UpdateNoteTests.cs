// ───────── Update notes ─────────
//
// Covers entity.AddUpdateNote / tx.Note and how a note is received: it arrives with its update (visible from field
// OnChanged handlers as CurrentUpdateNotes, then in OnUpdateNote), reaches every subscriber and the writer on its echo
// (marked as its own), carries the sender's send time, is gone once the update has been applied, is never seen by a
// later subscriber, travels alone on an empty update, replaces an earlier pending note, goes nowhere with a rejected
// update, rides only a transaction's committed attempt, and is not echoed on a client-authoritative entity.
//
// Making a client stale: local connections hand every action to the in-proc server at once, but a client only sees
// what the server pushed it when the test pumps that client. So A writes, only A is pumped, and B acts on old values.
#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class UpdateNoteTests : ImpunityTestHarness
	{
		const int ITEMS = IntegrationTestCollections.ITEMS;

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(IntegrationTestEntity),
					typeof(IntegrationTestChannel)
				}
			);
		}

		// ───────── Helpers ─────────

		sealed class World
		{
			public LocalGameConnection ConnA;
			public LocalGameConnection ConnB;
			public IntegrationTestChannel ChannelA;
			public IntegrationTestChannel ChannelB;
			/// <summary>The entity as A sees it (A created it).</summary>
			public IntegrationTestEntity A;
			/// <summary>The same entity as B sees it.</summary>
			public IntegrationTestEntity B;
		}

		/// <summary>What one client saw: each Health change with the note current at the time, and each OnUpdateNote.</summary>
		sealed class Seen
		{
			public readonly List<(int from, int to, UpdateNote? note)> HealthChanges = new List<(int, int, UpdateNote?)>();
			public readonly List<UpdateNote> Notes = new List<UpdateNote>();
			/// <summary>For each OnUpdateNote, how many Health changes had been seen by then.</summary>
			public readonly List<int> ChangesBeforeNote = new List<int>();

			/// <summary>The first note on the update being applied to the entity right now, if it has any.</summary>
			public static UpdateNote? FirstNote(IntegrationTestEntity entity)
			{
				return entity.CurrentUpdateNotes.Count > 0 ? entity.CurrentUpdateNotes[0] : (UpdateNote?)null;
			}

			public static Seen Watch(IntegrationTestEntity entity)
			{
				var seen = new Seen();
				entity.Health.OnChanged += (from, to) => seen.HealthChanges.Add((from, to, FirstNote(entity)));
				entity.OnUpdateNoteEvent += note =>
				{
					Assert.Greater(entity.CurrentUpdateNotes.Count, 0, "CurrentUpdateNotes is empty during OnUpdateNote");
					seen.Notes.Add(note);
					seen.ChangesBeforeNote.Add(seen.HealthChanges.Count);
				};
				return seen;
			}
		}

		async Task<LocalGameConnection> ConnectClient(string key)
		{
			var conn = Track(new LocalGameConnection(GameServer, Format));
			conn.ConnectionKey = key;
			await Pump(conn.ConnectAsync(), AllConnections());
			Assert.IsTrue(conn.Connected, "Local connection failed");
			return conn;
		}

		/// <summary>Two distinct clients on one channel, and one entity (Health 1) that both have.</summary>
		async Task<World> Setup(string channelName, bool clientAuthoritative = false)
		{
			CreateServer();
			var w = new World();
			w.ConnA = await ConnectClient(channelName + "_a");
			w.ConnB = await ConnectClient(channelName + "_b");
			w.ChannelA = await Pump(w.ConnA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			w.ChannelB = await Pump(w.ConnB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			w.A = new IntegrationTestEntity { IsClientAuthoritative = clientAuthoritative };
			w.A.Health.Set(1);
			w.A.DisplayName.Set("tree");
			await Pump(w.ConnA.EntityManager.CreateObjectAsync(w.A, w.ChannelA, false), AllConnections());
			await PumpUntil(() => w.ChannelB.DistributedObjects.Count == 1, AllConnections());
			w.B = (IntegrationTestEntity)w.ChannelB.DistributedObjects.Values.First();
			Assert.AreEqual(1, w.B.Health.Get());
			return w;
		}

		static BsonDocument Hit(string side)
		{
			return new BsonDocument { ["hit"] = side };
		}

		// ───────── Delivery ─────────

		/// <summary>A noted update: B's field handler sees the change and its note together, then OnUpdateNote fires; the
		/// writer gets the same on its echo, marked as its own. Afterwards nothing is current.</summary>
		[Test, Category("UpdateNote")]
		public async Task Note_ArrivesWithTheUpdate_OnEveryClient()
		{
			var w = await Setup("note_basic");
			var seenA = Seen.Watch(w.A);
			var seenB = Seen.Watch(w.B);

			long before = w.ConnA.GetServerTime();
			w.A.Health.Set(4);
			w.A.AddUpdateNote(Hit("left"));

			await PumpUntil(() => seenA.Notes.Count == 1 && seenB.Notes.Count == 1, AllConnections());

			Assert.AreEqual(1, seenB.HealthChanges.Count);
			var (from, to, note) = seenB.HealthChanges[0];
			Assert.AreEqual(1, from);
			Assert.AreEqual(4, to);
			Assert.IsTrue(note.HasValue, "The note was not current while B's field changed");
			Assert.AreEqual("left", note.Value.Data["hit"].AsString);
			Assert.IsFalse(note.Value.IsOwn);
			Assert.GreaterOrEqual(note.Value.SentServerMs, before);
			Assert.AreEqual(1, seenB.ChangesBeforeNote[0], "OnUpdateNote ran before the fields were applied");
			Assert.IsFalse(seenB.Notes[0].IsOwn);

			Assert.AreEqual(1, seenA.HealthChanges.Count);
			Assert.IsTrue(seenA.HealthChanges[0].note.HasValue, "The writer's echo carried no note");
			Assert.IsTrue(seenA.HealthChanges[0].note.Value.IsOwn, "The writer's echo was not marked as its own");
			Assert.IsTrue(seenA.Notes[0].IsOwn);
			Assert.AreEqual(seenB.Notes[0].SentServerMs, seenA.Notes[0].SentServerMs);

			Assert.AreEqual(0, w.A.CurrentUpdateNotes.Count);
			Assert.AreEqual(0, w.B.CurrentUpdateNotes.Count);
		}

		/// <summary>A note rides one update only: the next plain update arrives with none.</summary>
		[Test, Category("UpdateNote")]
		public async Task Note_RidesOneUpdateOnly()
		{
			var w = await Setup("note_once");
			var seenB = Seen.Watch(w.B);

			w.A.Health.Set(4);
			w.A.AddUpdateNote(Hit("left"));
			await PumpUntil(() => seenB.Notes.Count == 1, AllConnections());

			w.A.Health.Set(5);
			await PumpUntil(() => w.B.Health.Get() == 5, AllConnections());
			Assert.AreEqual(2, seenB.HealthChanges.Count);
			Assert.IsFalse(seenB.HealthChanges[1].note.HasValue, "A later update carried the note again");
			Assert.AreEqual(1, seenB.Notes.Count);
		}

		/// <summary>A note with no field change still goes out, on an empty update, and reaches OnUpdateNote.</summary>
		[Test, Category("UpdateNote")]
		public async Task NoteAlone_IsSentOnAnEmptyUpdate()
		{
			var w = await Setup("note_alone");
			var seenA = Seen.Watch(w.A);
			var seenB = Seen.Watch(w.B);

			w.A.AddUpdateNote(Hit("miss"));
			await PumpUntil(() => seenA.Notes.Count == 1 && seenB.Notes.Count == 1, AllConnections());
			Assert.AreEqual("miss", seenB.Notes[0].Data["hit"].AsString);
			Assert.AreEqual(0, seenB.HealthChanges.Count);
			Assert.IsTrue(seenA.Notes[0].IsOwn);
		}

		/// <summary>Notes accumulate: every note added before the send rides the same update, all of them are current
		/// while its fields are applied, and OnUpdateNote runs once per note, in the order added.</summary>
		[Test, Category("UpdateNote")]
		public async Task SeveralNotesBeforeTheSend_AllArriveInOrder()
		{
			var w = await Setup("note_several");
			var seenA = Seen.Watch(w.A);
			var seenB = Seen.Watch(w.B);
			int currentDuringChange = -1;
			w.B.Health.OnChanged += (_, __) => currentDuringChange = w.B.CurrentUpdateNotes.Count;

			w.A.Health.Set(4);
			w.A.AddUpdateNote(Hit("left"));
			w.A.AddUpdateNote(Hit("right"));
			await PumpUntil(() => seenA.Notes.Count == 2 && seenB.Notes.Count == 2, AllConnections());

			Assert.AreEqual(1, seenB.HealthChanges.Count, "The two notes did not ride one update");
			Assert.AreEqual(2, currentDuringChange, "Both notes should be current while the field changes");
			Assert.AreEqual("left", seenB.Notes[0].Data["hit"].AsString);
			Assert.AreEqual("right", seenB.Notes[1].Data["hit"].AsString);
			Assert.AreEqual(seenB.Notes[0].SentServerMs, seenB.Notes[1].SentServerMs);
			Assert.IsTrue(seenA.Notes[0].IsOwn && seenA.Notes[1].IsOwn, "The writer's echo was not marked as its own");

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.AreEqual(2, seenB.Notes.Count, "A note was delivered twice");
		}

		/// <summary>A note needs a payload: null and BSON null throw rather than silently sending nothing.</summary>
		[Test, Category("UpdateNote")]
		public async Task NullNote_Throws()
		{
			var w = await Setup("note_null");
			Assert.Throws<ArgumentNullException>(() => w.A.AddUpdateNote(null));
			Assert.Throws<ArgumentNullException>(() => w.A.AddUpdateNote(BsonValue.Null));
		}

		/// <summary>Notes are never stored: a client subscribing after the update sees the state, not the note.</summary>
		[Test, Category("UpdateNote")]
		public async Task LaterSubscriber_DoesNotSeeTheNote()
		{
			var w = await Setup("note_late");
			var seenB = Seen.Watch(w.B);
			w.A.Health.Set(4);
			w.A.AddUpdateNote(Hit("left"));
			await PumpUntil(() => seenB.Notes.Count == 1, AllConnections());

			var connC = await ConnectClient("note_late_c");
			var channelC = await Pump(connC.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("note_late", null), AllConnections());
			await PumpUntil(() => channelC.DistributedObjects.Count == 1, AllConnections());
			var c = (IntegrationTestEntity)channelC.DistributedObjects.Values.First();
			var seenC = Seen.Watch(c);

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.AreEqual(4, c.Health.Get());
			Assert.AreEqual(0, seenC.Notes.Count);
			Assert.AreEqual(0, c.CurrentUpdateNotes.Count);
		}

		/// <summary>A rejected guarded update takes its note with it: nobody receives it.</summary>
		[Test, Category("UpdateNote")]
		public async Task RejectedUpdate_TakesItsNoteWithIt()
		{
			var w = await Setup("note_rejected");
			var seenA = Seen.Watch(w.A);
			w.A.Health.Set(5);
			await PumpUntil(() => w.A.Health.Get() == 5, w.ConnA);
			Assert.AreEqual(1, w.B.Health.Get(), "B must still be stale for this test");

			w.B.Health.Set(2);
			w.B.AddUpdateNote(Hit("stale"));
			Assert.AreEqual(GuardedResult.Stale, await Pump(w.B.UpdateIfUnchangedAsync(), AllConnections()));

			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.AreEqual(0, seenA.Notes.Count, "The rejected update's note was relayed");
		}

		/// <summary>A client-authoritative entity's writer gets no echo, so it never sees its own note; others do.</summary>
		[Test, Category("UpdateNote")]
		public async Task ClientAuthoritative_WriterSeesNoNote()
		{
			var w = await Setup("note_clientauth", clientAuthoritative: true);
			var seenA = Seen.Watch(w.A);
			var seenB = Seen.Watch(w.B);

			w.A.Health.Set(4);
			Assert.AreEqual(1, seenA.HealthChanges.Count, "Client-authoritative Set applies locally");
			Assert.IsFalse(seenA.HealthChanges[0].note.HasValue);
			w.A.AddUpdateNote(Hit("left"));

			await PumpUntil(() => seenB.Notes.Count == 1, AllConnections());
			Assert.IsTrue(seenB.HealthChanges.Single().note.HasValue);
			Assert.IsFalse(seenB.Notes[0].IsOwn);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.AreEqual(0, seenA.Notes.Count);
		}

		/// <summary>Without a connection there is nobody to tell: setting a note does nothing and doesn't throw.</summary>
		[Test, Category("UpdateNote")]
		public void Offline_AddUpdateNote_DoesNothing()
		{
			var offline = new IntegrationTestEntity();
			offline.AddUpdateNote(Hit("left"));
			Assert.AreEqual(0, offline.CurrentUpdateNotes.Count);
		}

		// ───────── Transactions ─────────

		/// <summary>A transaction's note rides only the attempt that is accepted: the rejected attempt's note goes nowhere.</summary>
		[Test, Category("UpdateNote")]
		public async Task TransactNote_RidesTheCommittedAttemptOnly()
		{
			var w = await Setup("note_tx");
			var seenA = Seen.Watch(w.A);
			w.A.Health.Set(5);
			await PumpUntil(() => w.A.Health.Get() == 5, w.ConnA);
			Assert.AreEqual(1, w.B.Health.Get(), "B must still be stale for this test");

			var result = await Pump(w.B.TransactAsync(tx =>
			{
				w.B.Health.Set(w.B.Health.Get() - 1);
				tx.Note(new BsonDocument { ["attempt"] = tx.Attempt });
				return true;
			}), AllConnections());
			Assert.AreEqual(TransactResult.Committed, result);

			await PumpUntil(() => seenA.Notes.Count >= 1 && w.A.Health.Get() == 4, AllConnections());
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.AreEqual(1, seenA.Notes.Count);
			Assert.AreEqual(2, seenA.Notes[0].Data["attempt"].AsInt32);
		}

		/// <summary>A declined body's note is dropped: neither sent nor left for the next update.</summary>
		[Test, Category("UpdateNote")]
		public async Task TransactNote_DeclinedIsDropped()
		{
			var w = await Setup("note_tx_decline");
			var seenA = Seen.Watch(w.A);

			var result = await Pump(w.B.TransactAsync(tx => { tx.Note(Hit("declined")); return false; }), AllConnections());
			Assert.AreEqual(TransactResult.Declined, result);

			w.B.Health.Set(3);
			await PumpUntil(() => w.A.Health.Get() == 3, AllConnections());
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.AreEqual(0, seenA.Notes.Count, "The declined body's note was sent");
		}

		/// <summary>A note with a delete is a mixed body: the transaction fails and nothing is deleted.</summary>
		[Test, Category("UpdateNote")]
		public async Task TransactNote_WithADelete_Fails()
		{
			var w = await Setup("note_tx_delete");

			var error = await PumpExpectingError(w.B.TransactAsync(tx => { tx.Note(Hit("felled")); tx.Delete(w.B.Health.Guard); return true; }), AllConnections());
			Assert.IsNotNull(error);
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, error.ErrorId);
			await PumpFor(TimeSpan.FromSeconds(0.1), AllConnections());
			Assert.IsFalse(w.A.WasDeleted);
		}
	}
}
