using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;
using Impunity.Unity;

using UltraLiteDB;


// ───────── Migration test entity types (persisted) ─────────

[DistributedEntity(MigTestTypes.PCHANNEL, PersistAs = "mchan")]
public partial class MigTestChannel : DistributedChannelBase
{
	public enum Props : byte { LABEL = 1 }

	[Distributed((byte)Props.LABEL, PersistAs = "label")]
	public DistributedValue<string, StringSerializer> Label;
}

[DistributedEntity(MigTestTypes.POBJECT, PersistAs = "mobj")]
public partial class MigTestObject : DistributedObjectBase
{
	public enum Props : byte { SCORE = 1 }

	[Distributed((byte)Props.SCORE, PersistAs = "score")]
	public DistributedValue<int, Int32Serializer> Score;
}

static class MigTestTypes
{
	public const int PCHANNEL = 101;
	public const int POBJECT = 102;
}

static class MigTestCollections
{
	public const int ITEMS = 10;
	public const int PLAYERS = 11;
	public const string ITEMS_NAME = "Items";
	public const string PLAYERS_NAME = "Players";
}


// ───────── Migration Tests ─────────
//
// These exercise the data-migration flow (see docs/guides/SchemaMigration.md): a higher-version client connecting to an
// older world is OFFERED a migration (non-destructively), then explicitly runs it, commits, declines, or aborts.

public class ImpunityMigrationTests
{
	string GameStatePath;
	ImpunityOptions Options;

	GameStateFormat FormatV1;
	GameStateFormat FormatV2;
	GameStateEntityTypeDef[] EntityDefsV1;
	GameStateEntityTypeDef[] EntityDefsV2;

	GameStateServer GameServer;
	ImpunityServer TCPServer;
	LocalGameConnection ClientA;
	LocalGameConnection ClientB;

	const string MigrationBackupFile = "Game.db.migration.bak";
	const string MigrationMarkerFile = "migration.dat";

	[SetUp]
	public void SetUp()
	{
		BsonMapper.Global.IncludeFields = true;

		GameStatePath = Path.Combine(Application.temporaryCachePath, "ImpunityMigrationTest_" + Guid.NewGuid().ToString("N"));

		Options = new ImpunityOptions
		{
			GameTypeCode = "Test",
			ServerPort = 39655,
			RemoteUpgradeAllowed = false
		};

		Type[] entityTypes = new Type[] { typeof(MigTestChannel), typeof(MigTestObject) };

		FormatV1 = new GameStateFormat(
			1,
			new GameStateCollection[]
			{
				new GameStateCollection { Index = MigTestCollections.ITEMS, Name = MigTestCollections.ITEMS_NAME }
			},
			entityTypes);

		FormatV2 = new GameStateFormat(
			2,
			new GameStateCollection[]
			{
				new GameStateCollection { Index = MigTestCollections.ITEMS, Name = MigTestCollections.ITEMS_NAME },
				new GameStateCollection { Index = MigTestCollections.PLAYERS, Name = MigTestCollections.PLAYERS_NAME }
			},
			entityTypes);

		EntityDefsV1 = new ClientEntityManager().RegisterEntityTypes(FormatV1.EntityTypes);
		EntityDefsV2 = new ClientEntityManager().RegisterEntityTypes(FormatV2.EntityTypes);
	}

	[TearDown]
	public void TearDown()
	{
		ClientA?.Dispose();
		ClientA = null;
		ClientB?.Dispose();
		ClientB = null;
		TCPServer?.Dispose();
		TCPServer = null;
		GameServer?.Dispose();
		GameServer = null;

		try
		{
			if (Directory.Exists(GameStatePath))
			{
				Directory.Delete(GameStatePath, true);
			}
		}
		catch { }
	}

	// ───────── Helpers ─────────

	// Creates a fresh world already stamped at schema version 1.
	void CreateServerV1()
	{
		var summary = new BsonDocument { ["name"] = "MigrationTest" };
		GameServer = GameStateServer.Create("migtest", null, GameStatePath, summary, Options);
		GameServer.UpdateFormat(new GameStateFormatData(FormatV1, EntityDefsV1), false);
	}

	LocalGameConnection ConnectLocal(GameStateFormat format)
	{
		return new LocalGameConnection(GameServer, format);
	}

	IEnumerator Connect(LocalGameConnection conn)
	{
		yield return WaitForYield(conn.ConnectYield(), conn);
		Assert.IsTrue(conn.Connected, "Connection failed");
	}

	// Ticks the connection until the connect attempt resolves, returning the (possibly null) error without asserting.
	IEnumerator ConnectExpectingError(LocalGameConnection conn, Action<ImpunityErrorResponse> onResolved)
	{
		var yld = conn.ConnectYield();
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			conn.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Connect attempt timed out");
		onResolved(yld.Error);
	}

