# Impunity

Networked multiplayer save-game and distributed object library for Unity. Provides TCP networking, a key-value document database, and real-time state replication via channels and entities.

## Repository Structure

| Directory | Target | Description |
|-----------|--------|-------------|
| `ImpunityCodeGenerator/` | netstandard2.0 | Roslyn source generator — produces serialization helpers for distributed entity types |
| `ImpunityRuntime/` | netstandard2.1 | Core shared library (networking, serialization, game state) — used by both client and server. This is just the project file to build the dll, the actual code lives ImpunityUnity/Assets/Scripts/Impunity for ease of testing in Unity |
| `ImpunityStandaloneServer/` | net10.0 | ASP.NET Core standalone server with WebSocket + TCP transport, can host multiple Impunity server instances |
| `ImpunityTests/` | net10.0 | NUnit test project runnable with plain `dotnet test` — compiles the shared test sources against the non-Unity runtime; also hosts the out-of-proc standalone-server transport tests |
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

Run the test suite with `./test.sh` (see Tests below).

## Architecture

> In-depth manual for the distributed entity/channel system (annotations & ids, client↔server flow, subscriptions, persistence, client-authoritative, locks, temporal fields, local-only setters): [`docs/guides/DistributedEntities.md`](docs/guides/DistributedEntities.md).
>
> Companion manual for the connection & database model (local vs. remote connections, the connect handshake, the action request/reply system, the `Update()` loop, the document database, named locks, broadcasts): [`docs/guides/Connections.md`](docs/guides/Connections.md).
>
> Guide to schema versioning & migration (version+checksum guard, adopt-when-safe, and the implemented client-driven **data migration** flow: a higher-version client is *offered* a migration, runs it via raw name-addressed DB ops through `MigrationContext`, then commits — with server-side snapshot/restore, an ephemeral lock, idle timeout, and crash recovery): [`docs/guides/SchemaMigration.md`](docs/guides/SchemaMigration.md).

- **Wire protocol**: 4-byte length prefix + 12-byte header + BSON body over TCP
- **Serialization**: UltraLiteDB's `BsonMapper` for documents; custom binary serializers (readonly structs) for distributed field types
- **Request/reply pattern**: Client sends `ClientAction` → server processes → server replies or pushes `ServerAction` messages
- **State replication**: Clients subscribe to *channels*; channels contain *entities* with typed *distributed fields* (`DistributedValue`, `DistributedArray`, `DistributedQueue`, `DistributedIntDictionary`, `DistributedStringDictionary`). Fields track dirty bits and serialize only deltas.
- **Entity locks**: server-enforced exclusive claims, with a FIFO waiter queue — releasing a lock hands it straight to the longest-waiting connection (`EntityLockGranted` push) rather than freeing it for a re-race. `entity.RunExclusive(body, onComplete, timeout)` is the scoped form: it queues for the lock, runs the body, flushes the body's edits *before* sending the unlock, and releases on every exit path. That flush ordering is what guarantees the next holder observes the previous holder's edits (guaranteed fields only — `SetUnguaranteed` writes leave the ordered path). Client code in `Client/Connection/ClientEntityExclusive.cs`; server queue in `GameStateEntity.Lock`/`Unlock`.
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

### dotnet test suite (primary)

```bash
./test.sh                              # full suite (~35s)
./test.sh --filter "Category!=Slow"    # skip wall-clock reaper/migration-recovery tests
./test.sh --filter "Category=Transport"  # just the transport matrix
```

