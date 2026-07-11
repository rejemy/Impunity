// ───────── Channel Listing Tests ─────────
//
// Ported from Assets/Tests/PlayMode/ImpunityChannelListingTests.cs onto the async API + harness.
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
#nullable disable

using System;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

namespace Impunity.Tests
{
	public class ChannelListingTests : ImpunityTestHarness
	{
		// Short idle timeout so the reaper fires quickly for the reap-then-list test.
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

		// ───────── Helpers ─────────

		/// <summary>Subscribes the given connection to an ephemeral IntegrationTestChannel by name.</summary>
		async Task<IntegrationTestChannel> SubscribeEphemeral(BaseGameConnection conn, string channelName)
		{
			var ch = new IntegrationTestChannel();
			var live = await Pump(conn.EntityManager.SubscribeToChannelAsync(channelName, ch), AllConnections());
			Assert.IsNotNull(live, "Failed to subscribe to ephemeral channel " + channelName);
			return live;
		}

		/// <summary>Subscribes the given connection to a persisted MigTestChannel by name and lets the DB flush.</summary>
		async Task<MigTestChannel> SubscribePersisted(BaseGameConnection conn, string channelName, string label)
		{
			var ch = new MigTestChannel { IsPersisted = true };
			ch.Label.Set(label);
			var live = await Pump(conn.EntityManager.SubscribeToChannelAsync(channelName, ch), AllConnections());
			Assert.IsNotNull(live, "Failed to subscribe to persisted channel " + channelName);

			// Give the DB worker a moment to flush the persisted channel metadata.
			await PumpFor(TimeSpan.FromSeconds(0.5), AllConnections());
			return live;
		}

		// ═══════════════════════════════════════════════════════════
		// ListActiveChannels
		// ═══════════════════════════════════════════════════════════

		[Test, Category("ChannelListing")]
		public async Task ListActiveChannels_EmptyBeforeAnySubscription()
		{
			CreateServer();
			await ConnectLocal();

			var list = await Pump(LocalGame.ListActiveChannels(), LocalGame);

			Assert.IsNotNull(list, "Active-channel list should be non-null even when empty");
			Assert.AreEqual(0, list.Count, "No channels should be active before any subscription");
		}

		[Test, Category("ChannelListing")]
		public async Task ListActiveChannels_ContainsSubscribedChannel()
		{
			CreateServer();
			await ConnectLocal();

			await SubscribeEphemeral(LocalGame, "activeA");

			var list = await Pump(LocalGame.ListActiveChannels(), LocalGame);

			CollectionAssert.Contains(list, "activeA", "Subscribed channel should appear in the active list");
		}

		[Test, Category("ChannelListing")]
		public async Task ListActiveChannels_ReflectsMultipleSubscriptions()
		{
			CreateServer();
			await ConnectLocal();

			await SubscribeEphemeral(LocalGame, "multiA");
			await SubscribeEphemeral(LocalGame, "multiB");
			await SubscribePersisted(LocalGame, "multiC", "c-label");

			var list = await Pump(LocalGame.ListActiveChannels(), LocalGame);

			CollectionAssert.Contains(list, "multiA");
			CollectionAssert.Contains(list, "multiB");
			CollectionAssert.Contains(list, "multiC", "A persisted channel is also live while subscribed");
		}

		[Test, Category("ChannelListing")]
		public async Task ListActiveChannels_OverTCP_ContainsSubscribedChannel()
		{
			// Exercises the wire path: the List<string> result must round-trip across the TCP transport.
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			await SubscribeEphemeral(RemoteGame, "remoteActive");

			var list = await Pump(RemoteGame.ListActiveChannels(), AllConnections());

			Assert.IsNotNull(list, "Active-channel list should serialize across TCP");
			CollectionAssert.Contains(list, "remoteActive",
				"Channel subscribed over TCP should appear in the active list");
		}

		[Test, Category("ChannelListing")]
		public async Task ListActiveChannels_DropsChannelAfterReap()
		{
			CreateServer();
			await ConnectLocal();

			// Subscribe an ephemeral channel, then unsubscribe so it goes idle and gets reaped from live memory.
			var ch = new IntegrationTestChannel();
			var live = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("toReap", ch), LocalGame);
			Assert.IsNotNull(live);

			await Pump(live.UnsubscribeAsync(immediate: false), LocalGame);

			await PollUntil(() => GameServer.GetReapedChannelCount() >= 1, TimeSpan.FromSeconds(8), LocalGame);
			Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle channel was not reaped");

