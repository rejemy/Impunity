// ───────── Transport Suite ─────────
//
// A transport-agnostic battery of connection-level tests, run identically over every transport leg:
//   • TransportSuite_Local        — LocalGameConnection, in-proc, no sockets (this file)
//   • TransportSuite_EmbeddedTcp  — RemoteGameConnection over TCP to an in-proc ImpunityServer (this file)
//   • TransportSuite_StandaloneTcp / _StandaloneWs — dotnet-only legs against the out-of-proc
//     standalone server (ImpunityTests/Host/), which is why every test here must stick to the
//     BaseGameConnection-level API and never touch GameServer/TcpServer directly.
//
// The standalone legs reuse one server (and one world) for the whole fixture, so every channel, lock,
// and document name is uniquified via Name().
#nullable disable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public abstract class TransportSuite : ImpunityTestHarness
	{
		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = IntegrationTestCollections.ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(IntegrationTestEntity),
					typeof(IntegrationTestChannel)
				}
			);
		}

		/// <summary>Makes sure a server is reachable — in-proc legs create one on first call; the
		/// standalone legs already have one running.</summary>
		protected abstract Task EnsureServerAsync();

		/// <summary>Opens and connects one client over this leg's transport.</summary>
		protected abstract Task<BaseGameConnection> OpenConnectionAsync();

		/// <summary>Uniquifies a channel/lock/document name so legs that reuse one server across the
		/// fixture never collide between tests (or with leftovers from earlier runs).</summary>
		protected static string Name(string prefix)
		{
			return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
		}

		// ═══════════════════════════════════════════════════════════
		// Tests
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Transport")]
		public async Task ConnectHandshake()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			Assert.IsTrue(conn.Connected);
			Assert.IsNotNull(conn.ConnectionId, "Server should have assigned a connection id");
		}

		[Test, Category("Transport")]
		public async Task GameSummaryRoundTrip()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			var marker = Name("summary");
			await Pump(conn.SetGameSummaryAsync(new BsonDocument { ["marker"] = marker }), conn);

			var summary = await Pump(conn.GetGameSummaryAsync(), conn);
			Assert.IsNotNull(summary);
			Assert.AreEqual(marker, (string)summary["marker"]);
		}

		[Test, Category("Transport")]
		public async Task InsertAndFindDocument()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			var id = Name("doc");
			var doc = new BsonDocument { ["_id"] = id, ["name"] = "Sword", ["power"] = 42 };
			await Pump(conn.InsertDocumentAsync(IntegrationTestCollections.ITEMS, doc), conn);

			var found = await Pump(conn.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, id), conn);
			Assert.IsNotNull(found);
			Assert.AreEqual("Sword", (string)found["name"]);
			Assert.AreEqual(42, (int)found["power"]);
		}

		[Test, Category("Transport")]
		public async Task UpdateAndDeleteDocument()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			var id = Name("doc");
			await Pump(conn.UpsertDocumentAsync(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = id, ["v"] = 1 }), conn);

			var updated = await Pump(conn.UpdateDocumentAsync(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = id, ["v"] = 2 }), conn);
			Assert.IsTrue(updated);

			var found = await Pump(conn.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, id), conn);
			Assert.AreEqual(2, (int)found["v"]);

			var deleted = await Pump(conn.DeleteDocumentAsync(IntegrationTestCollections.ITEMS, id), conn);
			Assert.IsTrue(deleted);

			var gone = await Pump(conn.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, id), conn);
			Assert.IsNull(gone);
		}

		[Test, Category("Transport")]
		public async Task ChannelSubscribeAndReplicate()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("chan");

			// A creates the channel with an initial Status.
			var aInit = new IntegrationTestChannel();
			aInit.Status.Set("active");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, aInit), AllConnections());

			// B subscribes and sees the initial value.
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());
			Assert.IsNotNull(bChannel);
			Assert.AreEqual("active", bChannel.Status.Get());

			// A changes the value; B sees the change.
			aChannel.Status.Set("busy");
			await PumpUntil(() => bChannel.Status.Get() == "busy", TimeSpan.FromSeconds(3), AllConnections());
		}

		[Test, Category("Transport")]
		public async Task EntityCreateAndReplicate()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("chan");

			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var entity = new IntegrationTestEntity();
			entity.Health.Set(100);
			entity.DisplayName.Set("Hero");
			await Pump(connA.EntityManager.CreateObjectAsync(entity, aChannel, false), AllConnections());

			await PumpUntil(() => bChannel.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), AllConnections());

			IntegrationTestEntity bEntity = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				bEntity = obj as IntegrationTestEntity;
				break;
			}
			Assert.IsNotNull(bEntity);
			Assert.AreEqual(100, bEntity.Health.Get());
			Assert.AreEqual("Hero", bEntity.DisplayName.Get());
		}

		[Test, Category("Transport")]
		public async Task Broadcast()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var payload = Name("hello");
			int receivedType = -1;
			string receivedBody = null;
			connB.OnBroadcastMessage = (type, body, sender) =>
			{
				receivedType = type;
				receivedBody = body.AsString;
			};

			connA.SendBroadcastMessage(42, payload);

			await PumpUntil(() => receivedType == 42, TimeSpan.FromSeconds(3), AllConnections());
			Assert.AreEqual(payload, receivedBody);
		}

		[Test, Category("Transport")]
		public async Task NamedLockContention()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var lockName = Name("lock");

			Assert.IsTrue(await Pump(connA.TryToLockAsync(lockName), AllConnections()), "A should acquire the lock");
			Assert.IsFalse(await Pump(connB.TryToLockAsync(lockName), AllConnections()), "B should fail to acquire the held lock");
			Assert.IsTrue(await Pump(connA.UnlockAsync(lockName), AllConnections()), "A should release the lock");
			Assert.IsTrue(await Pump(connB.TryToLockAsync(lockName), AllConnections()), "B should acquire the lock after release");
			await Pump(connB.UnlockAsync(lockName), AllConnections());
		}

		/// <summary>
		/// The RunExclusive handoff guarantee, over each real transport: B's body must observe the edit A made
		/// inside its own scope. A writes only after the gate opens, so the write and the scope exit happen in one
		/// turn with no Update() between them — the case the flush-before-unlock exists for.
		/// </summary>
		[Test, Category("Transport")]
		public async Task RunExclusiveHandsOffEdits()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("chan");

			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var gate = new TaskCompletionSource<bool>();
			bool aStarted = false;
			var aScope = aChannel.RunExclusiveAsync(async () =>
			{
				aStarted = true;
				await gate.Task;
				aChannel.Status.Set("written-by-a");
			}, 10f);

			await PumpUntil(() => aStarted, TimeSpan.FromSeconds(5), AllConnections());

			string bObserved = null;
			var bScope = bChannel.RunExclusiveAsync(() => bObserved = bChannel.Status.Get(), 10f);

			await PumpFor(TimeSpan.FromSeconds(0.5), AllConnections());
			Assert.IsNull(bObserved, "B's body ran while A held the lock");

			gate.SetResult(true);

			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(aScope, AllConnections()));
			Assert.AreEqual(RunExclusiveResult.Ran, await Pump(bScope, AllConnections()));

			Assert.AreEqual("written-by-a", bObserved, "B's body did not observe A's edit from the previous scope");
		}

		/// <summary>
		/// Transact over each real transport: two clients fire increments at once, each also queueing several on its
		/// own instance in one frame. Every one commits and none is lost, which needs both the per-entity queue (no
		/// self-races) and the rerun on staleness (the other client's writes) to work over the wire.
		/// </summary>
		[Test, Category("Transport"), Category("Transact")]
		public async Task TransactIncrementsFromTwoClients()
		{
			const int N = 4;
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("tx");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var a = new IntegrationTestEntity();
			await Pump(connA.EntityManager.CreateObjectAsync(a, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(5), AllConnections());
			IntegrationTestEntity b = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				b = (IntegrationTestEntity)obj;
			}

			var results = new List<Task<TransactResult>>();
			for (int i = 0; i < N; i++)
			{
				results.Add(a.TransactAsync(_ => { a.Health.Set(a.Health.Get() + 1); return true; }, 4 * N));
				results.Add(b.TransactAsync(_ => { b.Health.Set(b.Health.Get() + 1); return true; }, 4 * N));
			}

			foreach (var result in results)
			{
				Assert.AreEqual(TransactResult.Committed, await Pump(result, AllConnections()));
			}

			await PumpUntil(() => a.Health.Get() == 2 * N && b.Health.Get() == 2 * N, TimeSpan.FromSeconds(5), AllConnections());
		}

		/// <summary>
		/// Transact over each real transport: a rerun after a stale rejection starts only once the rejected attempt's
		/// replicated action has reported, so the application state its callback releases (a reserved slot) is free.
		/// </summary>
		[Test, Category("Transport"), Category("Transact")]
		public async Task TransactRerunWaitsForRejectedAttemptsAction()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("tx_rerun");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var a = new IntegrationTestEntity();
			await Pump(connA.EntityManager.CreateObjectAsync(a, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(5), AllConnections());
			IntegrationTestEntity b = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				b = (IntegrationTestEntity)obj;
			}

			// Make B stale: A's write reaches the server, and only A is pumped until it is confirmed.
			Assert.AreEqual(TransactResult.Committed, await Pump(a.TransactAsync(_ => { a.Health.Set(1); return true; }), connA));
			Assert.AreEqual(0, b.Health.Get(), "B must still be stale for this test");

			string docId = Name("slot");
			int reserved = 0;
			var reservedAtRun = new List<int>();
			var result = b.TransactAsync(_ =>
			{
				reservedAtRun.Add(reserved);
				b.Health.Set(b.Health.Get() + 1);
				reserved++;
				b.AddReplicatedAction(new InsertDocumentAction(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = docId }, (err, _) =>
				{
					if (err != null)
					{
						reserved--;
					}
				}));
				return true;
			});

			Assert.AreEqual(TransactResult.Committed, await Pump(result, AllConnections()));
			Assert.GreaterOrEqual(reservedAtRun.Count, 2, "Expected a stale run and a rerun");
			foreach (int seen in reservedAtRun)
			{
				Assert.AreEqual(0, seen, "A run saw an earlier attempt's slot still reserved");
			}
			await PumpUntil(() => reserved == 1, TimeSpan.FromSeconds(5), AllConnections());
		}

		/// <summary>
		/// Guarded delete and replace over each real transport: once another client changes a guarded field, B's
		/// guarded replace and guarded delete are stale (nothing changes); after B catches up, the guarded delete goes
		/// through. Exercises the "xs" field on a delete and "rxs" on a create through every serializer path.
		/// </summary>
		[Test, Category("Transport"), Category("GuardedDelete")]
		public async Task GuardedDeleteAndReplaceAreStaleAfterAChange()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("guard");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var a = new IntegrationTestEntity { UniqueName = "tile" };
			a.Health.Set(1);
			await Pump(connA.EntityManager.CreateObjectAsync(a, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(5), AllConnections());
			IntegrationTestEntity b = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				b = (IntegrationTestEntity)obj;
			}

			// Make B stale on Health: A's write reaches the server, and only A is pumped until it is confirmed.
			a.Health.Set(2);
			await Pump(a.UpdateExclusiveAsync(), connA);
			Assert.AreEqual(1, b.Health.Get(), "B must still be stale for this test");

			var crop = new IntegrationTestEntity { UniqueName = "tile" };
			var replaceError = await PumpExpectingError(connB.EntityManager.CreateObjectAsync(crop, bChannel, false, null, b.DistributedEntityId, b.Health.Mask), AllConnections());
			Assert.IsNotNull(replaceError, "The stale guarded replace went through");
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, replaceError.ErrorId);

			// The stale replace's reply came after the echo, so B has caught up — but a fresh write by A makes it stale again.
			a.Health.Set(3);
			await Pump(a.UpdateExclusiveAsync(), connA);
			var deleteError = await PumpExpectingError(b.DeleteExclusiveAsync(b.Health.Mask, null), connB);
			Assert.IsNotNull(deleteError, "The stale guarded delete went through");
			Assert.AreEqual(ImpunityErrorCode.ActionStaleData, deleteError.ErrorId);
			Assert.AreEqual(3, b.Health.Get());

			Assert.IsTrue(await Pump(b.DeleteExclusiveAsync(b.Health.Mask, null), AllConnections()));
			await PumpUntil(() => aChannel.DistributedObjects.Count == 0, TimeSpan.FromSeconds(5), AllConnections());
		}

		/// <summary>
		/// An update note reaches the other client with its update (current while the field changes) and the writer on its
		/// echo marked as its own; a note with no field change travels alone on an empty update. Over every transport,
		/// since the writer's copy is encoded separately from the shared one.
		/// </summary>
		[Test, Category("Transport"), Category("UpdateNote")]
		public async Task UpdateNoteReachesEveryoneWithItsUpdate()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("note");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var a = new IntegrationTestEntity();
			a.Health.Set(1);
			await Pump(connA.EntityManager.CreateObjectAsync(a, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(5), AllConnections());
			IntegrationTestEntity b = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				b = (IntegrationTestEntity)obj;
			}

			var bChanges = new List<UpdateNote?>();
			var aNotes = new List<UpdateNote>();
			var bNotes = new List<UpdateNote>();
			b.Health.OnChanged += (_, __) => bChanges.Add(b.CurrentUpdateNote);
			a.OnUpdateNoteEvent += aNotes.Add;
			b.OnUpdateNoteEvent += bNotes.Add;

			a.Health.Set(4);
			a.SetUpdateNote(new BsonDocument { ["hit"] = "left" });
			await PumpUntil(() => aNotes.Count == 1 && bNotes.Count == 1, TimeSpan.FromSeconds(5), AllConnections());
			Assert.AreEqual(1, bChanges.Count);
			Assert.IsTrue(bChanges[0].HasValue, "The note was not current while B's field changed");
			Assert.AreEqual("left", bChanges[0].Value.Data["hit"].AsString);
			Assert.IsFalse(bNotes[0].IsOwn);
			Assert.IsTrue(aNotes[0].IsOwn, "The writer's echo was not marked as its own");
			Assert.AreNotEqual(0L, bNotes[0].SentServerMs);

			a.SetUpdateNote(new BsonDocument { ["hit"] = "miss" });
			await PumpUntil(() => aNotes.Count == 2 && bNotes.Count == 2, TimeSpan.FromSeconds(5), AllConnections());
			Assert.AreEqual("miss", bNotes[1].Data["hit"].AsString);
			Assert.AreEqual(1, bChanges.Count, "A note-only update changed a field");
		}

		/// <summary>
		/// A transaction's delete whose guarded field changed first is rejected and reruns against the winner's value,
		/// then goes through; a transaction queued behind it fails because the entity is gone. Over every transport, since
		/// the commit depends on the delete push arriving before the delete's reply.
		/// </summary>
		[Test, Category("Transport"), Category("Transact")]
		public async Task TransactDeleteRerunsOnStaleGuard()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("txdel");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var a = new IntegrationTestEntity { UniqueName = "tree" };
			a.Health.Set(1);
			await Pump(connA.EntityManager.CreateObjectAsync(a, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(5), AllConnections());
			IntegrationTestEntity b = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				b = (IntegrationTestEntity)obj;
			}

			a.Health.Set(2);
			await Pump(a.UpdateExclusiveAsync(), connA);
			Assert.AreEqual(1, b.Health.Get(), "B must still be stale for this test");

			var seen = new List<int>();
			var deleted = b.TransactAsync(tx => { seen.Add(b.Health.Get()); tx.Delete(b.Health.Mask, "felled"); return true; });
			var behind = b.TransactAsync(_ => { b.Health.Set(0); return true; });

			Assert.AreEqual(TransactResult.Committed, await Pump(deleted, AllConnections()));
			CollectionAssert.AreEqual(new[] { 1, 2 }, seen);
			var behindError = await PumpExpectingError(behind, AllConnections());
			Assert.IsNotNull(behindError, "The transaction queued behind the delete committed");
			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, behindError.ErrorId);
			await PumpUntil(() => aChannel.DistributedObjects.Count == 0, TimeSpan.FromSeconds(5), AllConnections());
		}

		/// <summary>
		/// Regression: a channel broadcast reaches EVERY listener, including the client that wrote it.
		/// GameStateChannel.SendToListeners hands the SAME action instance to each listener, so the
		/// outbound queue must carry the recipient alongside the action rather than stamping it onto
		/// the shared instance — otherwise, with three listeners, all three queued copies resolve to
		/// whichever recipient was stamped last and two clients receive nothing.
		/// </summary>
		[Test, Category("Transport")]
		public async Task ChannelBroadcastReachesEveryListener()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();
			var connC = await OpenConnectionAsync();

			var channelName = Name("chan");

			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());
			var cChannel = await Pump(connC.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			// A writes. The channel is not client-authoritative, so A's own Get() only advances on the
			// server's echo — making A a listener that must be served just like B and C.
			aChannel.Status.Set("broadcast");

			await PumpUntil(
				() => aChannel.Status.Get() == "broadcast"
					&& bChannel.Status.Get() == "broadcast"
					&& cChannel.Status.Get() == "broadcast",
				TimeSpan.FromSeconds(3), AllConnections());

			Assert.AreEqual("broadcast", aChannel.Status.Get(), "Writer never received its own echo");
			Assert.AreEqual("broadcast", bChannel.Status.Get(), "Second listener never received the update");
			Assert.AreEqual("broadcast", cChannel.Status.Get(), "Third listener never received the update");
		}

		[Test, Category("Transport")]
		public async Task UnsubscribeStopsReplication()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("chan");

			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			bool bSawChange = false;
			bChannel.Status.OnChanged += (o, n) => bSawChange = true;

			// Settle an initial value B does see.
			aChannel.Status.Set("before");
			await PumpUntil(() => bSawChange, TimeSpan.FromSeconds(3), AllConnections());

			// B unsubscribes; changes must stop arriving.
			await Pump(bChannel.UnsubscribeAsync(), AllConnections());
			bSawChange = false;

			aChannel.Status.Set("after");
			await PumpFor(TimeSpan.FromSeconds(0.5), AllConnections());

			Assert.IsFalse(bSawChange, "Replication leaked to an unsubscribed client");
		}

		/// <summary>Two clients pick up the same world item, each with a conditional "add to my inventory". Over a
		/// remote leg this is what exercises the conditional's second correlation id: its reply must find its own
		/// callback, independent of the delete's.</summary>
		[Test, Category("Transport"), Category("Conditional")]
		public async Task ConditionalDelete_RaceOnlyWinnerGetsItem()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("pickup");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var item = new IntegrationTestEntity();
			item.DisplayName.Set("sword");
			await Pump(connA.EntityManager.CreateObjectAsync(item, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(3), AllConnections());

			IntegrationTestEntity bItem = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				bItem = (IntegrationTestEntity)obj;
			}

			var entities = new IntegrationTestEntity[] { item, bItem };
			var invIds = new[] { Name("inv_a"), Name("inv_b") };
			var deletes = new[] { new CallbackProbe<bool>(), new CallbackProbe<bool>() };
			var inserts = new[] { new CallbackProbe<BsonValue>(), new CallbackProbe<BsonValue>() };

			for (int i = 0; i < 2; i++)
			{
				var doc = new BsonDocument { ["_id"] = invIds[i], ["item"] = "sword" };
				entities[i].Delete(null, deletes[i].OnComplete, new InsertDocumentAction(IntegrationTestCollections.ITEMS, doc, inserts[i].OnComplete));
			}

			await PumpUntil(() => deletes[0].Fired && deletes[1].Fired && inserts[0].Fired && inserts[1].Fired, TimeSpan.FromSeconds(5), AllConnections());

			int winner = (deletes[0].Error == null && deletes[0].Value) ? 0 : 1;
			int loser = 1 - winner;
			Assert.IsTrue(deletes[winner].Error == null && deletes[winner].Value, "Neither delete succeeded");
			Assert.IsNotNull(deletes[loser].Error, "Both deletes succeeded");
			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, deletes[loser].Error.ErrorCode);

			Assert.IsNull(inserts[winner].Error, "Winner's conditional failed: " + inserts[winner].Error?.Message);
			Assert.AreEqual(invIds[winner], inserts[winner].Value.AsString, "Conditional reply was matched to the wrong action");
			Assert.IsNotNull(inserts[loser].Error, "Loser's conditional ran");
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, inserts[loser].Error.ErrorCode);

			Assert.IsNotNull(await Pump(connA.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, invIds[winner]), AllConnections()));
			Assert.IsNull(await Pump(connA.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, invIds[loser]), AllConnections()),
				"The item was duplicated into the loser's inventory");
		}

		/// <summary>Drop: create in the world with a conditional "remove from my inventory", over the wire. The
		/// create's callback must fire first, then the conditional's with its own result.</summary>
		[Test, Category("Transport"), Category("Conditional")]
		public async Task ConditionalCreate_RemovesInventoryRow()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			var channelName = Name("drop");
			var channel = await Pump(conn.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), conn);

			var bagId = Name("bag");
			await Pump(conn.InsertDocumentAsync(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = bagId, ["item"] = "gem" }), conn);

			var log = new List<string>();
			var createProbe = new CallbackProbe<IntegrationTestEntity>(log, "create");
			var removeProbe = new CallbackProbe<bool>(log, "remove");

			var dropped = new IntegrationTestEntity();
			dropped.DisplayName.Set("gem");
			conn.EntityManager.CreateObject(dropped, channel, false, createProbe.OnComplete,
				new DeleteDocumentAction(IntegrationTestCollections.ITEMS, bagId, removeProbe.OnComplete));

			await PumpUntil(() => createProbe.Fired && removeProbe.Fired, TimeSpan.FromSeconds(5), conn);

			Assert.IsNull(createProbe.Error, "Create failed: " + createProbe.Error?.Message);
			Assert.IsNull(removeProbe.Error, "Conditional failed: " + removeProbe.Error?.Message);
			Assert.IsTrue(removeProbe.Value, "Conditional delete should have found the inventory row");
			CollectionAssert.AreEqual(new[] { "create", "remove" }, log);

			Assert.IsNull(await Pump(conn.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, bagId), conn));
		}

		/// <summary>A skipped compound conditional replies with an error and no results list. Over a remote leg the
		/// reply must still deserialize and reach the callback, rather than being dropped (and never timing out).</summary>
		[Test, Category("Transport"), Category("Conditional")]
		public async Task ConditionalCompound_SkippedStillReplies()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			var compoundProbe = new CallbackProbe<List<ActionResult>>();
			var compound = new CompoundDatabaseAction(new GameStateActionBase[]
			{
				new InsertDocumentAction(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = Name("never") })
			}, compoundProbe.OnComplete);

			// No such entity: the delete fails, so the compound is skipped.
			var deleteProbe = new CallbackProbe<bool>();
			conn.DeleteEntity(uint.MaxValue - 1, null, deleteProbe.OnComplete, compound);

			await PumpUntil(() => deleteProbe.Fired && compoundProbe.Fired, TimeSpan.FromSeconds(5), conn);

			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, deleteProbe.Code);
			Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, compoundProbe.Code);
			Assert.IsNotNull(compoundProbe.Value, "A skipped compound should hand its callback an empty list, not null");
			Assert.AreEqual(0, compoundProbe.Value.Count);
		}

		/// <summary>Two replicated actions on one exclusive update travel as a single compound. Over a remote leg each
		/// action's typed result must survive the compound round trip and reach its own callback.</summary>
		[Test, Category("Transport"), Category("Conditional")]
		public async Task ReplicatedActions_CombinedOnExclusiveUpdate()
		{
			await EnsureServerAsync();
			var conn = await OpenConnectionAsync();

			var channel = await Pump(conn.EntityManager.SubscribeToChannelAsync(Name("chest"), new IntegrationTestChannel()), conn);
			var chest = new IntegrationTestEntity();
			await Pump(conn.EntityManager.CreateObjectAsync(chest, channel, false), conn);

			var ids = new[] { Name("r1"), Name("r2") };
			var probes = new[] { new CallbackProbe<BsonValue>(), new CallbackProbe<BsonValue>() };

			chest.AddReplicatedAction(new InsertDocumentAction(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = ids[0] }, probes[0].OnComplete));
			chest.DisplayName.Set("gem");
			bool updateDone = false;
			ImpunityErrorResponse updateErr = null;
			chest.UpdateExclusive(err => { updateDone = true; updateErr = err; },
				new InsertDocumentAction(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = ids[1] }, probes[1].OnComplete));

			await PumpUntil(() => updateDone && probes[0].Fired && probes[1].Fired, TimeSpan.FromSeconds(5), conn);

			Assert.IsNull(updateErr, "Exclusive update failed: " + updateErr?.Message);
			for (int i = 0; i < 2; i++)
			{
				Assert.IsNull(probes[i].Error, "Action " + i + " failed: " + probes[i].Error?.Message);
				Assert.AreEqual(ids[i], probes[i].Value.AsString, "Action " + i + " got the wrong result");
			}
		}

		/// <summary>An exclusive update rejected by another client's lock skips its combined actions; over a remote leg
		/// the skip must still fan out to every action's callback.</summary>
		[Test, Category("Transport"), Category("Conditional")]
		public async Task ReplicatedActions_RejectedUpdateSkipsAll()
		{
			await EnsureServerAsync();
			var connA = await OpenConnectionAsync();
			var connB = await OpenConnectionAsync();

			var channelName = Name("chest");
			var aChannel = await Pump(connA.EntityManager.SubscribeToChannelAsync(channelName, new IntegrationTestChannel()), AllConnections());
			var bChannel = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>(channelName, null), AllConnections());

			var chest = new IntegrationTestEntity();
			await Pump(connA.EntityManager.CreateObjectAsync(chest, aChannel, false), AllConnections());
			await PumpUntil(() => bChannel.DistributedObjects.Count == 1, TimeSpan.FromSeconds(3), AllConnections());
			IntegrationTestEntity bChest = null;
			foreach (var obj in bChannel.DistributedObjects.Values)
			{
				bChest = (IntegrationTestEntity)obj;
			}
			Assert.IsTrue(await Pump(bChest.TryLockAsync(), AllConnections()), "B should take the lock");

			var ids = new[] { Name("s1"), Name("s2") };
			var probes = new[] { new CallbackProbe<BsonValue>(), new CallbackProbe<BsonValue>() };
			chest.AddReplicatedAction(new InsertDocumentAction(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = ids[0] }, probes[0].OnComplete));
			chest.DisplayName.Set("gem");
			bool updateDone = false;
			ImpunityErrorResponse updateErr = null;
			chest.UpdateExclusive(err => { updateDone = true; updateErr = err; },
				new InsertDocumentAction(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = ids[1] }, probes[1].OnComplete));

			await PumpUntil(() => updateDone && probes[0].Fired && probes[1].Fired, TimeSpan.FromSeconds(5), AllConnections());

			Assert.IsNotNull(updateErr, "The update should be rejected while B holds the lock");
			Assert.AreEqual(ImpunityErrorCode.ActionBlockedByLock, updateErr.ErrorCode);
			for (int i = 0; i < 2; i++)
			{
				Assert.IsNotNull(probes[i].Error, "Action " + i + " ran on a rejected update");
				Assert.AreEqual(ImpunityErrorCode.ActionConditionNotMet, probes[i].Error.ErrorCode);
				Assert.IsNull(await Pump(connA.FindDocumentByIdAsync(IntegrationTestCollections.ITEMS, ids[i]), AllConnections()));
			}
		}
	}

	// ───────── In-proc legs (also run inside Unity) ─────────

	public class TransportSuite_Local : TransportSuite
	{
		protected override Task EnsureServerAsync()
		{
			if (GameServer == null)
			{
				CreateServer();
			}
			return Task.CompletedTask;
		}

		protected override async Task<BaseGameConnection> OpenConnectionAsync()
		{
			var conn = Track(new LocalGameConnection(GameServer, Format));
			// Every LocalGameConnection shares the hard-coded "local_key" ConnectionKey, so the server
			// treats them as the SAME client for lock ownership (a second local client can re-enter the
			// first one's locks). Give each test client its own identity so the contention tests
			// exercise real cross-client behavior.
			conn.ConnectionKey = Name("local_key");
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected, "Local connection failed");
			return conn;
		}
	}

	public class TransportSuite_EmbeddedTcp : TransportSuite
	{
		protected override async Task EnsureServerAsync()
		{
			if (GameServer == null)
			{
				CreateServer();
				StartTcpServer();
				await Task.Delay(100);
			}
		}

		protected override async Task<BaseGameConnection> OpenConnectionAsync()
		{
			var conn = Track(RemoteGameConnection.MakeTCPRemoteConnection(
				TcpServer.TCPEndpoint, "test", null, Format, Options));
			conn.OnNetworkError = (err) => TestEnv.LogError("Network error: " + err.Message);
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected, "TCP connection failed");
			return conn;
		}
	}
}
