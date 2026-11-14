// ───────── Live-state inspection ─────────
//
// Covers the read-only inspection queries on BaseGameConnection (DescribeChannels, DescribeChannel, GetEntitySnapshot,
// DescribeConnections) and the client half in ClientEntityManager (GetFieldsAsBson, DecodeSnapshot, CompareWithSnapshot,
// CheckSync, BsonDiff): what each query reports, decoding a snapshot's opaque custom fields with the client's registry,
// and how a sync check tells a desync apart from a change in flight either way. Also the AllowStateInspection switch and
// the TCP round trip of every result type.
//
// Making a client stale: local connections hand every action to the in-proc server at once, but a client only sees
// what the server pushed it when the test pumps that client. So A writes, only A is pumped, and B lags behind.
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
	public class InspectionTests : ImpunityTestHarness
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
					typeof(IntegrationTestChannel),
					typeof(BsonTestEntity),
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

		async Task<LocalGameConnection> ConnectClient(string key)
		{
			var conn = Track(new LocalGameConnection(GameServer, Format));
			conn.ConnectionKey = key;
			await Pump(conn.ConnectAsync(), AllConnections());
			Assert.IsTrue(conn.Connected, "Local connection failed");
			return conn;
		}

		/// <summary>Two distinct clients on one channel, and one entity (Health 1, "tree") that both have.</summary>
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

		static FieldSyncResult Field(EntitySyncReport report, string name)
		{
			FieldSyncResult field = report.Fields.FirstOrDefault(f => f.Name == name);
			Assert.IsNotNull(field, "No field " + name + " in the report");
			return field;
		}

		// ───────── Server queries ─────────

		/// <summary>Every live channel, sorted by name, with its type, subscribers and object count; a prefix narrows it.</summary>
		[Test, Category("Inspection")]
		public async Task DescribeChannels_ReportsTypeSubscribersAndObjects()
		{
			var w = await Setup("insp_chan");
			await Pump(w.ConnA.EntityManager.CreateObjectAsync(new IntegrationTestEntity(), w.ChannelA, false), AllConnections());
			await Pump(w.ConnA.EntityManager.SubscribeToChannelAsync("other", new IntegrationTestChannel()), AllConnections());

			List<ChannelInfo> all = await Pump(w.ConnB.DescribeChannels(), AllConnections());
			CollectionAssert.AreEqual(new[] { "insp_chan", "other" }, all.Select(c => c.Name).ToArray());

			ChannelInfo chan = all[0];
			Assert.AreEqual(w.ChannelA.DistributedEntityId, chan.Id);
			Assert.AreEqual(IntegrationTestTypes.CHANNEL, chan.TypeId);
			Assert.AreEqual(nameof(IntegrationTestChannel), chan.TypeName);
			Assert.AreEqual(2, chan.ObjectCount);
			CollectionAssert.AreEquivalent(new[] { w.ConnA.ConnectionId, w.ConnB.ConnectionId }, chan.Subscribers);
			Assert.AreEqual(0, chan.IdleSinceMs, "A channel with subscribers isn't idle");
			Assert.IsNull(chan.LockedBy);
			Assert.IsFalse(chan.IsPersisted);

			List<ChannelInfo> narrowed = await Pump(w.ConnB.DescribeChannels("oth"), AllConnections());
			Assert.AreEqual(1, narrowed.Count);
			Assert.AreEqual("other", narrowed[0].Name);
			CollectionAssert.AreEqual(new[] { w.ConnA.ConnectionId }, narrowed[0].Subscribers);
		}

		/// <summary>A channel's objects with their unique names and lock holders — also for a client that isn't subscribed.</summary>
		[Test, Category("Inspection")]
		public async Task DescribeChannel_ReportsObjectsNamesAndLocks()
		{
			var w = await Setup("insp_contents");
			var named = new IntegrationTestEntity { UniqueName = "go_3_4" };
			await Pump(w.ConnA.EntityManager.CreateObjectAsync(named, w.ChannelA, false), AllConnections());
			Assert.IsTrue(await Pump(w.B.TryLockAsync(), AllConnections()), "B couldn't lock the entity");

			var outsider = await ConnectClient("insp_outsider");
			ChannelContents contents = await Pump(outsider.DescribeChannel("insp_contents"), AllConnections());

			Assert.AreEqual("insp_contents", contents.Channel.Name);
			Assert.AreEqual(2, contents.Objects.Count);
			Assert.AreEqual(w.A.DistributedEntityId, contents.Objects[0].Id, "Objects are by ascending id");

			EntityInfo locked = contents.Objects[0];
			Assert.AreEqual(nameof(IntegrationTestEntity), locked.TypeName);
			Assert.AreEqual(w.ConnB.ConnectionId, locked.LockedBy);
			Assert.IsFalse(locked.IsChannel);
			Assert.IsNull(locked.Name);

			EntityInfo unique = contents.Objects[1];
			Assert.AreEqual("go_3_4", unique.Name);
			Assert.IsNull(unique.LockedBy);
		}

		/// <summary>An unknown channel is not found — and isn't created or loaded by being asked about.</summary>
		[Test, Category("Inspection")]
		public async Task DescribeChannel_UnknownChannel_IsNotFound()
		{
			CreateServer();
			await ConnectLocal();
			var e = await PumpExpectingError(LocalGame.DescribeChannel("nowhere"), LocalGame);
			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, e.ErrorId);
			Assert.AreEqual(0, GameServer.Live.LiveChannelCount, "Asking about a channel loaded it");
		}

		/// <summary>An id that isn't a live entity is not found.</summary>
		[Test, Category("Inspection")]
		public async Task GetEntitySnapshot_UnknownId_IsNotFound()
		{
			CreateServer();
			await ConnectLocal();
			var e = await PumpExpectingError(LocalGame.GetEntitySnapshot(12345), LocalGame);
			Assert.AreEqual(ImpunityErrorCode.ActionNotFound, e.ErrorId);
		}

		/// <summary>Each connection with its subscriptions and the locks it holds.</summary>
		[Test, Category("Inspection")]
		public async Task DescribeConnections_ReportsSubscriptionsAndLocks()
		{
			var w = await Setup("insp_conns");
			Assert.IsTrue(await Pump(w.A.TryLockAsync(), AllConnections()));

			List<ConnectionInfo> conns = await Pump(w.ConnB.DescribeConnections(), AllConnections());
			Assert.AreEqual(2, conns.Count);
			ConnectionInfo a = conns.Single(c => c.Id == w.ConnA.ConnectionId);
			ConnectionInfo b = conns.Single(c => c.Id == w.ConnB.ConnectionId);
			Assert.IsFalse(a.IsRemote);
			CollectionAssert.AreEqual(new[] { "insp_conns" }, a.Subscriptions);
			CollectionAssert.AreEqual(new[] { w.A.DistributedEntityId }, a.LocksHeld);
			CollectionAssert.IsEmpty(b.LocksHeld);
		}

		/// <summary>With AllowStateInspection off every query is refused; the old name listing still works.</summary>
		[Test, Category("Inspection")]
		public async Task InspectionTurnedOff_RefusesEveryQuery()
		{
			Options.AllowStateInspection = false;                          // Options is rebuilt for every test
			CreateServer();
			await ConnectLocal();
			await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("closed", new IntegrationTestChannel()), LocalGame);

			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, (await PumpExpectingError(LocalGame.DescribeChannels(), LocalGame)).ErrorId);
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, (await PumpExpectingError(LocalGame.DescribeChannel("closed"), LocalGame)).ErrorId);
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, (await PumpExpectingError(LocalGame.GetEntitySnapshot(1), LocalGame)).ErrorId);
			Assert.AreEqual(ImpunityErrorCode.ActionBadRequest, (await PumpExpectingError(LocalGame.DescribeConnections(), LocalGame)).ErrorId);
			CollectionAssert.Contains(await Pump(LocalGame.ListActiveChannels(), LocalGame), "closed");
		}

		// ───────── Snapshots and decoding ─────────

		/// <summary>A snapshot decodes, with the client's registry, to exactly what a subscriber holds — custom-typed
		/// fields (opaque bytes on the server) and collections included — and names its fields' last writer.</summary>
		[Test, Category("Inspection")]
		public async Task Snapshot_DecodesToTheSubscribersValues()
		{
			CreateServer();
			var connA = await ConnectClient("snap_a");
			var connB = await ConnectClient("snap_b");
			var chanA = await Pump(connA.EntityManager.SubscribeToChannelAsync("snap", new IntegrationTestChannel()), AllConnections());
			var chanB = await Pump(connB.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("snap", null), AllConnections());

			var entity = new BsonTestEntity();
			entity.Name.Set("chest");
			entity.Data.Set(new BsonTestPoco { Label = "gold", Number = 7 });
			entity.Position.Set(new TestVec3(1, 2, 3));
			entity.Scores.Init(3);
			entity.Scores.Set(1, 5);
			entity.Tags.Init();
			entity.Tags.Add("colour", "red");
			await Pump(connA.EntityManager.CreateObjectAsync(entity, chanA, false), AllConnections());
			await PumpUntil(() => chanB.DistributedObjects.Count == 1, AllConnections());

			entity.Count.Set(42);
			await PumpUntil(() => ((BsonTestEntity)chanB.DistributedObjects.Values.First()).Count.Get() == 42, AllConnections());
			var seenByB = (BsonTestEntity)chanB.DistributedObjects.Values.First();

			EntitySnapshot snapshot = await Pump(connB.GetEntitySnapshot(entity.DistributedEntityId), AllConnections());
			Assert.AreEqual(BsonTestIds.ENTITY, snapshot.Info.TypeId);
			Assert.AreEqual("snap", snapshot.ChannelName);
			Assert.AreEqual(chanA.DistributedEntityId, snapshot.ChannelId);
			Assert.Greater(snapshot.Info.Seq, 0u);

			BsonDocument server = connB.EntityManager.DecodeSnapshot(snapshot);
			BsonDocument client = connB.EntityManager.GetFieldsAsBson(seenByB);
			Assert.AreEqual(JsonSerializer.Serialize(client), JsonSerializer.Serialize(server));
			Assert.AreEqual("gold", server["Data"]["Label"].AsString);
			Assert.AreEqual("chest", server["Name"].AsString);
			Assert.IsTrue(server.ContainsKey("Transient"), "Unpersisted fields are dumped too");

			FieldSnapshot count = snapshot.Fields.Single(f => f.Name == "Count");
			Assert.AreEqual(connA.ConnectionId, count.LastWriter);
			Assert.Greater(count.LastModSeq, 0u);
			Assert.Greater(count.ModifiedMs, 0L);

			var typed = (BsonTestEntity)connB.EntityManager.DecodeSnapshotEntity(snapshot);
			Assert.AreEqual(new TestVec3(1, 2, 3), typed.Position.Get());
			Assert.AreEqual(0u, typed.DistributedEntityId, "A decoded instance stays detached");
		}

		// ───────── Sync checks ─────────

		/// <summary>After the dust settles, both clients are in sync with the server, field for field.</summary>
		[Test, Category("Inspection")]
		public async Task CheckSync_AfterUpdatesSettle_EveryFieldMatches()
		{
			var w = await Setup("sync_settled");
			w.A.Health.Set(5);
			w.B.DisplayName.Set("oak");
			await PumpUntil(() => w.A.DisplayName.Get() == "oak" && w.B.Health.Get() == 5, AllConnections());

			foreach (var (conn, entity) in new[] { (w.ConnA, w.A), (w.ConnB, w.B) })
			{
				EntitySyncReport report = await Pump(conn.EntityManager.CheckSyncAsync(entity), AllConnections());
				Assert.IsTrue(report.IsSettled, string.Join(", ", report.Fields.Select(f => f.Name + "=" + f.State)));
				Assert.IsTrue(report.InSync);
			}
		}

		/// <summary>A client that hasn't received the server's latest change sees it as in flight, not as a desync.</summary>
		[Test, Category("Inspection")]
		public async Task CompareWithSnapshot_ClientBehindTheServer_IsInFlightToClient()
		{
			var w = await Setup("sync_behind");
			w.A.Health.Set(9);
			await PumpUntil(() => w.A.Health.Get() == 9, w.ConnA);          // B is not pumped: it still has 1
			EntitySnapshot snapshot = await Pump(w.ConnA.GetEntitySnapshot(w.A.DistributedEntityId), w.ConnA);

			EntitySyncReport report = w.ConnB.EntityManager.CompareWithSnapshot(w.B, snapshot);
			FieldSyncResult health = Field(report, "Health");
			Assert.AreEqual(FieldSyncState.InFlightToClient, health.State);
			Assert.AreEqual(1, health.ClientValue.AsInt32);
			Assert.AreEqual(9, health.ServerValue.AsInt32);
			Assert.IsTrue(report.InSync, "A change in flight isn't a desync");
			Assert.IsFalse(report.IsSettled);
		}

		/// <summary>A snapshot older than the client's copy shows the field as changed since the snapshot.</summary>
		[Test, Category("Inspection")]
		public async Task CompareWithSnapshot_SnapshotOlderThanTheClient_IsChangedSinceSnapshot()
		{
			var w = await Setup("sync_old_snapshot");
			EntitySnapshot old = await Pump(w.ConnB.GetEntitySnapshot(w.A.DistributedEntityId), AllConnections());
			w.A.Health.Set(3);
			await PumpUntil(() => w.B.Health.Get() == 3, AllConnections());

			EntitySyncReport report = w.ConnB.EntityManager.CompareWithSnapshot(w.B, old);
			Assert.AreEqual(FieldSyncState.ChangedSinceSnapshot, Field(report, "Health").State);
			Assert.AreEqual(FieldSyncState.Match, Field(report, "DisplayName").State);
		}

		/// <summary>A value changed behind replication's back is a real desync, with the difference spelled out.</summary>
		[Test, Category("Inspection")]
		public async Task CheckSync_LocalOnlyChange_IsAMismatch()
		{
			var w = await Setup("sync_desync");
			w.B.Health.SetLocalOnly(99);

			EntitySyncReport report = await Pump(w.ConnB.EntityManager.CheckSyncAsync(w.B), AllConnections());
			FieldSyncResult health = Field(report, "Health");
			Assert.AreEqual(FieldSyncState.Mismatch, health.State);
			CollectionAssert.AreEqual(new[] { "(value): 99 ≠ 1" }, health.Differences);
			Assert.IsFalse(report.InSync);
			Assert.AreEqual(1, report.Count(FieldSyncState.Mismatch));
		}

		/// <summary>The owner of a client-authoritative entity: an unsent change is in flight to the server, and once
		/// the server has it the field matches again.</summary>
		[Test, Category("Inspection")]
		public async Task CompareWithSnapshot_ClientAuthoritativeOwner_UnsentChangeIsInFlightToServer()
		{
			var w = await Setup("sync_owner", clientAuthoritative: true);
			w.A.Health.Set(6);                                               // applied locally at once, sent on A's next Update
			EntitySnapshot before = await Pump(w.ConnB.GetEntitySnapshot(w.A.DistributedEntityId), w.ConnB);
			Assert.AreEqual(FieldSyncState.InFlightToServer, Field(w.ConnA.EntityManager.CompareWithSnapshot(w.A, before), "Health").State);

			await PumpUntil(() => w.B.Health.Get() == 6, AllConnections());
			EntitySyncReport after = await Pump(w.ConnA.EntityManager.CheckSyncAsync(w.A), AllConnections());
			Assert.IsTrue(after.IsSettled, string.Join(", ", after.Fields.Select(f => f.Name + "=" + f.State)));

			w.A.Health.SetLocalOnly(13);
			EntitySyncReport desync = await Pump(w.ConnA.EntityManager.CheckSyncAsync(w.A), AllConnections());
			Assert.AreEqual(FieldSyncState.Mismatch, Field(desync, "Health").State, "The server had every update the owner sent");
		}

		/// <summary>A check on an entity this manager doesn't have fails cleanly.</summary>
		[Test, Category("Inspection")]
		public async Task CheckSync_UnregisteredEntity_Fails()
		{
			var w = await Setup("sync_unregistered");
			var e = await PumpExpectingError(w.ConnA.EntityManager.CheckSyncAsync(new IntegrationTestEntity()), AllConnections());
			Assert.AreEqual(ImpunityErrorCode.ActionInvalidParameter, e.ErrorId);
		}

		// ───────── Over the wire ─────────

		/// <summary>Every result type survives the TCP round trip, and a remote client's sync check works.</summary>
		[Test, Category("Inspection")]
		public async Task OverTcp_QueriesAndSyncCheckWork()
		{
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();
			var localChan = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("wire", new IntegrationTestChannel()), AllConnections());
			var remoteChan = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("wire", null), AllConnections());
			var entity = new IntegrationTestEntity();
			entity.Health.Set(8);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(entity, localChan, false), AllConnections());
			await PumpUntil(() => remoteChan.DistributedObjects.Count == 1, AllConnections());
			var remoteEntity = (IntegrationTestEntity)remoteChan.DistributedObjects.Values.First();

			List<ChannelInfo> channels = await Pump(RemoteGame.DescribeChannels(), AllConnections());
			Assert.AreEqual(2, channels.Single(c => c.Name == "wire").SubscriberCount);

			ChannelContents contents = await Pump(RemoteGame.DescribeChannel("wire"), AllConnections());
			Assert.AreEqual(entity.DistributedEntityId, contents.Objects.Single().Id);

			List<ConnectionInfo> conns = await Pump(RemoteGame.DescribeConnections(), AllConnections());
			Assert.IsTrue(conns.Single(c => c.Id == RemoteGame.ConnectionId).IsRemote);

			EntitySyncReport report = await Pump(RemoteGame.EntityManager.CheckSyncAsync(remoteEntity), AllConnections());
			Assert.IsTrue(report.IsSettled, string.Join(", ", report.Fields.Select(f => f.Name + "=" + f.State)));
			Assert.AreEqual(8, report.Fields.Single(f => f.Name == "Health").ServerValue.AsInt32);
		}

		// ───────── BsonDiff ─────────

		[Test, Category("Inspection")]
		public void BsonDiff_NamesEachDifferingPath()
		{
			var a = new BsonDocument
			{
				["hp"] = 3,
				["inv"] = new BsonArray { "axe", "seed" },
				["pos"] = new BsonDocument { ["x"] = 1, ["y"] = 2 },
				["mine"] = true,
			};
			var b = new BsonDocument
			{
				["hp"] = 3L,                                                 // same number, different BSON type
				["inv"] = new BsonArray { "axe", "hoe", "rope" },
				["pos"] = new BsonDocument { ["x"] = 1, ["y"] = 5 },
				["theirs"] = false,
			};

			var lines = new List<string>();
			BsonDiff.Collect(a, b, "", lines);
			CollectionAssert.AreEqual(new[]
			{
				"inv[1]: \"seed\" ≠ \"hoe\"",
				"inv: length 2 ≠ 3",
				"pos.y: 2 ≠ 5",
				"mine: only on the client side (true)",
				"theirs: only on the server side (false)",
			}, lines);
			Assert.IsTrue(BsonDiff.Same(new BsonDocument { ["n"] = 1 }, new BsonDocument { ["n"] = 1.0 }));
		}
	}
}