	IEnumerator WaitForYield(ImpunityYield yld, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Operation timed out");
		Assert.IsNull(yld.Error, "Operation failed: " + yld.Error?.Message);
	}

	IEnumerator WaitForYield<T>(ImpunityYield<T> yld, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Operation timed out");
		Assert.IsNull(yld.Error, "Operation failed: " + yld.Error?.Message);
	}

	// Drives connection Update() each frame until a Task completes, then surfaces any failure as a test failure.
	IEnumerator RunTask(Task task, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (!task.IsCompleted && elapsed < 15f)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsTrue(task.IsCompleted, "Task did not complete in time");
		if (task.IsFaulted)
		{
			Assert.Fail("Task failed: " + task.Exception);
		}
	}

	IEnumerator TickUntil(Func<bool> condition, float timeout, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (!condition() && elapsed < timeout)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsTrue(condition(), "Condition not met within timeout");
	}

	// Ticks the given connections for a fixed duration (no condition/assert). Used to let pending updates flush.
	IEnumerator TickFor(float seconds, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (elapsed < seconds)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
	}

	// Waits (ticking real time) until the world reaches the given migration phase. Useful after a disconnect where the
	// abort/restore runs on the server's own threads with no client to tick.
	IEnumerator WaitForPhase(MigrationPhase phase)
	{
		float elapsed = 0f;
		while (GameServer.GetMigrationPhase() != phase && elapsed < 5f)
		{
			yield return new WaitForSeconds(0.05f);
			elapsed += 0.05f;
		}
		Assert.AreEqual(phase, GameServer.GetMigrationPhase(), "World did not reach expected migration phase");
	}

	string BackupPath { get { return Path.Combine(GameStatePath, MigrationBackupFile); } }
	string MarkerPath { get { return Path.Combine(GameStatePath, MigrationMarkerFile); } }

	// Inserts a document into Items via a temporary v1 connection, then disconnects it.
	IEnumerator SeedItem(BsonDocument doc)
	{
		var seeder = ConnectLocal(FormatV1);
		yield return Connect(seeder);
		yield return WaitForYield(seeder.InsertDocumentYield(MigTestCollections.ITEMS, doc), seeder);
		seeder.Dispose();
		// Let the disconnect settle so the world is empty before the next connect.
		yield return new WaitForSeconds(0.2f);
	}

	// ═══════════════════════════════════════════════════════════
	// 1. Offer is non-destructive
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator OfferIsNonDestructive()
	{
		CreateServerV1();
		yield return SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

		ClientB = ConnectLocal(FormatV2);
		yield return Connect(ClientB);

		Assert.IsNotNull(ClientB.PendingMigration, "Higher-version client should be offered a migration");
		Assert.AreEqual(1, ClientB.PendingMigration.FromVersion);
		Assert.AreEqual(2, ClientB.PendingMigration.ToVersion);
		Assert.AreEqual(MigrationPhase.Offered, GameServer.GetMigrationPhase());

		// Nothing destructive has happened: no snapshot, no marker, version unchanged.
		Assert.IsFalse(File.Exists(BackupPath), "No backup should exist while only offered");
		Assert.IsFalse(File.Exists(MarkerPath), "No marker should exist while only offered");
		Assert.AreEqual(1, GameServer.GetGameMetadata().Version);
	}

