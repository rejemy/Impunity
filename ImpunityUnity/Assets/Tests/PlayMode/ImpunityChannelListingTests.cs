using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

using Impunity;
using Impunity.Connection;
using Impunity.GameState;
using Impunity.Networking;
using Impunity.Unity;

using UltraLiteDB;


// ───────── Channel Listing Tests ─────────
//
// Exercises the two channel-enumeration APIs on BaseGameConnection:
//   • ListActiveChannels()    -> GameStateLive.ListActiveChannelNames()  — channels currently live in memory
//                                 (i.e. with current or recent subscribers, before the idle reaper removes them).
//   • ListPersistedChannels() -> GameStateDB.ListPersistedChannelNames() — channels whose metadata is stored in
//                                 the database, whether or not they are currently live.
//
// Both return the subscribe-time channel NAME (GameStateDB writes entityDoc["ch"] = channelName), not the type's
// PersistAs code. So an ephemeral channel appears in the active list but never in the persisted list, and a
// persisted channel appears in the persisted list even after being reaped from live memory.
//
// Reuses the entity types declared in the sibling PlayMode test files (same assembly): the ephemeral
// IntegrationTestChannel and the persisted MigTestChannel (PersistAs = "mchan").

public class ImpunityChannelListingTests
{
	// Short idle timeout so the reaper fires quickly for the reap-then-list test.
	const int IdleTimeoutMillis = 1000;

	string GameStatePath;
	ImpunityOptions Options;
	GameStateFormat Format;
	GameStateEntityTypeDef[] EntityDefs;

	GameStateServer GameServer;
	ImpunityServer TCPServer;
	LocalGameConnection LocalGame;
	RemoteGameConnection RemoteGame;

	[SetUp]
	public void SetUp()
	{
		BsonMapper.Global.IncludeFields = true;

		GameStatePath = Path.Combine(Application.temporaryCachePath, "ImpunityListingTest_" + Guid.NewGuid().ToString("N"));

		Options = new ImpunityOptions
		{
			GameTypeCode = "Test",
			ServerPort = 39657, // Distinct port from the other PlayMode test fixtures.
			IdleChannelTimeoutMillis = IdleTimeoutMillis
		};

		Format = new GameStateFormat(
			1,
			new GameStateCollection[]
			{
				new GameStateCollection { Index = 10, Name = "Items" }
			},
			new Type[]
			{
				typeof(IntegrationTestEntity),
				typeof(IntegrationTestChannel),
				typeof(MigTestChannel),
				typeof(MigTestObject)
			}
		);

		EntityDefs = new ClientEntityManager().RegisterEntityTypes(Format.EntityTypes);
	}

