// ───────── Migration Tests ─────────
//
// Ported from Assets/Tests/PlayMode/ImpunityMigrationTests.cs onto the async API + harness.
//
// These exercise the data-migration flow (see docs/guides/SchemaMigration.md): a higher-version client
// connecting to an older world is OFFERED a migration (non-destructively), then explicitly runs it,
// commits, declines, or aborts. Rollback/recovery run on server threads against the real filesystem, so
// those tests keep their settle delays and are tagged Slow.
#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class MigrationTests : ImpunityTestHarness
	{
		GameStateFormat FormatV2;

		const string MigrationBackupFile = "Game.db.migration.bak";
		const string MigrationMarkerFile = "migration.dat";

		string BackupPath { get { return Path.Combine(GameStatePath, MigrationBackupFile); } }
		string MarkerPath { get { return Path.Combine(GameStatePath, MigrationMarkerFile); } }

		protected override void ConfigureOptions(ImpunityOptions options)
		{
			options.RemoteUpgradeAllowed = false;
		}

		// v1 is the harness Format — CreateServer() stamps the world at schema version 1.
		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = MigTestCollections.ITEMS, Name = MigTestCollections.ITEMS_NAME }
				},
				MigEntityTypes());
		}

		static Type[] MigEntityTypes()
		{
			return new Type[] { typeof(MigTestChannel), typeof(MigTestObject) };
		}

		[SetUp]
		public void MigrationSetUp()
		{
			FormatV2 = new GameStateFormat(
				2,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = MigTestCollections.ITEMS, Name = MigTestCollections.ITEMS_NAME },
					new GameStateCollection { Index = MigTestCollections.PLAYERS, Name = MigTestCollections.PLAYERS_NAME }
				},
				MigEntityTypes());
		}

		// ───────── Helpers ─────────

		void CreateServerV1() => CreateServer("migtest");

		LocalGameConnection NewLocal(GameStateFormat format)
		{
			return Track(new LocalGameConnection(GameServer, format));
		}

		async Task Connect(LocalGameConnection conn)
		{
			await Pump(conn.ConnectAsync(), conn);
			Assert.IsTrue(conn.Connected, "Connection failed");
		}

		// Waits until the world reaches the given migration phase. Useful after a disconnect where the
		// abort/restore runs on the server's own threads with no client to tick.
		async Task WaitForPhase(MigrationPhase phase)
		{
			await PollUntil(() => GameServer.GetMigrationPhase() == phase, TimeSpan.FromSeconds(5));
			Assert.AreEqual(phase, GameServer.GetMigrationPhase(), "World did not reach expected migration phase");
		}

		// Inserts a document into Items via a temporary v1 connection, then disconnects it.
		async Task SeedItem(BsonDocument doc)
		{
			var seeder = NewLocal(Format);
			await Connect(seeder);
			await Pump(seeder.InsertDocumentAsync(MigTestCollections.ITEMS, doc), seeder);
			DisposeConnection(seeder);
			// Let the disconnect settle so the world is empty before the next connect.
			await Task.Delay(200);
		}

		// ═══════════════════════════════════════════════════════════
		// 1. Offer is non-destructive
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration")]
		public async Task OfferIsNonDestructive()
		{
			CreateServerV1();
			await SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

			var clientB = NewLocal(FormatV2);
			await Connect(clientB);

			Assert.IsNotNull(clientB.PendingMigration, "Higher-version client should be offered a migration");
			Assert.AreEqual(1, clientB.PendingMigration.FromVersion);
			Assert.AreEqual(2, clientB.PendingMigration.ToVersion);
			Assert.AreEqual(MigrationPhase.Offered, GameServer.GetMigrationPhase());

			// Nothing destructive has happened: no snapshot, no marker, version unchanged.
			Assert.IsFalse(File.Exists(BackupPath), "No backup should exist while only offered");
			Assert.IsFalse(File.Exists(MarkerPath), "No marker should exist while only offered");
			Assert.AreEqual(1, GameServer.GetGameMetadata().Version);
		}

		// ═══════════════════════════════════════════════════════════
		// 2. Happy path — migrate documents and commit
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration")]
		public async Task MigrateDocumentsHappyPath()
		{
			CreateServerV1();
			await SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

			var clientB = NewLocal(FormatV2);
			await Connect(clientB);
			Assert.IsNotNull(clientB.PendingMigration);

			Task migration = clientB.RunMigrationAsync(async ctx =>
			{
				List<BsonDocument> items = await ctx.ListAsync(MigTestCollections.ITEMS_NAME);
				foreach (BsonDocument item in items)
				{
					item["power"] = item["power"].AsInt32 * 2;
					item["migrated"] = true;
					await ctx.UpsertAsync(MigTestCollections.ITEMS_NAME, item);
				}
				await ctx.InsertAsync(MigTestCollections.PLAYERS_NAME, new BsonDocument { ["_id"] = "p1", ["name"] = "Alice" });
			});
			await Pump(migration, TimeSpan.FromSeconds(15), clientB);

			Assert.AreEqual(MigrationPhase.None, GameServer.GetMigrationPhase());
			Assert.AreEqual(2, GameServer.GetGameMetadata().Version, "Version should be stamped to 2 after commit");
			Assert.IsFalse(File.Exists(BackupPath), "Backup should be discarded after commit");
			Assert.IsFalse(File.Exists(MarkerPath), "Marker should be discarded after commit");

			// The connection is now a normal v2 client; verify the transformed data.
			var found = await Pump(clientB.FindDocumentByIdAsync(MigTestCollections.ITEMS, "sword"), clientB);
			Assert.IsNotNull(found);
			Assert.AreEqual(20, found["power"].AsInt32);
			Assert.IsTrue(found["migrated"].AsBoolean);

			var players = await Pump(clientB.ListDocumentsAsync(MigTestCollections.PLAYERS), clientB);
			Assert.AreEqual(1, players.Count);
		}

		// ═══════════════════════════════════════════════════════════
		// 3. Decline reopens the world untouched
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration")]
		public async Task DeclineReopensWorld()
		{
			CreateServerV1();
			await SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

			var clientA = NewLocal(FormatV2);
			await Connect(clientA);
			Assert.IsNotNull(clientA.PendingMigration);

			await Pump(clientA.DeclineMigrationAsync(), TimeSpan.FromSeconds(15), clientA);
			Assert.AreEqual(MigrationPhase.None, GameServer.GetMigrationPhase());
			Assert.IsNull(clientA.PendingMigration);

			DisposeConnection(clientA);
			await Task.Delay(200);

			// World is still v1; a v1 client connects normally with no migration.
			var clientB = NewLocal(Format);
			await Connect(clientB);
			Assert.IsNull(clientB.PendingMigration);
			Assert.AreEqual(1, GameServer.GetGameMetadata().Version);
		}

		// ═══════════════════════════════════════════════════════════
		// 4. Other connections are locked out during an offer
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration")]
		public async Task OtherConnectionLockedOut()
		{
			CreateServerV1();

			var clientA = NewLocal(FormatV2);
			await Connect(clientA);
			Assert.AreEqual(MigrationPhase.Offered, GameServer.GetMigrationPhase());

			var clientB = NewLocal(Format);
			var error = await PumpExpectingError(clientB.ConnectAsync(), clientB);

			Assert.IsFalse(clientB.Connected, "Second client must not connect during a migration");
			Assert.IsNotNull(error, "Second client should be rejected");
			Assert.AreEqual(ImpunityErrorCode.ServerMigrationInProgress, error.ErrorId);
		}

		// ═══════════════════════════════════════════════════════════
		// 5. Disconnect mid-migration rolls back
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration"), Category("Slow")]
		public async Task DisconnectMidMigrationRollsBack()
		{
			CreateServerV1();
			await SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

			var clientA = NewLocal(FormatV2);
			await Connect(clientA);
			Assert.IsNotNull(clientA.PendingMigration);

			// Begin migrating and make a change, but do NOT commit.
			await Pump(clientA.BeginMigrationAsync(), TimeSpan.FromSeconds(15), clientA);
			Assert.AreEqual(MigrationPhase.Migrating, GameServer.GetMigrationPhase());
			Assert.IsTrue(File.Exists(BackupPath), "Snapshot should exist once migrating");

			var ctx = new MigrationContext(clientA, 1, 2);
			await Pump(ctx.UpsertAsync(MigTestCollections.ITEMS_NAME, new BsonDocument { ["_id"] = "sword", ["power"] = 999 }), TimeSpan.FromSeconds(15), clientA);

			// Simulate a crash of the migrating client: drop the connection without committing.
			DisposeConnection(clientA);

			// The server should roll the world back and reopen.
			await WaitForPhase(MigrationPhase.None);
			Assert.IsFalse(File.Exists(BackupPath), "Snapshot should be cleared after rollback");
			Assert.IsFalse(File.Exists(MarkerPath), "Marker should be cleared after rollback");
			Assert.AreEqual(1, GameServer.GetGameMetadata().Version);

			// The change must be gone.
			var clientB = NewLocal(Format);
			await Connect(clientB);
			var found = await Pump(clientB.FindDocumentByIdAsync(MigTestCollections.ITEMS, "sword"), clientB);
			Assert.AreEqual(10, found["power"].AsInt32, "Uncommitted change should have been rolled back");
		}

		// ═══════════════════════════════════════════════════════════
		// 6. Crash recovery restores an interrupted migration on restart
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration"), Category("Slow")]
		public async Task CrashRecoveryRestoresInterruptedMigration()
		{
			CreateServerV1();
			await SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

			var clientA = NewLocal(FormatV2);
			await Connect(clientA);

			await Pump(clientA.BeginMigrationAsync(), TimeSpan.FromSeconds(15), clientA);
			Assert.IsTrue(File.Exists(BackupPath));
			Assert.IsTrue(File.Exists(MarkerPath));

			var ctx = new MigrationContext(clientA, 1, 2);
			await Pump(ctx.UpsertAsync(MigTestCollections.ITEMS_NAME, new BsonDocument { ["_id"] = "sword", ["power"] = 999 }), TimeSpan.FromSeconds(15), clientA);

			// Simulate a hard crash: tear the server down mid-migration WITHOUT a graceful client disconnect. (Disposing
			// the client would trigger the ephemeral-lock abort, which rolls back cleanly — the opposite of a crash. We
			// abandon the client instead so the marker + backup are left behind for crash recovery to find.)
			AbandonConnection(clientA);
			GameServer.Dispose();
			GameServer = null;

			// Reopen the world: recovery should restore the pre-migration snapshot before serving anyone.
			GameServer = GameStateServer.Open("migtest", null, GameStatePath, Options);
			Assert.IsFalse(File.Exists(BackupPath), "Recovery should clear the snapshot");
			Assert.IsFalse(File.Exists(MarkerPath), "Recovery should clear the marker");
			Assert.AreEqual(1, GameServer.GetGameMetadata().Version, "World should be back at v1 after recovery");

			var clientB = NewLocal(Format);
			await Connect(clientB);
			var found = await Pump(clientB.FindDocumentByIdAsync(MigTestCollections.ITEMS, "sword"), clientB);
			Assert.AreEqual(10, found["power"].AsInt32, "Interrupted change should have been rolled back on restart");
		}

		// ═══════════════════════════════════════════════════════════
		// 7. Remote client not eligible without RemoteUpgradeAllowed
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration")]
		public async Task RemoteClientNotEligibleWithoutRemoteUpgrade()
		{
			CreateServerV1();

			StartTcpServer(); // RemoteUpgradeAllowed = false
			await Task.Delay(100);

			var remote = Track(RemoteGameConnection.MakeTCPRemoteConnection(TcpServer.TCPEndpoint, "migtest", null, FormatV2, Options));
			var error = await PumpExpectingError(remote.ConnectAsync(), remote);

			Assert.IsFalse(remote.Connected, "A remote client must not be offered migration when RemoteUpgradeAllowed is false");
			Assert.IsNotNull(error);
			Assert.AreEqual(ImpunityErrorCode.ServerVersionIncompatible, error.ErrorId);
		}

		// ═══════════════════════════════════════════════════════════
		// 8. Migrate a persisted live entity
		// ═══════════════════════════════════════════════════════════

		[Test, Category("Migration"), Category("Slow")]
		public async Task MigratePersistedLiveEntity()
		{
			CreateServerV1();

			// Seed a persisted channel with a persisted object (score = 10) via a v1 client.
			var seeder = NewLocal(Format);
			await Connect(seeder);

			var chan = await Pump(seeder.EntityManager.SubscribeToChannelAsync("zone1", new MigTestChannel { IsPersisted = true }), seeder);

			var obj = new MigTestObject { IsPersisted = true, UniqueName = "hero" };
			await Pump(seeder.EntityManager.CreateObjectAsync(obj, chan, false), seeder);

			obj.Score.Set(10);
			// Tick a few frames to flush the update and let the server persist it.
			await PumpFor(TimeSpan.FromSeconds(0.5), seeder);

			DisposeConnection(seeder);
			await Task.Delay(300);

			// Restart the server so its live state is empty and the persisted entity lives only in the database. This
			// mirrors the real migration scenario (a new build opening an existing save): migration rewrites the DB, and
			// nothing is cached in memory to shadow it. (Channels stay loaded for a process's lifetime, so without a restart
			// the in-memory copy from the seeder's session would mask the migrated value.)
			GameServer.Dispose();
			await Task.Delay(100);
			GameServer = GameStateServer.Open("migtest", null, GameStatePath, Options);

			// Migrate: bump the persisted "score" of the hero object from 10 to 25.
			var clientB = NewLocal(FormatV2);
			await Connect(clientB);
			Assert.IsNotNull(clientB.PendingMigration);

			int observedScore = -1;
			Task migration = clientB.RunMigrationAsync(async ctx =>
			{
				List<MigrationEntityRow> rows = await ctx.ScanEntitiesAsync();
				MigrationEntityRow hero = rows.Find(r => r.EntityId == "hero");
				if (hero == null)
				{
					throw new Exception("hero entity row not found in migration scan");
				}
				observedScore = hero.Properties["score"].AsInt32;
				hero.Properties["score"] = 25;
				await ctx.WriteEntityAsync(hero);
			});
			await Pump(migration, TimeSpan.FromSeconds(15), clientB);

			Assert.AreEqual(10, observedScore, "Migration should have read the original persisted score");
			Assert.AreEqual(2, GameServer.GetGameMetadata().Version);

			// Reload the persisted channel through the now-normal v2 connection and verify the new score.
			var chan2 = await Pump(clientB.EntityManager.SubscribeToChannelAsync<MigTestChannel>("zone1", null), clientB);

			await PumpUntil(() => chan2.DistributedObjects.Count > 0, TimeSpan.FromSeconds(3), clientB);

			MigTestObject reloaded = null;
			foreach (var kv in chan2.DistributedObjects)
			{
				if (kv.Value is MigTestObject m)
				{
					reloaded = m;
					break;
				}
			}
			Assert.IsNotNull(reloaded, "Persisted object should reload after migration");
			Assert.AreEqual(25, reloaded.Score.Get(), "Migrated persisted score should be 25");
		}
	}
}
