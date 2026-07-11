// ───────── Coroutine Yield Wrapper Tests (Play Mode) ─────────
//
// The shared suites moved to the async API, so the Unity coroutine wrappers (ImpunityYield /
// ImpunityYield<T> and the ...Yield() extensions in Client/Unity/ImpunityUnityExt.cs) get their
// coverage here: the yield-completion contract (keepWaiting / Value / Error) across connect, database
// ops, channels, entities, exclusive updates, locks, and an error path.
//
// Reuses the shared harness (server setup/teardown, ports, temp dirs) and entity types from the
// ImpunitySharedTests assembly; only the pumping here is coroutine-based, since that IS the API under
// test.

using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Tests;
using Impunity.Unity;

using UltraLiteDB;

public class YieldWrapperTests : ImpunityTestHarness
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

	// ───────── Coroutine helpers (the yield-pumping pattern under test) ─────────

	LocalGameConnection NewLocalClient()
	{
		var conn = Track(new LocalGameConnection(GameServer, Format));
		// Distinct identity per client — LocalGameConnections all share the hard-coded "local_key",
		// which would make the server treat them as the same client for lock ownership.
		conn.ConnectionKey = "local_key_" + Guid.NewGuid().ToString("N").Substring(0, 8);
		return conn;
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

	/// <summary>Ticks until the yield resolves WITHOUT asserting, so the caller can inspect Error.</summary>
	IEnumerator WaitYieldResolve<T>(ImpunityYield<T> yld, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < 5f)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
		Assert.IsFalse(yld.keepWaiting, "Operation did not resolve");
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

	IEnumerator ConnectClient(LocalGameConnection conn)
	{
		yield return WaitForYield(conn.ConnectYield(), conn);
		Assert.IsTrue(conn.Connected, "Connection failed");
	}

	// ═══════════════════════════════════════════════════════════
	// Tests
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator ConnectYield_Succeeds()
	{
		CreateServer();
		var conn = NewLocalClient();

		var yld = conn.ConnectYield();
		yield return WaitForYield(yld, conn);

		Assert.IsNull(yld.Error);
		Assert.IsTrue(conn.Connected);
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator DocumentYields_CrudRoundTrip()
	{
		CreateServer();
		var conn = NewLocalClient();
		yield return ConnectClient(conn);

		var insert = conn.InsertDocumentYield(IntegrationTestCollections.ITEMS, new BsonDocument { ["_id"] = "y1", ["name"] = "Sword" });
		yield return WaitForYield(insert, conn);
		Assert.IsNotNull(insert.Value);

		var find = conn.FindDocumentByIdYield(IntegrationTestCollections.ITEMS, "y1");
		yield return WaitForYield(find, conn);
		Assert.AreEqual("Sword", (string)find.Value["name"]);

		var list = conn.ListDocumentsYield(IntegrationTestCollections.ITEMS);
		yield return WaitForYield(list, conn);
		Assert.AreEqual(1, list.Value.Count);

		var del = conn.DeleteDocumentYield(IntegrationTestCollections.ITEMS, "y1");
		yield return WaitForYield(del, conn);
		Assert.IsTrue(del.Value);
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator GameSummaryYields_RoundTrip()
	{
		CreateServer();
		var conn = NewLocalClient();
		yield return ConnectClient(conn);

		yield return WaitForYield(conn.SetGameSummaryYield(new BsonDocument { ["marker"] = "yields" }), conn);

		var summary = conn.GetGameSummaryYield();
		yield return WaitForYield(summary, conn);
		Assert.AreEqual("yields", (string)summary.Value["marker"]);
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator SubscribeToChannelYield_ReturnsLiveRegisteredInstance()
	{
		CreateServer();
		var conn = NewLocalClient();
		yield return ConnectClient(conn);

		// Documented gotcha: the createIfNeeded instance is discarded — the live, registered channel
		// is the yield's Value, built fresh from the server's create message.
		var init = new IntegrationTestChannel();
		var sub = conn.EntityManager.SubscribeToChannelYield("yieldchan", init);
		yield return WaitForYield(sub, conn);

		Assert.IsNotNull(sub.Value, "Subscribe did not return a channel instance");
		Assert.AreNotSame(init, sub.Value, "The live channel should be a fresh instance, not the createIfNeeded one");
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator ChannelValueReplication_ViaYields()
	{
		CreateServer();
		var c1 = NewLocalClient();
		var c2 = NewLocalClient();
		yield return ConnectClient(c1);
		yield return ConnectClient(c2);

		var c1Init = new IntegrationTestChannel();
		c1Init.Status.Set("active");
		var sub1 = c1.EntityManager.SubscribeToChannelYield("repchan", c1Init);
		yield return WaitForYield(sub1, c1, c2);

		var sub2 = c2.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("repchan", null);
		yield return WaitForYield(sub2, c1, c2);
		Assert.AreEqual("active", sub2.Value.Status.Get(), "Initial channel value did not replicate");

		sub1.Value.Status.Set("busy");
		yield return TickUntil(() => sub2.Value.Status.Get() == "busy", 3f, c1, c2);
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator CreateObjectYield_And_UpdateExclusiveYield()
	{
		CreateServer();
		var conn = NewLocalClient();
		yield return ConnectClient(conn);

		var sub = conn.EntityManager.SubscribeToChannelYield("exclchan", new IntegrationTestChannel());
		yield return WaitForYield(sub, conn);

		var entity = new IntegrationTestEntity();
		entity.Health.Set(10);
		var create = conn.EntityManager.CreateObjectYield(entity, sub.Value, false);
		yield return WaitForYield(create, conn);
		Assert.AreSame(entity, create.Value, "CreateObject keeps the caller's instance as the live one");

		// The creator of a fresh object can exclusively update it (no error), and the echo applies the value.
		entity.Health.Set(20);
		var excl = entity.UpdateExclusiveYield();
		yield return WaitForYield(excl, conn);
		yield return TickUntil(() => entity.Health.Get() == 20, 3f, conn);
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator DeleteYield_ReplicatesDeletion()
	{
		CreateServer();
		var c1 = NewLocalClient();
		var c2 = NewLocalClient();
		yield return ConnectClient(c1);
		yield return ConnectClient(c2);

		var sub1 = c1.EntityManager.SubscribeToChannelYield("delchan", new IntegrationTestChannel());
		yield return WaitForYield(sub1, c1, c2);

		var entity = new IntegrationTestEntity();
		entity.Health.Set(1);
		yield return WaitForYield(c1.EntityManager.CreateObjectYield(entity, sub1.Value, false), c1, c2);

		var sub2 = c2.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("delchan", null);
		yield return WaitForYield(sub2, c1, c2);
		yield return TickUntil(() => sub2.Value.DistributedObjects.Count > 0, 3f, c1, c2);

		IntegrationTestEntity c2Entity = null;
		foreach (var obj in sub2.Value.DistributedObjects.Values)
		{
			c2Entity = obj as IntegrationTestEntity;
			break;
		}
		Assert.IsNotNull(c2Entity);

		var del = entity.DeleteYield("goodbye");
		yield return WaitForYield(del, c1, c2);
		Assert.IsTrue(del.Value);

		yield return TickUntil(() => c2Entity.WasDeleted, 3f, c1, c2);
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator LockYields_Contention()
	{
		CreateServer();
		var c1 = NewLocalClient();
		var c2 = NewLocalClient();
		yield return ConnectClient(c1);
		yield return ConnectClient(c2);

		var lock1 = c1.TryToLockYield("yieldlock");
		yield return WaitForYield(lock1, c1, c2);
		Assert.IsTrue(lock1.Value, "C1 should acquire the lock");

		var lock2 = c2.TryToLockYield("yieldlock");
		yield return WaitForYield(lock2, c1, c2);
		Assert.IsFalse(lock2.Value, "C2 should fail to acquire the held lock");

		var unlock = c1.UnlockYield("yieldlock");
		yield return WaitForYield(unlock, c1, c2);
		Assert.IsTrue(unlock.Value);

		var lock3 = c2.TryToLockYield("yieldlock");
		yield return WaitForYield(lock3, c1, c2);
		Assert.IsTrue(lock3.Value, "C2 should acquire the lock after release");
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator ListActiveChannelsYield_ContainsSubscribed()
	{
		CreateServer();
		var conn = NewLocalClient();
		yield return ConnectClient(conn);

		yield return WaitForYield(conn.EntityManager.SubscribeToChannelYield("listchan", new IntegrationTestChannel()), conn);

		var list = conn.ListActiveChannelsYield();
		yield return WaitForYield(list, conn);
		CollectionAssert.Contains(list.Value, "listchan");
	}

	[UnityTest, Category("YieldWrappers")]
	public IEnumerator SubscribeYield_ErrorPath_SetsError()
	{
		CreateServer();
		var conn = NewLocalClient();
		yield return ConnectClient(conn);

		// Subscribing to a channel that does not exist, without create-if-needed, must resolve with
		// an Error (the ImpunityYield error path, not an exception or a hang).
		var sub = conn.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("no_such_channel", null);
		yield return WaitYieldResolve(sub, conn);

		Assert.IsNotNull(sub.Error, "Expected an error subscribing to a missing channel");
		Assert.IsNull(sub.Value);
	}
}