			var list = await Pump(LocalGame.ListActiveChannels(), LocalGame);

			CollectionAssert.DoesNotContain(list, "toReap",
				"A reaped channel should no longer appear in the active list");
		}

		// ═══════════════════════════════════════════════════════════
		// ListPersistedChannels
		// ═══════════════════════════════════════════════════════════

		[Test, Category("ChannelListing")]
		public async Task ListPersistedChannels_EmptyBeforeAnyPersistedChannel()
		{
			CreateServer();
			await ConnectLocal();

			var list = await Pump(LocalGame.ListPersistedChannels(), LocalGame);

			Assert.IsNotNull(list, "Persisted-channel list should be non-null even when empty");
			Assert.AreEqual(0, list.Count, "No channels should be persisted before any persisted subscribe");
		}

		[Test, Category("ChannelListing")]
		public async Task ListPersistedChannels_ContainsPersistedChannel()
		{
			CreateServer();
			await ConnectLocal();

			await SubscribePersisted(LocalGame, "persistedA", "hello");

			var list = await Pump(LocalGame.ListPersistedChannels(), LocalGame);

			CollectionAssert.Contains(list, "persistedA",
				"A persisted channel should appear in the persisted list");
		}

		[Test, Category("ChannelListing")]
		public async Task ListPersistedChannels_ExcludesEphemeralChannel()
		{
			CreateServer();
			await ConnectLocal();

			// One ephemeral, one persisted — only the persisted one should be in the persisted list.
			await SubscribeEphemeral(LocalGame, "ephOnly");
			await SubscribePersisted(LocalGame, "persOnly", "data");

			var list = await Pump(LocalGame.ListPersistedChannels(), LocalGame);

			CollectionAssert.Contains(list, "persOnly");
			CollectionAssert.DoesNotContain(list, "ephOnly",
				"An ephemeral channel must never appear in the persisted list");
		}

		[Test, Category("ChannelListing")]
		public async Task ListPersistedChannels_SurvivesReapWhileActiveDropsIt()
		{
			// The defining contrast between the two APIs: after a persisted channel is reaped from live memory it
			// disappears from ListActiveChannels but remains in ListPersistedChannels (its DB metadata persists).
			CreateServer();
			await ConnectLocal();

			var ch = new MigTestChannel { IsPersisted = true };
			ch.Label.Set("survivor");
			var live = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync("survivor", ch), LocalGame);
			await PumpFor(TimeSpan.FromSeconds(0.5), LocalGame); // Let the DB flush the persisted create.

			// While subscribed it appears in both lists.
			var activeBefore = await Pump(LocalGame.ListActiveChannels(), LocalGame);
			CollectionAssert.Contains(activeBefore, "survivor", "Persisted channel should be active while subscribed");

			// Unsubscribe -> idle -> reaped from live memory.
			await Pump(live.UnsubscribeAsync(immediate: false), LocalGame);
			await PollUntil(() => GameServer.GetReapedChannelCount() >= 1, TimeSpan.FromSeconds(8), LocalGame);
			Assert.GreaterOrEqual(GameServer.GetReapedChannelCount(), 1, "Idle persisted channel was not reaped from memory");

			// Now it's gone from the active list...
			var activeAfter = await Pump(LocalGame.ListActiveChannels(), LocalGame);
			CollectionAssert.DoesNotContain(activeAfter, "survivor",
				"Reaped channel should drop out of the active list");

			// ...but still present in the persisted list.
			var persistedAfter = await Pump(LocalGame.ListPersistedChannels(), LocalGame);
			CollectionAssert.Contains(persistedAfter, "survivor",
				"Persisted channel should remain in the persisted list after being reaped from memory");
		}

		[Test, Category("ChannelListing")]
		public async Task ListPersistedChannels_OverTCP_ContainsPersistedChannel()
		{
			// Exercises the wire path for a DB-backed action: the List<string> result must round-trip over TCP.
			CreateServer();
			await ConnectLocal();
			await StartTCPAndConnectRemote();

			await SubscribePersisted(RemoteGame, "remotePersisted", "over-the-wire");

			var list = await Pump(RemoteGame.ListPersistedChannels(), AllConnections());

			Assert.IsNotNull(list, "Persisted-channel list should serialize across TCP");
			CollectionAssert.Contains(list, "remotePersisted",
				"Channel persisted over TCP should appear in the persisted list");
		}
	}
}
