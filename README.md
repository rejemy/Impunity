# Impunity

A networked multiplayer and persistent game state library for Unity. Impunity gives your game a flexible multiplayer model that works for LAN play between friends or dedicated server hosting like Minecraft and Terraria — with a powerful document database, real-time state replication, and a networking stack built from the ground up for games.

## Features

- **Document database** — Save and load game data with a full CRUD API backed by an embedded BSON database. Supports compound (atomic) operations across multiple collections.
- **Live state replication** — Distributed objects replicate efficiently between clients using delta serialization and dirty-bit tracking, with minimal garbage generation.
- **Client authoritative and predicted properties** — Distributed fields support both server-authoritative and client-authoritative modes, so you can choose the right model per property.
- **Flexible async APIs** — Every operation works with callbacks, `async/await`, or Unity coroutines. Mix and match freely.
- **Custom TCP + UDP networking** — Purpose-built protocol with a compact wire format for fast, reliable state replication.
- **Even works in WebGL** — Works in WebGL using WebSockets (though state replication will not be as low-latency)
- **Built for live games** - Built in tools to version and migrate your game's data as it evolves over time
- **Named locks** — Distributed locking API prevents multiplayer race conditions with try-lock and wait-for-lock semantics.
- **Broadcasts** — Send typed messages to all connected clients.
- **Persistence** — Distributed entities can be marked as persisted to automatically save and restore their state across server restarts.
- **Wealth of distributed types** - Distributed objects can contain all basic C# and Unity types, as well as arrays, dictionaries and queus.
- **One dependency** — The only external dependency is [UltraLiteDB](https://github.com/rejemy/UltraLiteDB), an embedded BSON document database.
- **Works with any recent Unity** version, including Unity 6.
- **Includes standalone server** Standalone server is a Dotnet 10 executable that can run on Linux, Windows or macOS.

## Quick Start

### Installation

Copy ImpunityRuntime.dll, ImpunityCodeGenerator.dll, ImpunityWebSocket.jslib and UltraLiteDB.dll into your project's Assets/Plugins directory
In Unity, add the Asset Label "RoslynAnalyzer" to  ImpunityCodeGenerator.dll 

### Define your distributed types

Annotate your types with `[DistributedEntity]` and `[Distributed]` — the source generator handles the rest.

```csharp
[DistributedEntity(1)]
public partial class Player : DistributedEntityBase
{
    public enum Props : byte { HEALTH = 1, NAME = 2 }

    [Distributed((byte)Props.HEALTH)]
    public DistributedValue<int, Int32Serializer> Health;

    [Distributed((byte)Props.NAME)]
    public DistributedValue<string, StringSerializer> DisplayName;

    public Player() { InitializeDistributedFields(); }
}

[DistributedEntity(2)]
public partial class GameWorld : DistributedChannelBase
{
    public enum Props : byte { STATUS = 1, CHAT = 2 }

    [Distributed((byte)Props.STATUS)]
    public DistributedValue<string, StringSerializer> Status;

    [Distributed((byte)Props.CHAT)]
    public DistributedQueue<string, StringSerializer> Chat;

    public GameWorld() { InitializeDistributedFields(); }
}
```

### Set up a server

```csharp
var format = new GameStateFormat(
    1,
    new GameStateCollection[]
    {
        new GameStateCollection { Index = 10, Name = "Inventory" }
    },
    new Type[] { typeof(Player), typeof(GameWorld) }
);

var gameServer = GameStateServer.Create("mygame", "password", dataPath, summary, options);
gameServer.UpdateFormat(new GameStateFormatData(format, entityDefs), false);

// For dedicated server hosting
var server = new ImpunityServer(gameServer, options);
server.Start();
```

### Connect a client

```csharp
// TCP connection to a dedicated server
var connection = RemoteGameConnection.MakeTCPRemoteConnection(
    server.TCPEndpoint, "mygame", "password", format, options);
await connection.ConnectAsync();

// WebSocket — works in WebGL builds!
var connection = RemoteGameConnection.MakeWebsocketRemoteConnection(
    "localhost", 29653, "mygame", "password", format, options);
await connection.ConnectAsync();

// In-process — great for single-player with saves, or LAN hosting
var connection = new LocalGameConnection(gameServer, format);
await connection.ConnectAsync();
```

### Use the database

```csharp
// Insert
var doc = new BsonDocument { ["_id"] = "sword_01", ["name"] = "Iron Sword", ["damage"] = 15 };
await connection.InsertDocumentAsync(inventoryCollection, doc);

// Query
var sword = await connection.FindDocumentByIdAsync(inventoryCollection, "sword_01");

// List all
var allItems = await connection.ListDocumentsAsync(inventoryCollection);

// Atomic compound operations
await connection.CompoundDatabaseActionAsync(new GameStateActionBase[]
{
    new UpsertDocumentAction(inventoryCollection, doc1),
    new UpsertDocumentAction(inventoryCollection, doc2),
});
```

### Replicate game objects

```csharp
// Host creates a channel and spawns an entity
var world = new GameWorld();
world.Status.Set("active");
await connection.EntityManager.SubscribeToChannelAsync("world", world);

var player = new Player();
player.Health.Set(100);
player.DisplayName.Set("Hero");
await connection.EntityManager.CreateObjectAsync(player, world, false);

// Another client subscribes and sees the replicated state
var world = await otherClient.EntityManager.SubscribeToChannelAsync<GameWorld>("world", null);
// Entities appear in world.DistributedObjects as they replicate

// React to property changes
player.Health.OnChanged += (oldVal, newVal) =>
{
    Debug.Log($"Health: {oldVal} -> {newVal}");
};
```

### Prevent race conditions with locks

```csharp
bool acquired = await connection.TryToLockAsync("chest_42");
if (acquired)
{
    // Safe to modify — no other client holds this lock
    // ...
    await connection.UnlockAsync("chest_42");
}

// Or wait until the lock becomes available
await connection.WaitForLockAsync("chest_42");
```

### Unity coroutine style

Every async API also works as a Unity coroutine yield:

```csharp
yield return connection.ConnectYield();
yield return connection.InsertDocumentYield(collectionId, doc);

var findYield = connection.FindDocumentByIdYield(collectionId, "sword_01");
yield return findYield;
BsonDocument result = findYield.Value;
```

## Distributed Field Types

| Type | Description |
|------|-------------|
| `DistributedValue<T, S>` | Single replicated value |
| `DistributedArray<T, S>` | Indexed array with per-element change tracking |
| `DistributedQueue<T, S>` | FIFO queue — ideal for chat or event logs |
| `DistributedIntDictionary<T, S>` | Dictionary with integer keys |
| `DistributedStringDictionary<T, S>` | Dictionary with string keys |

All field types fire `OnChanged` callbacks and serialize only deltas over the wire.

> **📖 Full guide:** [docs/guides/DistributedEntities.md](docs/guides/DistributedEntities.md) covers the distributed entity system in depth — the annotation/id system, client→server→client flow, subscriptions, persistence, client-authoritative objects, locks, and niche features like temporal fields and local-only setters.

## Architecture

Impunity uses a channel-based replication model:

- **Channels** are named containers that clients subscribe to (a game world, a room, a match)
- **Entities** live inside channels and hold distributed fields that replicate automatically
- **The server** manages game state, runs the database, and routes updates between clients
- **Transports**: TCP (primary, reliable), UDP (fast, unreliable), and WebSocket (for WebGL)

Server threads (TCP listener, UDP listener, network writer, DB worker, live state worker) coordinate via concurrent queues for high throughput.

## Building

```bash
# Build everything (CodeGenerator → Runtime → StandaloneServer)
./build.sh

# Or individually
cd ImpunityCodeGenerator && ./build.sh
cd ImpunityRuntime && ./build.sh
cd ImpunityStandaloneServer && ./build.sh
```

## Project Structure

| Directory | Description |
|-----------|-------------|
| `ImpunityCodeGenerator/` | Roslyn source generator for distributed entity serialization |
| `ImpunityRuntime/` | Core shared library — networking, serialization, game state |
| `ImpunityStandaloneServer/` | ASP.NET Core dedicated server with TCP + WebSocket transport |
| `ImpunityUnity/` | Unity project with client code, distributed types, and tests |

## License

MIT
