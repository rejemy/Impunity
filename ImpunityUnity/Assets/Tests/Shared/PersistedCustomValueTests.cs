// ───────── Persisted custom values, through a restart ─────────
//
// The server stores a custom-serialized field's raw bytes as BSON binary, and a null (default) segment as BSON null.
// Reading that null back used to throw a NullReferenceException deep in the channel load (unboxing null to an
// ArraySegment), failing the whole channel with an error that named nothing. These cover the round trip of every
// custom kind, null and set, across a server restart; a row that can't be read naming its entity and property; and
// the error plumbing that carries a server-side stack to the client.
#nullable disable

using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

using Impunity.Connection;
using Impunity.GameState;

using UltraLiteDB;

namespace Impunity.Tests
{
	public class PersistedCustomValueTests : ImpunityTestHarness
	{
		const string ChannelName = "pcustom";

		protected override GameStateFormat CreateFormat()
		{
			return new GameStateFormat(
				1,
				new GameStateCollection[]
				{
					new GameStateCollection { Index = MigTestCollections.ITEMS, Name = "Items" }
				},
				new Type[]
				{
					typeof(MigTestChannel),
					typeof(PersistedCustomEntity)
				}
			);
		}

		// ───────── Helpers ─────────

		async Task<PersistedCustomEntity> CreateAndFlush(Action<PersistedCustomEntity> setup)
		{
			var channel = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync(
				ChannelName, new MigTestChannel { IsPersisted = true }), LocalGame);

			var entity = new PersistedCustomEntity { IsPersisted = true, UniqueName = "thing" };
			setup(entity);
			await Pump(LocalGame.EntityManager.CreateObjectAsync(entity, channel, false), LocalGame);

			// DB actions run in FIFO order on one worker, so once this read replies the create's rows have landed.
			await Pump(LocalGame.FindDocumentByIdAsync(MigTestCollections.ITEMS, "flush"), LocalGame);
			return entity;
		}

		void StopServer()
		{
			DisposeConnection(LocalGame);
			GameServer.Dispose();
		}

		async Task Restart()
		{
			GameServer = GameStateServer.Open("test", null, GameStatePath, Options);
			await ConnectLocal();
		}

		async Task<PersistedCustomEntity> ReloadThing()
		{
			var reloaded = await Pump(LocalGame.EntityManager.SubscribeToChannelAsync<MigTestChannel>(ChannelName, null), LocalGame);
			Assert.IsNotNull(reloaded, "the persisted channel didn't reload");

			foreach (var obj in reloaded.DistributedObjects.Values)
			{
				if (obj is PersistedCustomEntity thing)
				{
					return thing;
				}
			}

			Assert.Fail("the persisted object didn't reload");
			return null;
		}

		/// <summary>Rewrites one persisted property row of the stopped server's database: the raw way to plant a row
		/// the entity type can't read.</summary>
		void OverwritePersistedProperty(string propertyKey, BsonValue value)
		{
			string[] files = Directory.GetFiles(GameStatePath, "Game.db", SearchOption.AllDirectories);
			Assert.AreEqual(1, files.Length, "expected one game database under " + GameStatePath);

			using (var db = new UltraLiteDatabase(new ConnectionString { Filename = files[0], Password = Options.DBPassword }))
			{
				var rows = db.GetCollection<BsonDocument>(GameStateDB.EntitiesCollectionName);
				int changed = 0;
				foreach (BsonDocument row in rows.FindAll())
				{
					if (row["_id"].AsString.EndsWith("/" + propertyKey, StringComparison.Ordinal))
					{
						row["v"] = value;
						rows.Upsert(row);
						changed++;
					}
				}

				Assert.AreEqual(1, changed, "expected one '" + propertyKey + "' row");
			}
		}

		// ───────── Round trips ─────────

		[Test, Category("Persistence")]
		public async Task NullCustomFields_SurviveRestart()
		{
			CreateServer();
			await ConnectLocal();
			await CreateAndFlush(e => { });

			StopServer();
			await Restart();
			PersistedCustomEntity thing = await ReloadThing();

			Assert.IsNull(thing.Box.Get(), "nullable custom (wide)");
			Assert.IsNull(thing.SmallBox.Get(), "nullable custom (small)");
			Assert.IsNull(thing.Blob.Get().Array, "nullable blob");
			Assert.AreEqual(default(TestVec3), thing.Position.Get(), "small custom");
		}