	// ═══════════════════════════════════════════════════════════
	// 2. Happy path — migrate documents and commit
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator MigrateDocumentsHappyPath()
	{
		CreateServerV1();
		yield return SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

		ClientB = ConnectLocal(FormatV2);
		yield return Connect(ClientB);
		Assert.IsNotNull(ClientB.PendingMigration);

		Task migration = ClientB.RunMigrationAsync(async ctx =>
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
		yield return RunTask(migration, ClientB);

		Assert.AreEqual(MigrationPhase.None, GameServer.GetMigrationPhase());
		Assert.AreEqual(2, GameServer.GetGameMetadata().Version, "Version should be stamped to 2 after commit");
		Assert.IsFalse(File.Exists(BackupPath), "Backup should be discarded after commit");
		Assert.IsFalse(File.Exists(MarkerPath), "Marker should be discarded after commit");

		// The connection is now a normal v2 client; verify the transformed data.
		var find = ClientB.FindDocumentByIdYield(MigTestCollections.ITEMS, "sword");
		yield return WaitForYield(find, ClientB);
		Assert.IsNotNull(find.Value);
		Assert.AreEqual(20, find.Value["power"].AsInt32);
		Assert.IsTrue(find.Value["migrated"].AsBoolean);

		var players = ClientB.ListDocumentsYield(MigTestCollections.PLAYERS);
		yield return WaitForYield(players, ClientB);
		Assert.AreEqual(1, players.Value.Count);
	}

	// ═══════════════════════════════════════════════════════════
	// 3. Decline reopens the world untouched
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator DeclineReopensWorld()
	{
		CreateServerV1();
		yield return SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

		ClientA = ConnectLocal(FormatV2);
		yield return Connect(ClientA);
		Assert.IsNotNull(ClientA.PendingMigration);

		yield return RunTask(ClientA.DeclineMigrationAsync(), ClientA);
		Assert.AreEqual(MigrationPhase.None, GameServer.GetMigrationPhase());
		Assert.IsNull(ClientA.PendingMigration);

		ClientA.Dispose();
		ClientA = null;
		yield return new WaitForSeconds(0.2f);

		// World is still v1; a v1 client connects normally with no migration.
		ClientB = ConnectLocal(FormatV1);
		yield return Connect(ClientB);
		Assert.IsNull(ClientB.PendingMigration);
		Assert.AreEqual(1, GameServer.GetGameMetadata().Version);
	}

	// ═══════════════════════════════════════════════════════════
	// 4. Other connections are locked out during an offer
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator OtherConnectionLockedOut()
	{
		CreateServerV1();

		ClientA = ConnectLocal(FormatV2);
		yield return Connect(ClientA);
		Assert.AreEqual(MigrationPhase.Offered, GameServer.GetMigrationPhase());

		ClientB = ConnectLocal(FormatV1);
		ImpunityErrorResponse error = null;
		yield return ConnectExpectingError(ClientB, e => error = e);

		Assert.IsFalse(ClientB.Connected, "Second client must not connect during a migration");
		Assert.IsNotNull(error, "Second client should be rejected");
		Assert.AreEqual(ImpunityErrorCode.ServerMigrationInProgress, error.ErrorCode);
	}

	// ═══════════════════════════════════════════════════════════
	// 5. Disconnect mid-migration rolls back
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator DisconnectMidMigrationRollsBack()
	{
		CreateServerV1();
		yield return SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

		ClientA = ConnectLocal(FormatV2);
		yield return Connect(ClientA);
		Assert.IsNotNull(ClientA.PendingMigration);

		// Begin migrating and make a change, but do NOT commit.
		Task begin = ClientA.BeginMigrationAsync();
		yield return RunTask(begin, ClientA);
		Assert.AreEqual(MigrationPhase.Migrating, GameServer.GetMigrationPhase());
		Assert.IsTrue(File.Exists(BackupPath), "Snapshot should exist once migrating");

		var ctx = new MigrationContext(ClientA, 1, 2);
		Task change = ctx.UpsertAsync(MigTestCollections.ITEMS_NAME, new BsonDocument { ["_id"] = "sword", ["power"] = 999 });
		yield return RunTask(change, ClientA);

		// Simulate a crash of the migrating client: drop the connection without committing.
		ClientA.Dispose();
		ClientA = null;

		// The server should roll the world back and reopen.
		yield return WaitForPhase(MigrationPhase.None);
		Assert.IsFalse(File.Exists(BackupPath), "Snapshot should be cleared after rollback");
		Assert.IsFalse(File.Exists(MarkerPath), "Marker should be cleared after rollback");
		Assert.AreEqual(1, GameServer.GetGameMetadata().Version);

		// The change must be gone.
		ClientB = ConnectLocal(FormatV1);
		yield return Connect(ClientB);
		var find = ClientB.FindDocumentByIdYield(MigTestCollections.ITEMS, "sword");
		yield return WaitForYield(find, ClientB);
		Assert.AreEqual(10, find.Value["power"].AsInt32, "Uncommitted change should have been rolled back");
	}

	// ═══════════════════════════════════════════════════════════
	// 6. Crash recovery restores an interrupted migration on restart
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator CrashRecoveryRestoresInterruptedMigration()
	{
		CreateServerV1();
		yield return SeedItem(new BsonDocument { ["_id"] = "sword", ["power"] = 10 });

		ClientA = ConnectLocal(FormatV2);
		yield return Connect(ClientA);

		Task begin = ClientA.BeginMigrationAsync();
		yield return RunTask(begin, ClientA);
		Assert.IsTrue(File.Exists(BackupPath));
		Assert.IsTrue(File.Exists(MarkerPath));

		var ctx = new MigrationContext(ClientA, 1, 2);
		Task change = ctx.UpsertAsync(MigTestCollections.ITEMS_NAME, new BsonDocument { ["_id"] = "sword", ["power"] = 999 });
		yield return RunTask(change, ClientA);

		// Simulate a hard crash: tear the server down mid-migration WITHOUT a graceful client disconnect. (Disposing
		// the client would trigger the ephemeral-lock abort, which rolls back cleanly — the opposite of a crash. We
		// abandon the client instead so the marker + backup are left behind for crash recovery to find.)
		ClientA = null;
		GameServer.Dispose();
		GameServer = null;

		// Reopen the world: recovery should restore the pre-migration snapshot before serving anyone.
		GameServer = GameStateServer.Open("migtest", null, GameStatePath, Options);
		Assert.IsFalse(File.Exists(BackupPath), "Recovery should clear the snapshot");
		Assert.IsFalse(File.Exists(MarkerPath), "Recovery should clear the marker");
		Assert.AreEqual(1, GameServer.GetGameMetadata().Version, "World should be back at v1 after recovery");

		ClientB = ConnectLocal(FormatV1);
		yield return Connect(ClientB);
		var find = ClientB.FindDocumentByIdYield(MigTestCollections.ITEMS, "sword");
		yield return WaitForYield(find, ClientB);
		Assert.AreEqual(10, find.Value["power"].AsInt32, "Interrupted change should have been rolled back on restart");
	}

	// ═══════════════════════════════════════════════════════════
	// 7. Remote client not eligible without RemoteUpgradeAllowed
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator RemoteClientNotEligibleWithoutRemoteUpgrade()
	{
		CreateServerV1();

		TCPServer = new ImpunityServer(GameServer, Options); // RemoteUpgradeAllowed = false
		TCPServer.Start();
		yield return new WaitForSeconds(0.1f);

		var remote = RemoteGameConnection.MakeTCPRemoteConnection(TCPServer.TCPEndpoint, "migtest", null, FormatV2, Options);
		var yld = remote.ConnectYield();
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			remote.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Connect attempt timed out");
		Assert.IsFalse(remote.Connected, "A remote client must not be offered migration when RemoteUpgradeAllowed is false");
		Assert.IsNotNull(yld.Error);
		Assert.AreEqual(ImpunityErrorCode.ServerVersionIncompatible, yld.Error.ErrorCode);

		remote.Dispose();
	}

	// ═══════════════════════════════════════════════════════════
	// 8. Migrate a persisted live entity
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("Migration")]
	public IEnumerator MigratePersistedLiveEntity()
	{
		CreateServerV1();

		// Seed a persisted channel with a persisted object (score = 10) via a v1 client.
		var seeder = ConnectLocal(FormatV1);
		yield return Connect(seeder);

		var chanInit = new MigTestChannel { IsPersisted = true };
		var subYield = seeder.EntityManager.SubscribeToChannelYield("zone1", chanInit);
		yield return WaitForYield(subYield, seeder);

		var obj = new MigTestObject { IsPersisted = true, UniqueName = "hero" };
		var createYield = seeder.EntityManager.CreateObjectYield(obj, subYield.Value, false);
		yield return WaitForYield(createYield, seeder);

		obj.Score.Set(10);
		// Tick a few frames to flush the update and let the server persist it.
		yield return TickFor(0.5f, seeder);

		seeder.Dispose();
		yield return new WaitForSeconds(0.3f);

		// Restart the server so its live state is empty and the persisted entity lives only in the database. This
		// mirrors the real migration scenario (a new build opening an existing save): migration rewrites the DB, and
		// nothing is cached in memory to shadow it. (Channels stay loaded for a process's lifetime, so without a restart
		// the in-memory copy from the seeder's session would mask the migrated value.)
		GameServer.Dispose();
		yield return new WaitForSeconds(0.1f);
		GameServer = GameStateServer.Open("migtest", null, GameStatePath, Options);

		// Migrate: bump the persisted "score" of the hero object from 10 to 25.
		ClientB = ConnectLocal(FormatV2);
		yield return Connect(ClientB);
		Assert.IsNotNull(ClientB.PendingMigration);

		int observedScore = -1;
		Task migration = ClientB.RunMigrationAsync(async ctx =>
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
		yield return RunTask(migration, ClientB);

		Assert.AreEqual(10, observedScore, "Migration should have read the original persisted score");
		Assert.AreEqual(2, GameServer.GetGameMetadata().Version);

		// Reload the persisted channel through the now-normal v2 connection and verify the new score.
		var subYield2 = ClientB.EntityManager.SubscribeToChannelYield<MigTestChannel>("zone1", null);
		yield return WaitForYield(subYield2, ClientB);
		var chan2 = subYield2.Value;

		yield return TickUntil(() => chan2.DistributedObjects.Count > 0, 3f, ClientB);

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
