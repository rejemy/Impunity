# Impunity

Networked multiplayer save-game and distributed object library for Unity. Provides TCP networking, a key-value document database, and real-time state replication via channels and entities.

## Repository Structure

| Directory | Target | Description |
|-----------|--------|-------------|
| `ImpunityCodeGenerator/` | netstandard2.0 | Roslyn source generator — produces serialization helpers for distributed entity types |
| `ImpunityRuntime/` | netstandard2.1 | Core shared library (networking, serialization, game state) — used by both client and server. This is just the project file to build the dll, the actual code lives ImpunityUnity/Assets/Scripts/Impunity for ease of testing in Unity |
| `ImpunityStandaloneServer/` | net10.0 | ASP.NET Core standalone server with WebSocket (incomplete) + TCP transport, can host multiple Impunity server instances |
| `ImpunityUnity/` | Unity 6 (netstandard2.1) | Unity project containing client connection code, distributed field types, and test scenes |

Server-side game state code lives in `ImpunityUnity/Assets/Scripts/Impunity/Server/` and is linked into `ImpunityStandaloneServer` via glob includes in its `.csproj`.

## Build

```bash
# Build everything (CodeGenerator → Runtime → StandaloneServer)
./build.sh

# Or build individually
cd ImpunityCodeGenerator && ./build.sh   # outputs to bin/ and ImpunityUnity/Assets/Plugins/
cd ImpunityRuntime && ./build.sh         # builds Release + Debug, outputs to bin/Runtime/
cd ImpunityStandaloneServer && ./build.sh # outputs to bin/StandaloneServer/
```

All projects use `dotnet build -c Release`. The Unity project is opened via Unity Editor (version 6.3.9f1).

## Architecture

> In-depth manual for the distributed entity/channel system (annotations & ids, client↔server flow, subscriptions, persistence, client-authoritative, locks, temporal fields, local-only setters): [`docs/guides/DistributedEntities.md`](docs/guides/DistributedEntities.md).
>
> Companion manual for the connection & database model (local vs. remote connections, the connect handshake, the action request/reply system, the `Update()` loop, the document database, named locks, broadcasts): [`docs/guides/Connections.md`](docs/guides/Connections.md).
>
> Draft/WIP guide to schema versioning & migration (version+checksum guard, adopt-when-safe, standalone-server constraints; large "not yet implemented" + "open questions" sections — the data-migration system is mostly unbuilt): [`docs/guides/SchemaMigration.md`](docs/guides/SchemaMigration.md).

- **Wire protocol**: 4-byte length prefix + 12-byte header + BSON body over TCP
- **Serialization**: UltraLiteDB's `BsonMapper` for documents; custom binary serializers (readonly structs) for distributed field types
- **Request/reply pattern**: Client sends `ClientAction` → server processes → server replies or pushes `ServerAction` messages
- **State replication**: Clients subscribe to *channels*; channels contain *entities* with typed *distributed fields* (`DistributedValue`, `DistributedArray`, `DistributedQueue`, `DistributedIntDictionary`, `DistributedStringDictionary`). Fields track dirty bits and serialize only deltas.
- **Server threads**: TCP listener, UDP listener, network writer, DB worker, live state worker — coordinated via concurrent queues
- **Socket thread shutdown**: All socket listener threads use non-blocking checks (`Socket.Poll()` for UDP, `TcpListener.Pending()` for TCP accept, `ReceiveTimeout` for TCP stream reads) so they can poll `ImpunityLifecycle.ShuttingDown` and exit cleanly within ~1 second. Call `ImpunityLifecycle.CleanupAll()` at app shutdown to signal all threads to stop. This avoids a macOS deadlock where closing a socket from another thread does not reliably unblock a `Receive()` call.

## Key Namespaces

- `Impunity` — shared types, logging, utilities
- `Impunity.Networking` — TCP/UDP client and server transports
- `Impunity.GameState` — server-side game state, DB, live state management
- `Impunity.Connection` — client-side connection API, entity manager, distributed field types
- `Impunity.Unity` — Unity-specific adapters (MonoBehaviour bases, coroutine yields, Unity type serializers)

## Dependencies

- **UltraLiteDB** — embedded BSON document database (DLL in `ImpunityUnity/Assets/Plugins/`)
- **Microsoft.CodeAnalysis.CSharp** 3.8.0 — used by code generator only

## Conventions

- All client-facing APIs use callback delegates (`ImpunityCallback<T>`), with async/await and Unity coroutine yield extensions provided separately
- netstandard2.1 compatibility is required for all runtime code (Unity constraint)
- Public APIs have XML doc comments (`/// <summary>`)
- Distributed entity types are annotated with `[DistributedEntity]` and fields with `[Distributed]`

## Tests

### NUnit Tests (Unity Test Runner)

Tests live in `ImpunityUnity/Assets/Tests/` with two subdirectories:

| Directory | Assembly | Mode | Description |
|-----------|----------|------|-------------|
| `Tests/Unit/` | `Tests` (Editor-only) | Edit Mode | Serializer round-trips, utility functions, sequence number logic, buffer pool |
| `Tests/PlayMode/` | `PlayModeTests` | Play Mode | Integration tests with real servers and clients — database CRUD, TCP connections, live channels, entity replication, broadcasts, locks, distributed collections |

**Assembly definitions:**
- `ImpunityUnity/Assets/Scripts/Impunity/Impunity.asmdef` — core library assembly, referenced by both test assemblies
- `Tests/Unit/Tests.asmdef` — Editor-only, references `Impunity` + `UltraLiteDB.dll`
- `Tests/PlayMode/PlayModeTests.asmdef` — all platforms (required for Play Mode), references `Impunity` + `UltraLiteDB.dll`

**Running tests:** Open Unity → Window → General → Test Runner. Edit Mode tests run immediately; Play Mode tests enter play mode to execute.

**Play Mode test patterns:**
- Tests use `[UnityTest]` returning `IEnumerator` for coroutine-based async
- Real `GameStateServer`, `ImpunityServer`, `LocalGameConnection`, and `RemoteGameConnection` — no mocks
- `connection.Update()` must be called each frame; helper methods `WaitForYield()` and `TickUntil()` handle this
- Each test creates a fresh server with a unique temp directory, cleaned up in `[TearDown]`
- Test entity types (`IntegrationTestEntity`, `IntegrationTestChannel`) are defined in the test file using only non-Unity serializer types
- After subscribing to a channel, tick several frames before modifying collection fields (`DistributedQueue`, `DistributedIntDictionary`, `DistributedStringDictionary`) so the initial `NewValue` is flushed and subsequent changes go through the delta `Changes` path

### Legacy Test Component

`ImpunityUnity/Assets/Scripts/Test/` contains `ImpunityTestComponent.cs` (MonoBehaviour) and `ImpunityTestClasses.cs` (entity type definitions) — an older manual test harness run via a Unity scene. Uses log-based verification rather than assertions.
