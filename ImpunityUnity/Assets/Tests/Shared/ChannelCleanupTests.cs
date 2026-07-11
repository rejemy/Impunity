// ───────── Idle Channel Cleanup Tests ─────────
//
// Ported from Assets/Tests/PlayMode/ImpunityChannelCleanupTests.cs onto the async API + harness.
//
// Exercises the periodic idle-channel reaper (see GameStateLive.CleanupIdleChannels): a channel with zero
// subscribers for longer than ImpunityOptions.IdleChannelTimeoutMillis is removed from live memory. Persisted
// channels keep their database data and reload lazily on the next subscribe; ephemeral channels are gone.
//
// These are genuinely wall-clock tests (the reaper runs on server threads against IdleTimeoutMillis), so
// the whole suite is tagged Slow.
#nullable disable

using System;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

namespace Impunity.Tests
{
	[Category("Slow")]
	public class ChannelCleanupTests : ImpunityTestHarness
	{
		// Short timeout so the reaper fires quickly under real wall-clock time during the test run.
		const int IdleTimeoutMillis = 1000;

		protected override void ConfigureOptions(ImpunityOptions options)
		{
			options.IdleChannelTimeoutMillis = IdleTimeoutMillis;
		}

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
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
		}

		// ═══════════════════════════════════════════════════════════
		// Tests
		// ═══════════════════════════════════════════════════════════

		[Test, Category("ChannelCleanup")]
		public async Task EphemeralChannel_ReapedAfterIdleTimeout()
		{
			CreateServer();
			await ConnectLocal();

			// Create + subscribe an ephemeral channel.
			var live = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("ephReap", new IntegrationTestChannel()), LocalGame);
			Assert.IsNotNull(live);
			Assert.AreEqual(0, GameServer.GetReapedChannelCount(), "Nothing should be reaped while subscribed");

			// Unsubscribe -> zero subscribers -> the idle clock starts.
			await Pump(live.UnsubscribeAsync(immediate: false), LocalGame);

			// The reaper should remove it after the idle timeout elapses.
			await PollUntil(() => GameServer.GetReapedChannelCount() >= 1, TimeSpan.FromSeconds(8), LocalGame);
			Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle ephemeral channel was not reaped");

			// It had no DB backing, so re-subscribing without create-if-missing must now fail.
			var resubTask = LocalGame.EntityManager.SubscribeToChannelAsync<IntegrationTestChannel>("ephReap", null);
			await PumpUntilComplete(resubTask, LocalGame);
			Assert.IsTrue(resubTask.IsFaulted, "Re-subscribe to a reaped ephemeral channel should fail (it no longer exists)");
		}

		[Test, Category("ChannelCleanup")]
		public async Task Disconnect_DropsSubscriber_AndChannelIsReaped()
		{
			// This proves the subscription-tracking fix: a disconnecting client must be removed from the channel's
			// listeners, otherwise the channel never reaches zero subscribers and is never reaped.
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			var live = await Pump(RemoteGame.EntityManager.SubscribeToChannelAsync("discReap", new IntegrationTestChannel()), AllConnections());
			Assert.IsNotNull(live);

			// Hard-disconnect the only subscriber WITHOUT unsubscribing first.
			DisposeConnection(RemoteGame);

			// LocalGame is not subscribed to this channel, so the only path to idleness is the disconnect cleanup.
			await PollUntil(() => GameServer.GetReapedChannelCount() >= 1, TimeSpan.FromSeconds(10), LocalGame);
			Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1,
				"Channel was not reaped after its subscriber disconnected — disconnect did not drop the listener");
		}

		[Test, Category("ChannelCleanup")]
		public async Task ActiveChannel_IsNotReaped()
		{
			CreateServer();
			await ConnectLocal();

			await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("active", new IntegrationTestChannel()), LocalGame);

			// Stay subscribed well past the idle timeout (and across several reaper passes).
			await PumpFor(TimeSpan.FromSeconds(3), LocalGame);

			Assert.AreEqual(0, GameServer.GetReapedChannelCount(), "A channel with an active subscriber must not be reaped");
		}

		[Test, Category("ChannelCleanup")]
		public async Task PersistedChannel_ReapedFromMemory_ThenReloadsData()
		{
			CreateServer();
			await ConnectLocal();

			// Create + subscribe a persisted channel with some data.
			var ch = new MigTestChannel { IsPersisted = true };
			ch.Label.Set("hello");
			var live = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("persistReap", ch), LocalGame);

			// Give the DB worker a moment to flush the persisted create.
			await PumpFor(TimeSpan.FromSeconds(0.5), LocalGame);

			// Unsubscribe -> idle -> eligible for reaping.
			await Pump(live.UnsubscribeAsync(immediate: false), LocalGame);

			await PollUntil(() => GameServer.GetReapedChannelCount() >= 1, TimeSpan.FromSeconds(8), LocalGame);
			Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle persisted channel was not reaped from memory");

			// Re-subscribe without create-if-missing: it must reload from the database with its data intact.
			var reloaded = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync<MigTestChannel>("persistReap", null), LocalGame);
			Assert.IsNotNull(reloaded, "Persisted channel did not reload after being reaped");
			Assert.AreEqual("hello", reloaded.Label.Get(), "Reloaded persisted channel lost its data");
		}
	}
}