	[TearDown]
	public void TearDown()
	{
		LocalGame?.Dispose();
		LocalGame = null;

		RemoteGame?.Dispose();
		RemoteGame = null;

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

	void CreateServer()
	{
		var summary = new BsonDocument { ["name"] = "ListingTest" };
		GameServer = GameStateServer.Create("test", null, GameStatePath, summary, Options);
		GameServer.UpdateFormat(new GameStateFormatData(Format, EntityDefs), false);
	}

	IEnumerator ConnectLocal()
	{
		LocalGame = new LocalGameConnection(GameServer, Format);
		yield return WaitForYield(LocalGame.ConnectYield(), LocalGame);
		Assert.IsTrue(LocalGame.Connected, "Local connection failed");
	}

	IEnumerator StartTCPAndConnectRemote()
	{
		TCPServer = new ImpunityServer(GameServer, Options);
		TCPServer.Start();

		yield return new WaitForSeconds(0.1f);

		RemoteGame = RemoteGameConnection.MakeTCPRemoteConnection(
			TCPServer.TCPEndpoint, "test", null, Format, Options);
		RemoteGame.OnNetworkError = (err) => Debug.LogError("Network error: " + err.Message);

		yield return WaitForYield(RemoteGame.ConnectYield(), RemoteGame);
		Assert.IsTrue(RemoteGame.Connected, "Remote connection failed");
	}

	BaseGameConnection[] AllConnections()
	{
		var list = new List<BaseGameConnection>();
		if (LocalGame != null) list.Add(LocalGame);
		if (RemoteGame != null) list.Add(RemoteGame);
		return list.ToArray();
	}

	/// <summary>Ticks connection(s) each frame until the yield completes; asserts success.</summary>
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

	/// <summary>Ticks connection(s) each frame until the yield completes; asserts success. Returns value.</summary>
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

	/// <summary>Ticks connections each frame for the given duration.</summary>
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

	/// <summary>Ticks connections until condition is met or timeout, WITHOUT asserting.</summary>
	IEnumerator PollUntil(Func<bool> condition, float timeout, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (!condition() && elapsed < timeout)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
	}

	/// <summary>Subscribes the given connection to an ephemeral IntegrationTestChannel by name.</summary>
	IEnumerator SubscribeEphemeral(BaseGameConnection conn, string channelName)
	{
		var ch = new IntegrationTestChannel();
		var subYield = conn.EntityManager.SubscribeToChannelYield(channelName, ch);
		yield return WaitForYield(subYield, AllConnections());
		Assert.IsNotNull(subYield.Value, "Failed to subscribe to ephemeral channel " + channelName);
	}

	/// <summary>Subscribes the given connection to a persisted MigTestChannel by name and lets the DB flush.</summary>
	IEnumerator SubscribePersisted(BaseGameConnection conn, string channelName, string label)
	{
		var ch = new MigTestChannel { IsPersisted = true };
		ch.Label.Set(label);
		var subYield = conn.EntityManager.SubscribeToChannelYield(channelName, ch);
		yield return WaitForYield(subYield, AllConnections());
		Assert.IsNotNull(subYield.Value, "Failed to subscribe to persisted channel " + channelName);

		// Give the DB worker a moment to flush the persisted channel metadata.
		yield return TickFor(0.5f, AllConnections());
	}

	// ═══════════════════════════════════════════════════════════
	// ListActiveChannels
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListActiveChannels_EmptyBeforeAnySubscription()
	{
		CreateServer();
		yield return ConnectLocal();

		var listYield = LocalGame.ListActiveChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		Assert.IsNotNull(listYield.Value, "Active-channel list should be non-null even when empty");
		Assert.AreEqual(0, listYield.Value.Count, "No channels should be active before any subscription");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListActiveChannels_ContainsSubscribedChannel()
	{
		CreateServer();
		yield return ConnectLocal();

		yield return SubscribeEphemeral(LocalGame, "activeA");

		var listYield = LocalGame.ListActiveChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		CollectionAssert.Contains(listYield.Value, "activeA", "Subscribed channel should appear in the active list");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListActiveChannels_ReflectsMultipleSubscriptions()
	{
		CreateServer();
		yield return ConnectLocal();

		yield return SubscribeEphemeral(LocalGame, "multiA");
		yield return SubscribeEphemeral(LocalGame, "multiB");
		yield return SubscribePersisted(LocalGame, "multiC", "c-label");

		var listYield = LocalGame.ListActiveChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		CollectionAssert.Contains(listYield.Value, "multiA");
		CollectionAssert.Contains(listYield.Value, "multiB");
		CollectionAssert.Contains(listYield.Value, "multiC", "A persisted channel is also live while subscribed");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListActiveChannels_OverTCP_ContainsSubscribedChannel()
	{
		// Exercises the wire path: the List<string> result must round-trip across the TCP transport.
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		yield return SubscribeEphemeral(RemoteGame, "remoteActive");

		var listYield = RemoteGame.ListActiveChannelsYield();
		yield return WaitForYield(listYield, AllConnections());

		Assert.IsNotNull(listYield.Value, "Active-channel list should serialize across TCP");
		CollectionAssert.Contains(listYield.Value, "remoteActive",
			"Channel subscribed over TCP should appear in the active list");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListActiveChannels_DropsChannelAfterReap()
	{
		CreateServer();
		yield return ConnectLocal();

		// Subscribe an ephemeral channel, then unsubscribe so it goes idle and gets reaped from live memory.
		var ch = new IntegrationTestChannel();
		var subYield = LocalGame.EntityManager.SubscribeToChannelYield("toReap", ch);
		yield return WaitForYield(subYield, LocalGame);
		Assert.IsNotNull(subYield.Value);

		var unsubYield = LocalGame.EntityManager.UnsubscribeFromChannelYield(subYield.Value, immediate: false);
		yield return WaitForYield(unsubYield, LocalGame);

		yield return PollUntil(() => GameServer.GetReapedChannelCount() >= 1, 8f, LocalGame);
		Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle channel was not reaped");

		var listYield = LocalGame.ListActiveChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		CollectionAssert.DoesNotContain(listYield.Value, "toReap",
			"A reaped channel should no longer appear in the active list");
	}

	// ═══════════════════════════════════════════════════════════
	// ListPersistedChannels
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListPersistedChannels_EmptyBeforeAnyPersistedChannel()
	{
		CreateServer();
		yield return ConnectLocal();

		var listYield = LocalGame.ListPersistedChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		Assert.IsNotNull(listYield.Value, "Persisted-channel list should be non-null even when empty");
		Assert.AreEqual(0, listYield.Value.Count, "No channels should be persisted before any persisted subscribe");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListPersistedChannels_ContainsPersistedChannel()
	{
		CreateServer();
		yield return ConnectLocal();

		yield return SubscribePersisted(LocalGame, "persistedA", "hello");

		var listYield = LocalGame.ListPersistedChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		CollectionAssert.Contains(listYield.Value, "persistedA",
			"A persisted channel should appear in the persisted list");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListPersistedChannels_ExcludesEphemeralChannel()
	{
		CreateServer();
		yield return ConnectLocal();

		// One ephemeral, one persisted — only the persisted one should be in the persisted list.
		yield return SubscribeEphemeral(LocalGame, "ephOnly");
		yield return SubscribePersisted(LocalGame, "persOnly", "data");

		var listYield = LocalGame.ListPersistedChannelsYield();
		yield return WaitForYield(listYield, LocalGame);

		CollectionAssert.Contains(listYield.Value, "persOnly");
		CollectionAssert.DoesNotContain(listYield.Value, "ephOnly",
			"An ephemeral channel must never appear in the persisted list");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListPersistedChannels_SurvivesReapWhileActiveDropsIt()
	{
		// The defining contrast between the two APIs: after a persisted channel is reaped from live memory it
		// disappears from ListActiveChannels but remains in ListPersistedChannels (its DB metadata persists).
		CreateServer();
		yield return ConnectLocal();

		var ch = new MigTestChannel { IsPersisted = true };
		ch.Label.Set("survivor");
		var subYield = LocalGame.EntityManager.SubscribeToChannelYield("survivor", ch);
		yield return WaitForYield(subYield, LocalGame);
		yield return TickFor(0.5f, LocalGame); // Let the DB flush the persisted create.

		// While subscribed it appears in both lists.
		var activeBefore = LocalGame.ListActiveChannelsYield();
		yield return WaitForYield(activeBefore, LocalGame);
		CollectionAssert.Contains(activeBefore.Value, "survivor", "Persisted channel should be active while subscribed");

		// Unsubscribe -> idle -> reaped from live memory.
		var unsubYield = LocalGame.EntityManager.UnsubscribeFromChannelYield(subYield.Value, immediate: false);
		yield return WaitForYield(unsubYield, LocalGame);
		yield return PollUntil(() => GameServer.GetReapedChannelCount() >= 1, 8f, LocalGame);
		Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle persisted channel was not reaped from memory");

		// Now it's gone from the active list...
		var activeAfter = LocalGame.ListActiveChannelsYield();
		yield return WaitForYield(activeAfter, LocalGame);
		CollectionAssert.DoesNotContain(activeAfter.Value, "survivor",
			"Reaped channel should drop out of the active list");

		// ...but still present in the persisted list.
		var persistedAfter = LocalGame.ListPersistedChannelsYield();
		yield return WaitForYield(persistedAfter, LocalGame);
		CollectionAssert.Contains(persistedAfter.Value, "survivor",
			"Persisted channel should remain in the persisted list after being reaped from memory");
	}

	[UnityTest, Category("ChannelListing")]
	public IEnumerator ListPersistedChannels_OverTCP_ContainsPersistedChannel()
	{
		// Exercises the wire path for a DB-backed action: the List<string> result must round-trip over TCP.
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		yield return SubscribePersisted(RemoteGame, "remotePersisted", "over-the-wire");

		var listYield = RemoteGame.ListPersistedChannelsYield();
		yield return WaitForYield(listYield, AllConnections());

		Assert.IsNotNull(listYield.Value, "Persisted-channel list should serialize across TCP");
		CollectionAssert.Contains(listYield.Value, "remotePersisted",
			"Channel persisted over TCP should appear in the persisted list");
	}
}