		[Test, Category("Persistence")]
		public async Task SetCustomFields_SurviveRestart()
		{
			CreateServer();
			await ConnectLocal();
			await CreateAndFlush(e =>
			{
				e.Box.Set(new TestBox(7));
				e.SmallBox.Set(new TestBox(-3));
				e.Blob.Set(new ArraySegment<byte>(new byte[] { 1, 2, 3 }));
				e.Position.Set(new TestVec3(1, 2, 3));
			});

			StopServer();
			await Restart();
			PersistedCustomEntity thing = await ReloadThing();

			Assert.AreEqual(new TestBox(7), thing.Box.Get());
			Assert.AreEqual(new TestBox(-3), thing.SmallBox.Get());
			CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, thing.Blob.Get());
			Assert.AreEqual(new TestVec3(1, 2, 3), thing.Position.Get());
		}

		[Test, Category("Persistence")]
		public async Task EmptyBlob_SurvivesRestartAsEmptyNotNull()
		{
			CreateServer();
			await ConnectLocal();
			await CreateAndFlush(e => e.Blob.Set(new ArraySegment<byte>(new byte[0])));

			StopServer();
			await Restart();
			PersistedCustomEntity thing = await ReloadThing();

			Assert.IsNotNull(thing.Blob.Get().Array, "empty is not null");
			Assert.AreEqual(0, thing.Blob.Get().Count);
		}

		// ───────── A row that can't be read ─────────

		[Test, Category("Persistence")]
		public async Task UnreadableRow_FailsTheLoadNamingEntityAndProperty()
		{
			CreateServer();
			await ConnectLocal();
			await CreateAndFlush(e => e.Box.Set(new TestBox(7)));

			StopServer();
			OverwritePersistedProperty("box", "not binary");
			await Restart();

			ImpunityErrorResponseException error = await PumpExpectingError(
				LocalGame.EntityManager.SubscribeToChannelAsync<MigTestChannel>(ChannelName, null), LocalGame);

			Assert.AreEqual(ImpunityErrorCode.InternalServerError, error.ErrorId);
			StringAssert.Contains("'box'", error.Message, "names the property");
			StringAssert.Contains("'thing'", error.Message, "names the entity");
			StringAssert.Contains("'" + ChannelName + "'", error.Message, "names the channel");
			StringAssert.Contains("String", error.Message, "says what was stored");
			StringAssert.Contains("CustomNullable", error.Message, "says what was expected");
			StringAssert.Contains("FromBson", error.ServerStacktrace, "the server's stack, down to the original throw");
			StringAssert.Contains("[server", error.StackTrace, "StackTrace carries the server's half");
		}

		// ───────── The pieces ─────────

		[Test]
		public void FromBson_NullIsDefault_BinaryIsItself_AnythingElseThrows()
		{
			Assert.IsNull(ServerCustomFramingSerializer.FromBson(BsonValue.Null).Array);
			Assert.IsNull(ServerCustomFramingSerializer.FromBson(null).Array);

			ArraySegment<byte> bytes = ServerCustomFramingSerializer.FromBson(new BsonValue(new byte[] { 9, 8 }));
			CollectionAssert.AreEqual(new byte[] { 9, 8 }, bytes);

			Assert.Throws<InvalidCastException>(() => ServerCustomFramingSerializer.FromBson("text"));
		}

		[Test]
		public void ErrorResponse_FromAWrappingException_KeepsTheInnerStack()
		{
			ImpunityErrorResponse response;
			try
			{
				try
				{
					throw new NullReferenceException("deep down");
				}
				catch (Exception inner)
				{
					throw new ImpunityServerException(ImpunityErrorCode.InternalServerError, "named failure", inner);
				}
			}
			catch (ImpunityServerException e)
			{
				response = new ImpunityErrorResponse(e);
			}

			Assert.AreEqual(ImpunityErrorCode.InternalServerError, response.ErrorCode);
			Assert.AreEqual("named failure", response.Message);
			StringAssert.Contains("---> NullReferenceException: deep down", response.Stacktrace);
		}

		[Test]
		public void ResponseException_StackTrace_PutsTheServerStackFirst()
		{
			ImpunityErrorResponse response;
			try
			{
				throw new InvalidOperationException("on the server");
			}
			catch (Exception e)
			{
				response = new ImpunityErrorResponse(e);
			}

			ImpunityErrorResponseException thrown = null;
			try
			{
				throw new ImpunityErrorResponseException(response);
			}
			catch (ImpunityErrorResponseException e)
			{
				thrown = e;
			}

			string stack = thrown.StackTrace;
			StringAssert.StartsWith("[server, InternalServerError]", stack);
			StringAssert.Contains(response.Stacktrace, stack);
			StringAssert.Contains("[client]", stack);
			Assert.Less(stack.IndexOf(response.Stacktrace, StringComparison.Ordinal), stack.IndexOf("[client]", StringComparison.Ordinal));
		}

		[Test]
		public void ResponseException_WithoutAServerStack_IsUnchanged()
		{
			var response = new ImpunityErrorResponse(ImpunityErrorCode.ActionNotFound, "gone");
			var e = new ImpunityErrorResponseException(response);

			Assert.IsNull(e.StackTrace, "never thrown, no server stack: nothing to show");
		}
	}
}