`ImpunityTests/ImpunityTests.csproj` (net10.0, NUnit 3.14) is the primary way to run tests — no Unity editor needed. Building it automatically builds the code generator (wired as a Roslyn analyzer so `[DistributedEntity] partial` test types get their serialization helpers), the non-Unity `ImpunityRuntime`, and the standalone server (build-only reference — never a compile reference, its types would collide with `ImpunityRuntime`'s).

**Shared test sources** live in `ImpunityUnity/Assets/Tests/Shared/` and are compiled by BOTH the dotnet csproj (glob include) and Unity (asmdef `ImpunitySharedTests`, shows in the Play Mode tab) — the same source-sharing pattern the runtime uses. Portability rules for everything under `Shared/`:
- No `using UnityEngine` — `Harness/TestEnv.cs` holds the only `#if UNITY_5_3_OR_NEWER` (temp root, error log). The dotnet build enforces this at compile time.
- Tests are plain NUnit `[Test] async Task` methods driving the async API (`GameConnectionAsyncExt`); never `.Result`/`.Wait()`/`ConfigureAwait(false)` (Unity main-thread deadlock), and stay within Unity's NUnit 3.5 API surface (no `Assert.Multiple`).
- `Harness/ImpunityTestHarness.cs` is the shared base fixture: per-test temp dir + free ports (`TestPorts` probes TCP+UDP; `ImpunityServer` binds both), `CreateServer()`/`ConnectLocal()`/`StartTCPAndConnectRemote()`, and the pump helpers `Pump(task)` / `PumpUntil(cond)` / `PumpFor(dur)` / `PumpExpectingError(task)` that drive `connection.Update()` (Stopwatch deadlines — NUnit `[Timeout]` is not enforced on .NET Core). Passing explicit connections to a pump is how a test keeps a client "stale"; no arguments pumps every tracked connection.
- `Harness/SharedTestEntities.cs` holds the test entity types (all `partial`, codegen-dependent), including `TestVec3` — a portable stand-in matching Unity `Vector3Serializer`'s wire shape (CustomSmall, 12 bytes).

**Suites:** `CoreUnitTests` (serializers/util), `BsonSerializationTests` (persisted-field BSON path), `IntegrationTests` (DB CRUD, channels, entities, broadcasts, locks, collections, unsubscribe, exclusive updates, delete-on-disconnect), `ExclusiveScopeTests` (`RunExclusive`: handoff guarantee, FIFO ordering, timeout, exception safety, same-client serialization, release on disconnect), `MigrationTests`, `ChannelCleanupTests` (idle reaper, `[Category("Slow")]`), `ChannelListingTests`, and `TransportSuite` — an abstract transport-agnostic battery with four legs: `_Local`, `_EmbeddedTcp` (shared, run in Unity too), `_StandaloneTcp`, `_StandaloneWs` (dotnet-only, in `ImpunityTests/Host/` — launch the real standalone binary out-of-proc via `StandaloneServerFixture` with a generated `config.json`, `/info` readiness probe, and kill-tree teardown).

**Unity-only tests** live in `ImpunityUnity/Assets/Tests/Unity/`:
- `Unity/Editor/` (asmdef `ImpunityUnityEditorTests`, Edit Mode): Unity type serializer round-trips (binary + BSON) and Unity-typed entity BSON interop.
- `Unity/PlayMode/` (asmdef `ImpunityUnityPlayModeTests`): `YieldWrapperTests` — coroutine coverage of the `ImpunityYield` / `...Yield()` API, which the shared suites no longer exercise. Reuses the shared harness + entity types.

**Running in Unity:** Window → General → Test Runner. The shared suites appear in the Play Mode tab (Mono runtime coverage); Unity-only Edit Mode tests in the Edit Mode tab.

**Known gotchas preserved from the original suites:**
- `SubscribeToChannel*` discards the `createIfNeeded` instance — drive the *returned* channel. (`CreateObject*` keeps the caller's instance.)
- Non-client-authoritative `Set()` applies only on server echo — assert via `PumpUntil`, not synchronously.
- All `LocalGameConnection`s share the hard-coded `ConnectionKey` `"local_key"`, so the server treats them as the same client for lock ownership; tests needing two distinct local clients assign unique `ConnectionKey`s before connecting.
- Known intermittent issue under full-suite load (~1 in 6–10 full runs): a second subscriber's `SubscribeChannelAction` over TCP occasionally gets no reply within `ActionTimeoutMillis` — tracked as a library race to investigate, not a harness bug.
- Tests that need a scope to stay open while another client queues behind it use the `TaskCompletionSource` "gate" pattern (see `ExclusiveScopeTests`): the body awaits the gate, and the test completes it to release the lock. To exercise the flush-before-unlock ordering the body must write *after* the gate — writing before it lets the ordinary per-frame sweep flush the update while the test pumps, which hides the bug.

### Legacy Test Component

`ImpunityUnity/Assets/Scripts/Test/` contains `ImpunityTestComponent.cs` (MonoBehaviour) and `ImpunityTestClasses.cs` (entity type definitions) — an older manual test harness run via a Unity scene. Uses log-based verification rather than assertions.
