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


// ───────── Idle Channel Cleanup Tests ─────────
//
// Exercises the periodic idle-channel reaper (see GameStateLive.CleanupIdleChannels): a channel with zero
// subscribers for longer than ImpunityOptions.IdleChannelTimeoutMillis is removed from live memory. Persisted
// channels keep their database data and reload lazily on the next subscribe; ephemeral channels are gone.
//
// Reuses the entity types declared in the sibling PlayMode test files (same assembly): the ephemeral
// IntegrationTestChannel and the persisted MigTestChannel (PersistAs = "mchan", Label persisted as "label").

public class ImpunityChannelCleanupTests
{
	// Short timeout so the reaper fires quickly under real wall-clock time during the test run.
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

		GameStatePath = Path.Combine(Application.temporaryCachePath, "ImpunityCleanupTest_" + Guid.NewGuid().ToString("N"));

		Options = new ImpunityOptions
		{
			GameTypeCode = "Test",
			ServerPort = 39656, // Distinct port from the other PlayMode test fixtures.
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
		var summary = new BsonDocument { ["name"] = "CleanupTest" };
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

	/// <summary>Ticks until the yield resolves (or times out) WITHOUT asserting, so the caller can inspect the error.</summary>
	IEnumerator WaitYieldResolve<T>(ImpunityYield<T> yld, float timeout, params BaseGameConnection[] connections)
	{
		float elapsed = 0f;
		while (yld.keepWaiting && elapsed < timeout)
		{
			foreach (var c in connections) c?.Update();
			yield return null;
			elapsed += Time.deltaTime;
		}
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

	// ═══════════════════════════════════════════════════════════
	// Tests
	// ═══════════════════════════════════════════════════════════

	[UnityTest, Category("ChannelCleanup")]
	public IEnumerator EphemeralChannel_ReapedAfterIdleTimeout()
	{
		CreateServer();
		yield return ConnectLocal();

		// Create + subscribe an ephemeral channel.
		var ch = new IntegrationTestChannel();
		var subYield = LocalGame.EntityManager.SubscribeToChannelYield("ephReap", ch);
		yield return WaitForYield(subYield, LocalGame);
		Assert.IsNotNull(subYield.Value);
		Assert.AreEqual(0, GameServer.GetReapedChannelCount(), "Nothing should be reaped while subscribed");

		// Unsubscribe -> zero subscribers -> the idle clock starts.
		var unsubYield = LocalGame.EntityManager.UnsubscribeFromChannelYield(subYield.Value, immediate: false);
		yield return WaitForYield(unsubYield, LocalGame);

		// The reaper should remove it after the idle timeout elapses.
		yield return PollUntil(() => GameServer.GetReapedChannelCount() >= 1, 8f, LocalGame);
		Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle ephemeral channel was not reaped");

		// It had no DB backing, so re-subscribing without create-if-missing must now fail.
		var resubYield = LocalGame.EntityManager.SubscribeToChannelYield<IntegrationTestChannel>("ephReap", null);
		yield return WaitYieldResolve(resubYield, 5f, LocalGame);
		Assert.IsFalse(resubYield.keepWaiting, "Re-subscribe did not resolve");
		Assert.IsNotNull(resubYield.Error, "Re-subscribe to a reaped ephemeral channel should fail (it no longer exists)");
	}

	[UnityTest, Category("ChannelCleanup")]
	public IEnumerator Disconnect_DropsSubscriber_AndChannelIsReaped()
	{
		// This proves the subscription-tracking fix: a disconnecting client must be removed from the channel's
		// listeners, otherwise the channel never reaches zero subscribers and is never reaped.
		CreateServer();
		yield return ConnectLocal();
		yield return StartTCPAndConnectRemote();

		var ch = new IntegrationTestChannel();
		var subYield = RemoteGame.EntityManager.SubscribeToChannelYield("discReap", ch);
		yield return WaitForYield(subYield, AllConnections());
		Assert.IsNotNull(subYield.Value);

		// Hard-disconnect the only subscriber WITHOUT unsubscribing first.
		RemoteGame.Dispose();
		RemoteGame = null;

		// LocalGame is not subscribed to this channel, so the only path to idleness is the disconnect cleanup.
		yield return PollUntil(() => GameServer.GetReapedChannelCount() >= 1, 10f, LocalGame);
		Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1,
			"Channel was not reaped after its subscriber disconnected — disconnect did not drop the listener");
	}

	[UnityTest, Category("ChannelCleanup")]
	public IEnumerator ActiveChannel_IsNotReaped()
	{
		CreateServer();
		yield return ConnectLocal();

		var ch = new IntegrationTestChannel();
		var subYield = LocalGame.EntityManager.SubscribeToChannelYield("active", ch);
		yield return WaitForYield(subYield, LocalGame);

		// Stay subscribed well past the idle timeout (and across several reaper passes).
		yield return TickFor(3f, LocalGame);

		Assert.AreEqual(0, GameServer.GetReapedChannelCount(), "A channel with an active subscriber must not be reaped");
	}

	[UnityTest, Category("ChannelCleanup")]
	public IEnumerator PersistedChannel_ReapedFromMemory_ThenReloadsData()
	{
		CreateServer();
		yield return ConnectLocal();

		// Create + subscribe a persisted channel with some data.
		var ch = new MigTestChannel { IsPersisted = true };
		ch.Label.Set("hello");
		var subYield = LocalGame.EntityManager.SubscribeToChannelYield("persistReap", ch);
		yield return WaitForYield(subYield, LocalGame);

		// Give the DB worker a moment to flush the persisted create.
		yield return TickFor(0.5f, LocalGame);

		// Unsubscribe -> idle -> eligible for reaping.
		var unsubYield = LocalGame.EntityManager.UnsubscribeFromChannelYield(subYield.Value, immediate: false);
		yield return WaitForYield(unsubYield, LocalGame);

		yield return PollUntil(() => GameServer.GetReapedChannelCount() >= 1, 8f, LocalGame);
		Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle persisted channel was not reaped from memory");

		// Re-subscribe without create-if-missing: it must reload from the database with its data intact.
		var resubYield = LocalGame.EntityManager.SubscribeToChannelYield<MigTestChannel>("persistReap", null);
		yield return WaitForYield(resubYield, LocalGame);
		Assert.IsNotNull(resubYield.Value, "Persisted channel did not reload after being reaped");
		Assert.AreEqual("hello", resubYield.Value.Label.Get(), "Reloaded persisted channel lost its data");
	}
}
