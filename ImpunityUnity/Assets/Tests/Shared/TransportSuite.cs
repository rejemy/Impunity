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
