# Connections & the Database

A guide to how a client talks to an Impunity server: the two kinds of **connection**, the **handshake** that opens one, the **action** request/reply system every call rides on, the document **database**, **named locks**, and **broadcast** messaging.

This is the companion to [`DistributedEntities.md`](DistributedEntities.md). That document covers real-time state replication (channels, entities, distributed fields); this one covers everything *underneath* it — getting connected, the per-frame pump, and the non-entity APIs. Where the two overlap (creating channels, entity locks, events) this doc points back to the entity guide. See the project `CLAUDE.md` for the build/run layout and the wire protocol.

> **Conventions used here.** "Connection" means a `BaseGameConnection` — concretely a `RemoteGameConnection` (over a network transport) or a `LocalGameConnection` (an embedded in-process server). "Action" means a `GameStateActionBase`. "The server" means a `GameStateServer`. Code samples use the public API from `Impunity.Connection`.

---

## Contents

1. [Mental model](#1-mental-model)
2. [Local vs. remote connections](#2-local-vs-remote-connections)
3. [Defining your format](#3-defining-your-format)
4. [Opening a connection: the handshake](#4-opening-a-connection-the-handshake)
5. [The update loop and threading](#5-the-update-loop-and-threading)
6. [The action system: request → reply](#6-the-action-system-request--reply)
7. [The document database](#7-the-document-database)
8. [Named locks](#8-named-locks)
9. [Broadcast messages](#9-broadcast-messages)
10. [Server time and the game summary](#10-server-time-and-the-game-summary)
11. [Errors](#11-errors)
12. [Quick reference](#12-quick-reference)
13. [Known caveats](#13-known-caveats)

---

## 1. Mental model

A connection is a **typed message pipe** to one game world on a server. Almost everything you do — read a document, subscribe to a channel, take a lock, send a broadcast — is expressed as an **action** that you hand to the connection. The connection sends it to the server; the server runs it and (usually) sends back a reply; the reply is delivered to your **callback**.

The one rule that ties it all together: **you drive the connection by calling `connection.Update()` once per frame.** Outbound work is flushed and inbound replies/pushes are dispatched to your callbacks *during that call*, on the calling thread. Nothing happens between `Update()` calls.

```
   Your code                         BaseGameConnection                      GameStateServer
   ─────────                         ──────────────────                      ───────────────
   conn.InsertDocument(…, cb) ─────▶ build action, DoAction()
                                       └─ queue outbound ───────(transport)────▶ run on DB / Live thread
                                                                                    │
   conn.Update()  (each frame)                                                      │ reply
     ├─ flush dirty entities                                                        ▼
     └─ drain completed queue ◀──────  enqueue reply  ◀────────(transport)──── send result
          └─ invoke cb(err, id)   ← on the main thread
```

The same shape holds for both connection types. The only thing that changes is what sits in the middle: a socket (remote) or a direct in-process queue (local).

---

## 2. Local vs. remote connections

Both implement the same `BaseGameConnection` API, so application code is identical. They differ only in transport.

| | `LocalGameConnection` | `RemoteGameConnection` |
|---|---|---|
| Server | An embedded `GameStateServer` in the same process | A separate process, reached over the network |
| Transport | Actions queued directly to the server — **no serialization** | BSON over TCP (with optional UDP for unguaranteed sends), or WebSocket |
| Threads | Server's own DB/Live worker threads | A background writer thread + the transport's socket reader thread |
| Unguaranteed sends | Not supported (everything is reliable) | Supported once UDP is negotiated |
| Action timeouts | None (a stuck action waits forever) | Yes — `ImpunityOptions.ActionTimeoutMillis` (default 5 s) |
| `IsRemote` | `false` | `true` |

Use a **local** connection for single-player, for a listen-server host that also plays, and for tests — the integration tests run real servers and local connections with no mocks. Use a **remote** connection for clients joining a hosted world.

> **Local connections share payloads by reference.** Because nothing is serialized, the `BsonDocument`s and property blobs you pass into a call are the *same instances* the server reads. Don't mutate a document after handing it to a DB call on a local connection. (Over a remote connection the copy is implicit — everything is serialized.)

---

## 3. Defining your format

Both ends agree on a `GameStateFormat`: a schema **version**, the **collections** (named document tables) clients may use, and the distributed **entity types**.

```csharp
var format = new GameStateFormat(
    version: 1,
    collections: new[]
    {
        new GameStateCollection { Index = 10, Name = "Players" },
        new GameStateCollection { Index = 11, Name = "Items"   },
    },
    entityTypes: new[] { typeof(Player), typeof(Zone) });   // see DistributedEntities.md
```

- **Collection `Index` must be ≥ 10.** Indices 0–9 are reserved for internal use (index 1 is the live-entity store), and the format rejects anything lower. The index is the `collectionId` you pass to every database call (see [§7](#7-the-document-database)).
- `version` plus a content **checksum** (derived from the collections and entity types) identify the format on the wire. The server compares them at connect time to decide whether the client is compatible — see [§4](#4-opening-a-connection-the-handshake).
- The entity types are validated and registered with the connection's `ClientEntityManager` when you construct the connection. That's the live-state half; this guide doesn't repeat it.

---

## 4. Opening a connection: the handshake

Construct a connection, then call `Connect`. The callback fires once — with `null` on success, or an error.

```csharp
// Remote, over TCP:
var conn = RemoteGameConnection.MakeTCPRemoteConnection(
    serverEndpoint, gameId: "my-world", gamePassword: "hunter2", format);

conn.Connect(err =>
{
    if (err != null) { /* failed to connect */ return; }
    // Connected: conn.ConnectionId is assigned, the clock is synced, conn.Connected == true.
});
```

```csharp
// Local, against an embedded server:
var server = GameStateServer.OpenOrCreate("my-world", gamePassword: null, path, summary: null);
var conn   = new LocalGameConnection(server, format);
conn.Connect(err => { /* … */ });
```

There are factory helpers for each remote transport: `MakeTCPRemoteConnection` (by `IPEndPoint`, or by host + port) and `MakeWebsocketRemoteConnection` (host + port, for WebGL). All take the same `gameId`, `gamePassword`, `format`, and optional `ImpunityOptions` / `ClientEntityManager`.

### What the handshake does

1. **Transport connect** (remote only): the socket is opened on a background thread and the writer thread starts.
2. **Establish** — the client sends an establish-connection action carrying the `gameId`, the **hashed** password (SHA-256; never sent in the clear), and the format data. The server:
   - rejects the connection if it is in a no-new-connections state (`ServerUnavailable`);
   - **validates the format** (version + checksum) against its stored metadata;
   - if they don't match, **upgrades** to the client's format *only if it's safe* — the world is brand new (version 0) or this is the only connection. Otherwise it fails with `ServerVersionIncompatible`. A remote client additionally may not drive an upgrade unless the server opted in with `RemoteUpgradeAllowed`, and a world can never be reverted to an older version.
3. **Clock sync** — the client fetches the server clock and stores the offset (see [§10](#10-server-time-and-the-game-summary)).
4. On success the connection is marked `Connected` and your callback runs.

### Connection id and connection key

- **`ConnectionId`** is assigned by the server and reads `"unconnected"` until the handshake completes. It identifies this connection for the duration of the session (it is also the `sender` you see on broadcasts).
- **`ConnectionKey`** is a *client-generated* identifier (random by default; `"local_key"` for local connections). Set it **before** `Connect` to give the client a stable identity across reconnects — the server keys per-connection ownership (locks, client-authoritative entities) on it. Reusing a key on reconnect is how a returning client is recognized as the same client.

> **Server-side hosting and discovery** are out of scope here. A remote world is hosted by an `ImpunityServer` / the standalone server (see `CLAUDE.md`); clients can locate worlds via LAN UDP discovery or the HTTP discovery API, which yield `ServerInfo` records you can feed to the factory helpers.

---

## 5. The update loop and threading

Call `connection.Update()` on a regular cadence — typically once per frame from a Unity `MonoBehaviour.Update`, or in a loop for a headless client. A single call does three things, in order:

1. **Flush outbound entity state** — pending distributed-field changes are serialized and sent (`EntityManager.SendUpdates()`).
2. **Periodic clock resync** — roughly once a minute.
3. **Dispatch the inbound queue** — every reply and server push that has arrived since the last call is processed: action callbacks are invoked, and server pushes (entity creates/updates/events/locks/deletes, broadcasts, named-lock releases) are applied.

Because dispatch happens *inside* `Update()`, **all of your callbacks run on the thread that calls it** — the main/Unity thread — where it is safe to touch game state and UI. Inbound messages are received and deserialized on a background thread (a socket thread for remote; the server's worker threads for local) and parked on a thread-safe queue until then. A callback that throws is caught and logged, so one bad handler can't stall the queue.

The remote connection's `Update()` adds two transport chores before the shared steps above: on WebGL it pumps the send queue (there is no writer thread there), and on every platform it **times out** any reply-expecting action older than `ActionTimeoutMillis`, completing it with a `TimeoutError`.

---

## 6. The action system: request → reply

Every method on the connection is a thin wrapper that builds a `GameStateActionBase` and hands it to `DoAction`. You rarely touch actions directly, but understanding them explains the whole API's behavior.

### Callbacks, await, and coroutines

The native style is a callback whose first argument is an error (`null` means success):

```csharp
conn.FindDocumentById(collectionId, id, (err, doc) =>
{
    if (err != null) { /* handle */ return; }
    // use doc
});
```

Two ergonomic layers wrap that callback:

- **`async`/`await`** — extension methods like `FindDocumentByIdAsync` return a `Task<T>` that **throws `ImpuntyErrorResponseException`** on failure instead of handing you an error object.
- **Unity coroutines** — `…Yield` extensions return a yield instruction you can `yield return`, then read `.Value`. (These still require `Update()` to be running.)

```csharp
BsonDocument doc = await conn.FindDocumentByIdAsync(collectionId, id);   // throws on error
```

### Guaranteed vs. unguaranteed

Most actions are sent **guaranteed** (reliably, over TCP). A few high-frequency ones — entity property updates — can be sent **unguaranteed** (best-effort, over UDP) when you don't care about losing an intermediate frame. Unguaranteed delivery is only available on a remote connection that has negotiated UDP (a ping/pong exchange during connect); otherwise the send transparently falls back to TCP, and a local connection is always reliable. This matters mainly for distributed fields — see [`DistributedEntities.md`](DistributedEntities.md) §5/§12.

### Replies, ordering, and timeouts

A request that has a callback waits for exactly one reply; a callback-less request is flagged "no reply" so the server doesn't send one. Replies are matched to requests **positionally** — in send order — which is safe over the single ordered TCP stream. The reply timeout (`ActionTimeoutMillis`, remote only) is the only thing that completes a request the server never answers; a local connection has no timeout.

### Compound actions

You can batch several **database** actions into a single round trip with `CompoundDatabaseAction`. They run **sequentially on the server's DB thread** and return one result per sub-action, in order.

```csharp
conn.CompoundDatabaseAction(new GameStateActionBase[]
{
    new UpsertDocumentAction(itemsCollection, doc1),
    new UpsertDocumentAction(itemsCollection, doc2),
    new ListDocumentsAction(itemsCollection),
}, (err, results) =>
{
    // results[0], results[1], results[2] — inspect each for its own error/result
});
```

> **A compound action is a batch, not a transaction.** Sub-actions are applied one at a time with **no rollback** — an early failure does not undo earlier successes. If any sub-action fails, the top-level result also carries an `ActionCompoundFailure` error, while the per-action results tell you exactly which ones failed. Every sub-action must be a database operation, or the whole request is rejected with `ActionBadRequest`.

---

## 7. The document database

Each world has a small embedded document database (UltraLiteDB under the hood). You read and write **BSON documents** in the **collections** declared in your format ([§3](#3-defining-your-format)). Documents are keyed by their BSON `_id` field; an insert without one gets a server-assigned id.

### The raw API

All take a `collectionId` (the collection's `Index`) and deliver results via callback:

| Method | Returns | Notes |
|---|---|---|
| `InsertDocument(cid, doc, cb)` | the assigned `_id` | Fails on a duplicate id |
| `UpdateDocument(cid, doc, cb)` | `bool` — found & replaced | Matched by `_id` |
| `UpsertDocument(cid, doc, cb)` | `bool` — **`true` = inserted new, `false` = replaced existing** | |
| `MergeIntoDocument(cid, doc, cb)` | `bool` — merged (`false` if not found) | Copies the given fields over an existing doc; never inserts |
| `MergeInsertDocument(cid, doc, cb)` | `bool` — success | Merge if present, else insert |
| `FindDocumentById(cid, id, cb)` | the document, or `null` | |
| `DeleteDocument(cid, id, cb)` | `bool` — found & deleted | |
| `ListDocuments(cid, cb)` | all documents (empty list if none) | |

```csharp
var doc = new BsonDocument { ["_id"] = "sword", ["name"] = "Sword", ["power"] = 42 };
conn.UpsertDocument(itemsCollection, doc, (err, wasInserted) => { /* … */ });
```

Database operations run on the server's dedicated **DB worker thread**, separate from live-state work, so a slow query doesn't stall replication.

### The typed wrapper

`GameStateDBCollection<T>` is a thin, strongly-typed view over one collection. It maps your `T` to a `BsonDocument` on the way out and back on the way in, so you work in your own types:

```csharp
var players = new GameStateDBCollection<PlayerRecord>(conn, playersCollection);

players.InsertDocument(new PlayerRecord { Name = "Ada" }, (err, id) => { /* … */ });
players.FindDocumentById("ada", (err, record) => { /* record is a PlayerRecord */ });

// async and yield variants exist too:
List<PlayerRecord>? all = await players.ListDocumentsAsync();
```

The mapping is **client-side only** — the wire payload is always BSON — and uses `BsonMapper.Global` unless you pass your own mapper. Custom type registrations that affect *storage* must be made on both client and server, since the server (de)serializes documents with its own mapper.

---

## 8. Named locks

A **named lock** is a server-wide mutex keyed by an arbitrary string, independent of any entity. It's the tool for coordinating exclusive access to something that isn't a distributed entity — a spawn slot, a turn, a region of the world.

```csharp
conn.TryToLock("boss-spawn", (err, gotIt) =>
{
    if (gotIt) { /* we hold it; nobody else can take it until we Unlock */ }
});

conn.Unlock("boss-spawn", (err, released) => { });
```

- `TryToLock` is non-blocking: `true` if you now hold the lock, `false` if another connection does.
- `WaitForLock` tries to take it and, if it's busy, **defers** your callback until the lock is released, then fires with `LockWaitResult.Unlocked`.
- `Unlock` releases a lock you hold (`false` if you didn't hold it).
- A connection's named locks are **released automatically when it disconnects** (they're tracked as ephemeral, per-connection state — the same mechanism that backs delete-on-disconnect entities).

> **`WaitForLock` signals availability, it does not re-acquire.** When the lock frees, *every* waiter is notified at once and the wait list is cleared, so `Unlocked` means "it's free, race for it" — call `TryToLock` again to actually claim it (someone else may win). There is no timeout. See [§13](#13-known-caveats).

For locking a specific **live entity** (so the server rejects others' updates and deletes to it) use the entity lock API instead — `entity.TryLock` / `entity.Unlock`, covered in [`DistributedEntities.md`](DistributedEntities.md) §10.

---

## 9. Broadcast messages

A broadcast is a fire-and-forget, typed message relayed to **every connected client, including the sender**. It carries no stored state and isn't persisted — use it for transient, world-wide signals (a global announcement, a round-start cue).

```csharp
// send (no callback — fire and forget)
conn.SendBroadcastMessage(messageType: 1, msgBody: new BsonDocument { ["text"] = "Round starting!" });

// receive
conn.OnBroadcastMessage = (messageType, body, sender) =>
{
    // sender is the ConnectionId of the originating client
};
```

`OnBroadcastMessage` is invoked on the main thread during `Update()`, like every other callback. Because the sender receives its own broadcast, you can treat the handler as the single place that reacts to the message rather than acting locally at the send site.

For a message targeted at the subscribers of one entity's channel (rather than everyone), use an entity **event** instead — see [`DistributedEntities.md`](DistributedEntities.md) §11.

---

## 10. Server time and the game summary

### Server time

Several systems (notably temporal distributed fields) reason about *server* time. The connection learns the server's clock during the handshake and re-syncs periodically, then exposes:

```csharp
long nowOnServer = conn.GetServerTime();   // Unix milliseconds
```

This is local UTC plus the measured offset. The offset is computed simply as *server time − local time at the moment the reply arrived*, so it does **not** correct for network latency — expect accuracy on the order of the round-trip time, plus a little drift between resyncs.

### The game summary

Every world has a **summary** document — the small, public blob surfaced in discovery (map name, mode, player count, whatever you choose). Read and write it through the connection:

```csharp
conn.SetGameSummary(new BsonDocument { ["map"] = "Dust", ["mode"] = "CTF" }, err => { });
conn.GetGameSummary((err, summary) => { /* may be null if never set */ });
```

---

## 11. Errors

Failures are reported as an `ImpunityErrorResponse` — an `ErrorCode` (the `ImpunityErrorCode` enum), a human-readable `Message`, and an optional server `Stacktrace`. In callback style it's the first argument (`null` on success); in `async` style it's thrown as an `ImpuntyErrorResponseException`.

Codes you'll actually branch on:

| Code | Meaning |
|---|---|
| `TimeoutError` | No reply within `ActionTimeoutMillis` (remote only) |
| `ServerVersionIncompatible` | The client's format doesn't match the server and can't be upgraded |
| `ServerPasswordIncorrect` | Wrong world password |
| `ServerUnavailable` | The server is refusing new connections |
| `ActionUniqueNameExists` | An entity unique-name collision on create |
| `ActionBlockedByLock` | An update/delete was rejected because another connection holds the lock (also an exclusive update on a foreign-locked entity) |
| `ActionStaleData` | An `UpdateExclusive` was rejected because a written field changed since this client last saw it |
| `ActionNotFound` | The target entity/document doesn't exist |
| `ActionBadRequest` / `ActionInvalidParameter` | Malformed request (e.g. a non-DB action in a compound batch, a bad collection id) |
| `ActionCompoundFailure` | At least one sub-action of a compound action failed |

A **fatal** server error (e.g. an incompatible version at connect) closes the connection after reporting. Transport-level failures on a remote connection surface through `OnNetworkError` — note the caveat in [§13](#13-known-caveats) about *clean* disconnects.

---

## 12. Quick reference

### Open

```csharp
var conn = RemoteGameConnection.MakeTCPRemoteConnection(endpoint, gameId, password, format);
// or: new LocalGameConnection(GameStateServer.OpenOrCreate(...), format);
conn.ConnectionKey = "stable-id";        // optional, set before Connect for reconnect identity
conn.Connect(err => { /* connected */ });
```

### Per frame

```csharp
conn.Update();   // flush outbound, resync clock, dispatch callbacks/pushes on this thread
```

### Database (each also has `…Async` and `…Yield` variants)

`InsertDocument` · `UpdateDocument` · `UpsertDocument` · `MergeIntoDocument` · `MergeInsertDocument` · `FindDocumentById` · `DeleteDocument` · `ListDocuments` · `CompoundDatabaseAction` — or the typed `GameStateDBCollection<T>`.

### Coordination & messaging

`TryToLock` / `WaitForLock` / `Unlock` (named locks) · `SendBroadcastMessage` + `OnBroadcastMessage` · `GetServerTime` · `SetGameSummary` / `GetGameSummary`.

### Live state

Channels, entities, distributed fields, entity locks, events → use `connection.EntityManager` and see [`DistributedEntities.md`](DistributedEntities.md).

---

## 13. Known caveats

A few sharp edges in the current implementation (candidates for cleanup; the XML doc comments flag them inline too):

- **`GameStateDBCollection<T>.FindDocumentById` doesn't guard the not-found/error case** the way `ListDocuments` does — it maps the (possibly null) result unconditionally. Check `err` first and treat a null/default result as "not found".
- **A clean, server-initiated disconnect is invisible to application code.** Only socket *errors* raise `OnNetworkError`; a graceful close is merely logged (and its reason code is always 0). Don't rely on a callback to learn the server hung up gracefully.
- **`WaitForLock` does not re-acquire.** Its deferred `Unlocked` result means "the lock is free," not "you now hold it" — call `TryToLock` again. `LockWaitResult.Timeout` is defined but never produced (no timeout is applied), so a lock that never frees leaves the callback pending. Only one waiter per lock name per connection is tracked; a second `WaitForLock` for the same name replaces the first.
- **Reply matching is positional.** Replies are paired to requests in send order with no per-message id. This is correct over TCP, but if an action is timed out and dropped while its reply is still in flight, that late reply is matched to the next waiting action.
- **`GetServerTime` ignores network latency.** The clock offset doesn't subtract round-trip time, so it can be off by roughly the RTT plus drift between resyncs.

---

*This guide covers the essentials. For exact signatures and behavior, the XML doc comments on `BaseGameConnection`, `RemoteGameConnection`, `LocalGameConnection`, and `GameStateDBCollection` (`Client/Connection/…`), and the server's `GameStateServer` / `GameStateLive` / `GameStateDB`, are the authoritative reference.*
