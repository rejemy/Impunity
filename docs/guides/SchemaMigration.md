# Schema Migration & Versioning

How Impunity guards a saved game against incompatible client versions, how it adopts a new schema, and how a higher-version client **migrates** an older save's data.

> **Status: implemented.** The *versioning guard* (client and server agree on a schema **version + checksum**, and the server **adopts** a new schema when safe) and the **data migration** flow (a higher-version client is *offered* a migration, explicitly runs it through raw database operations, and commits, with the server snapshotting the world as a safety net) both exist. The flow is **client-driven**: Impunity coordinates (who may migrate, locking others out, snapshot/restore) but the game's own code performs every data transformation. See [§7](#7-the-migration-flow) for the built design.

This guide builds on [`Connections.md`](Connections.md) (the handshake and the action system) and [`DistributedEntities.md`](DistributedEntities.md) (especially the *durable id* discussion in its §2). Read those first.

---

## Contents

1. [The problem](#1-the-problem)
2. [The format: version + checksum](#2-the-format-version--checksum) — *(implemented)*
3. [The connect-time guard](#3-the-connect-time-guard) — *(implemented)*
4. [Adopting a new schema](#4-adopting-a-new-schema) — *(implemented)*
5. [What survives a schema change](#5-what-survives-a-schema-change)
6. [Standalone server mode](#6-standalone-server-mode)
7. [The migration flow](#7-the-migration-flow) — *(implemented)*
8. [Notes & remaining sharp edges](#8-notes--remaining-sharp-edges)
9. [Quick reference](#9-quick-reference)

---

## 1. The problem

A shipped game evolves. A patch adds a distributed field, removes a document collection, renames a property, introduces a whole new entity type. Meanwhile there is **saved data on disk** written by the old build, and there may be **other clients** still running the old build. Two things must not happen:

1. An old client and a new client must not both mutate the same world with mismatched assumptions about its shape (silent corruption).
2. New code must not read old data — or old code new data — as if it were its own shape (silent corruption, the worse kind).

Impunity's answer has two layers. The **versioning guard** (built) detects mismatch and refuses unsafe connections. **Migration** (mostly not built) would *transform* the stored data so the new build can keep using the old save. Today only the first layer exists; the second is the subject of most of this document.

---

## 2. The format: version + checksum *(implemented)*

When you construct a connection you hand it a `GameStateFormat` — a `version`, the document `collections`, and the distributed `entityTypes` (see [`Connections.md`](Connections.md) §3). From it the connection builds a `GameStateFormatData`, which carries two identifiers used for compatibility:

- **`Version`** — your integer schema version, straight from `GameStateFormat.version`.
- **`DataChecksum`** — an MD5 of the BSON-serialized `GameStateFormatData`, computed by `ImpunityUtil.MakeDataChecksum`. Because it hashes the *whole* serialized format, it changes if you touch **anything** structural: a collection's index or name, an entity type's id / name / `PersistAs`, or any property's id, name, value type, `PersistAs`, or temporal flag.

The server stores the currently-adopted version and checksum in its `GameMetadata` (`Version`, `DataFormatChecksum`), persisted in the database. The pair is what a client is checked against at connect time, and what the server advertises so clients can detect a mismatch *before* connecting:

- LAN/TCP discovery puts the checksum on `ServerInfo.GameStateFormatChecksum`.
- The standalone server's HTTP info API returns both `GameVersion` and `DataFormatChecksum` (see `StandaloneServerWorldInfo`).

> **Implication.** Two builds are "the same schema" iff their version **and** checksum match. The checksum does the real work; the version number is a human-facing label and the basis for the no-downgrade rule ([§4](#4-adopting-a-new-schema)). Note this means you *can* change the schema without bumping the version — the checksum will still differ and be treated as a change — but then the version number no longer distinguishes the two schemas, which defeats the downgrade guard. **Bump the version whenever the schema changes.**

---

## 3. The connect-time guard *(implemented)*

Every connection runs `GameStateServer.EstablishConnection` during its handshake. The relevant logic:

```
if (NewConnectionsDisabled)              → fatal: ServerUnavailable
if (a migration is offered or running)   → fatal: ServerMigrationInProgress   (world reserved for its owner)
if (!ValidateFormat(clientFormat)):      → version+checksum don't match the world's:
    if   world is brand new (Version == 0)         → adopt the client's format
    elif clientVersion > worldVersion:             → OFFER a migration (see §7), unless:
            remote client and !RemoteUpgradeAllowed   → fatal: ServerVersionIncompatible
            any other client is connected             → fatal: ServerVersionIncompatible
    elif no other client is connected              → adopt the client's format
    else                                           → fatal: ServerVersionIncompatible
ConnectionOpened(...)
```

`ValidateFormat` is a strict equality check on both version and checksum. So a connecting client is in one of these situations:

| Situation | Result |
|---|---|
| Format matches the world | Connects normally. |
| Format differs, world brand new (or same/lower version with no other clients) | The world **adopts** the client's format ([§4](#4-adopting-a-new-schema)), then the client connects. |
| **Client version is *higher*, eligible, world has no other clients** | The client is **offered a migration** ([§7](#7-the-migration-flow)) — it connects, but must explicitly run or decline it. Nothing is changed yet. |
| Client version higher but ineligible (remote without `RemoteUpgradeAllowed`) or other clients present | Rejected with a fatal `ServerVersionIncompatible`. |
| A migration is already offered/running | Rejected with `ServerMigrationInProgress`. |

So: a matching client connects; a higher-version client into an empty world is offered a migration; everyone else is turned away. Existing clients are **never** evicted to make room for a migration.

---

## 4. Adopting a new schema *(implemented — metadata only)*

When the guard decides adoption is safe it calls `GameStateServer.UpdateFormat`, which:

1. Refuses to **downgrade** — `format.Version < Metadata.Version` is a fatal error ("Can't revert savegame to earlier version").
2. For a remote client, refuses to change the format at all unless the server opted in with `ImpunityOptions.RemoteUpgradeAllowed` (fatal otherwise). A `LocalGameConnection` is always allowed.
3. Updates the in-memory live `SetFormat(...)` and the `GameMetadata` (version, checksum, collections, entity types).
4. Queues an `UpdateDBFormatAction`, which on the DB thread calls `DB.SetFormat(collections)` and `SaveMetadata(...)`.
5. Notifies `IGameStateListener.OnGameMetadataChanged`.

**What this does and does not do is the crux of the whole document:**

- ✅ It records the new version/checksum and rebuilds the set of document collections (`GameStateDB.SetFormat`). New collections become available.
- ❌ It does **not** transform a single byte of existing data. Old documents keep their old shape. A removed collection's documents are simply orphaned in the underlying file (no longer surfaced). A renamed collection appears as a new, empty one and orphans the old. Persisted live entities are untouched.

So "adopt" means "accept the new schema label and make room for new collections" — **not** "migrate the data." For additive changes (new collection, new field, new entity type) that is often fine, because old data is read through code that simply finds the new field absent. For destructive or transforming changes (rename, retype, split/merge a field), use the **migration flow** ([§7](#7-the-migration-flow)), which adopts the new format only after the client has rewritten the data.

> **Brand-new-world gotcha.** Because a fresh world has `Metadata.Version == 0`, the *very first* connection always triggers `UpdateFormat`. For a **remote** first client that means initializing a brand-new world **requires `RemoteUpgradeAllowed = true`** (otherwise the first client is rejected). This is exactly why the standalone server sets it — see [§6](#6-standalone-server-mode). A local connection can always initialize a world.

---

## 5. What survives a schema change

This is the practical "what can I safely change" table for the *additive / re-label* changes that work today. It combines the immutable-id rules from [`DistributedEntities.md`](DistributedEntities.md) §2 with what `UpdateFormat` actually preserves.

| Stored thing | Identity that must not change | Free to change | Hazard |
|---|---|---|---|
| **Documents** in a collection | the collection's **name** (in the DB) + each doc's `_id` | add collections; add fields to a document type; rename the C# types you map to BSON | renaming or removing a collection orphans its data |
| **Distributed field values** of a persisted entity | the field's **`PersistAs`** string | rename the C# field; reorder fields in source; renumber the field id (a wire/schema change — version bump — but not a data change) | renaming a `PersistAs` key orphans the stored value |
| **A persisted entity's type** | its **`PersistAs`** key (written to disk as `t`) | rename or move the class; renumber the type id (wire/schema change only) | ⚠️ renaming the key breaks reload — the stored `t` is resolved against the *current* registry (`GameStateLive.GetEntityTypeByPersistKey`), so an unknown key throws when the channel loads |

As [`DistributedEntities.md`](DistributedEntities.md) §2 spells out, **numeric ids are wire-only and `PersistAs` keys are the durable identity**. Nothing numeric is written into saved data: a persisted entity's row records its type's `PersistAs` key (`t`), and its field values are stored under their field-level keys. Renumbering a type or field id is therefore a coordinated schema change (checksum change → version bump, all builds move together) but never touches existing saves; renaming a `PersistAs` key is the destructive act, recoverable only by a migration step that rewrites the stored rows. Class names, field names, and namespaces are *not* part of either identity, so rename and relocate them freely.

---

## 6. Standalone server mode

In a self-hosted/local setup the host *is* the authority and usually the first (local) connection, so it naturally sets the schema before anyone else joins. The **standalone server** is the multi-client case where migration coordination matters most.

How it works (`ImpunityStandaloneServer`):

- At startup `WorldService` opens **every** configured world with `GameStateServer.OpenOrCreate` and keeps them running for the process lifetime. Each world recovers from an interrupted migration on open (see [§7](#7-the-migration-flow)).
- `ConnectionService` hosts them all through one `ImpunityServer`, with `RemoteUpgradeAllowed = true` (set in `Program.cs`) so any remote client may migrate. **Embedded** servers leave it `false`, so only the local (in-process) connection can.

Put that together with the guard ([§3](#3-the-connect-time-guard)):

- A world is empty until the first client connects. A matching-version client just plays. A **higher-version** client into an empty world is **offered a migration** ([§7](#7-the-migration-flow)) and, while it holds that offer (or runs the migration), the world is reserved — every other connection is refused with `ServerMigrationInProgress`.
- A higher-version client that arrives while **other clients are connected** is rejected with `ServerVersionIncompatible`; the running clients are never disturbed. The migrator must wait for the world to empty.
- The coordination primitives that were once dormant are now live: the per-world migration state machine (`Open → Offered → Migrating → Open`) replaces the never-set `NewConnectionsDisabled` flag, and the ephemeral, connection-bound migration lock releases automatically on disconnect.

---

## 7. The migration flow

Guiding principle: **Impunity is data-agnostic; the client owns migration.** The server cannot know whether or how stored data needs to change — only the game's own code does. So the server's job is purely to *coordinate* (decide who may migrate, keep everyone else out, snapshot/restore as a safety net), while the **client performs every transformation through raw database operations.** Migration is **explicit and user-driven**: connecting never starts one automatically.

### Two phases: offer, then migrate

A migration moves the world through three states (`MigrationPhase`): `Open → Offered(owner) → Migrating(owner) → Open`.

1. **Offer (automatic, non-destructive).** A client built at version **Y** connects to a world at version **X < Y**. The guard ([§3](#3-the-connect-time-guard)) sees it is eligible (a local connection, or remote with `RemoteUpgradeAllowed`) and that no other client is connected, and puts the world in **Offered**: it reserves the world for this connection and replies with a successful connect carrying `MigrationRequired`/`from`/`to`. **Nothing is changed** — no snapshot, no marker, the DB is untouched. The client surfaces this as `BaseGameConnection.PendingMigration`; the game can now show a "migrate this save?" dialog. While offered, the only actions this connection may take are *begin* or *decline*; every other connection is refused with `ServerMigrationInProgress`.

2. **Migrate (explicit).** On the user's go-ahead the client sends **begin** (`RunMigrationAsync` → `BeginMigrationAction`). The world transitions to **Migrating**: the server **snapshots the database** (close → copy → reopen) and writes a persistent **marker** (`from`, `to`, owner, timestamp), then replies success. Now the client's migration delegate runs, issuing raw operations through a `MigrationContext` (see below). On success the client sends **commit** (`CommitMigrationAction`): the server stamps the new version/checksum (`UpdateFormat`, [§4](#4-adopting-a-new-schema)), discards the snapshot and marker, releases the world, and the *same connection becomes a normal client at version Y*.

Declining (or just disconnecting) while only offered releases the reservation with zero on-disk footprint.

### The migration delegate and `MigrationContext`

You register the migration logic as a **single delegate** `Func<MigrationContext, Task>` and drive it via the connection extensions:

```csharp
// Show the user a dialog, then migrate if they agree (connects first):
await connection.EnsureFormatAsync(
    shouldMigrate: req => ui.AskAsync($"Upgrade save from v{req.FromVersion} to v{req.ToVersion}?"),
    migrate: async ctx =>
    {
        // ctx.FromVersion / ctx.ToVersion let you branch across version gaps yourself.
        foreach (var doc in await ctx.ListAsync("Items"))
        {
            doc["power"] = doc["power"].AsInt32 * 2;   // reshape old data
            await ctx.UpsertAsync("Items", doc);
        }
    });
```

Or do it in explicit steps: `await connection.ConnectAsync();` then inspect `connection.PendingMigration`; call `connection.RunMigrationAsync(migrate)` to perform it or `connection.DeclineMigrationAsync()` to decline. Keep calling `connection.Update()` while it runs (migration replies are delivered there, like every other call).

`MigrationContext` exposes raw, **name-addressed** access (UltraLiteDB keys collections by *name*, so a renamed collection is simply a different name — your delegate reads the old name and writes the new one):

| Member | Purpose |
|---|---|
| `FromVersion` / `ToVersion` | The version range being migrated. |
| `GetCollectionNamesAsync()` | Every collection present (including old/renamed ones and the live-entities collection). |
| `ListAsync(name)` / `ScanPageAsync(name, skip, limit)` | Read raw documents (paged internally to stay under the wire size). |
| `InsertAsync` / `UpsertAsync` / `UpdateAsync` / `DeleteAsync(name, …)` | Raw writes by `_id`. |
| `ScanEntitiesAsync()` | Read persisted live entities as `MigrationEntityRow` (id, channel, type `PersistAs` key, flags, property→BSON). |
| `WriteEntityAsync(row)` / `DeleteEntityAsync(row)` | Write/remove a persisted live entity (metadata row + property rows). |

Persisted live entities live in the reserved `"Entities"` collection with the row layout `_id`/`ch`/`t`/`f` (metadata) and `entityId/propertyName` → `v` (one row per persisted property). The `*Entity*` helpers group and rebuild that layout for you; the raw `*Async` calls can also reach it directly via `MigrationContext.EntitiesCollectionName`.

### Abort & recovery

Because the client does the work, the dangerous case is it going away mid-migration. The snapshot is the safety net: on any abort the server **restores the pre-migration snapshot**, returning the world to a clean version X so the next eligible client can retry from scratch (restoring the whole file means delegate code need not be resumable or idempotent). Triggers:

- **The migrator disconnects.** The migration lock is *ephemeral* — bound to the connection and released on disconnect, the same lifecycle as [named locks](Connections.md#8-named-locks). If it had begun (snapshot taken), the server restores; if only offered, it just releases.
- **The migrator stalls.** An idle/progress timer (`ImpunityOptions.MigrationIdleTimeoutMillis`, reset on every migration operation) aborts a connected-but-wedged migrator so the world isn't locked forever.
- **The server restarts mid-migration.** On open it finds the marker: if `marker.ToVersion == Metadata.Version` the commit had already landed (just clean up), otherwise it restores the snapshot before accepting any connection. An interrupted *offer* leaves no marker, so there is nothing to recover.

### Where it lives

- Client: `BaseGameConnection.PendingMigration`, the `RunMigration`/`DeclineMigration` calls and their `…Async` / `EnsureFormatAsync` extensions, and `MigrationContext` / `MigrationEntityRow`.
- Server: the `MigrationPhase` state machine and offer/begin/commit/abort handlers on `GameStateServer`; backup/restore, the marker, and the name-addressed raw API on `GameStateDB`.

---

## 8. Notes & remaining sharp edges

1. **Collection rename = copy old name → new name.** Because UltraLiteDB stores collections by name, "renaming" a collection in version Y points its index at a new, empty collection; the old data sits under the old name until a migration step copies it across. (A collection's numeric *index* is still recommended to be immutable like type/field ids, but migration no longer depends on it because it addresses collections by name.)

2. **`PersistAs` keys remain immutable forever** ([§5](#5-what-survives-a-schema-change)). Migration does **not** relax this by default: a persisted entity's stored `t` (its type's `PersistAs` key) and its property keys are resolved against the current registry on reload, so a key rename only works if the migration step rewrites the stored rows to the new key. Changing a persisted field's serializer/value type likewise changes the stored shape and is the migration step's responsibility (read the old BSON, write the new). Numeric type/field ids are wire-only and never stored, so renumbering them needs a version bump but no data rewrite.

3. **Same version, different checksum** is still treated as adopt-when-alone (no migration), per the versioning rule — migration is offered only when the client's *version* is strictly higher. Always bump the version when the schema changes.

4. **Large collections** are paged by `ScanPageAsync` (and `ListAsync` loops it) to stay under the ~64 KB wire message size.

5. **Stepwise vs. single delegate.** The engine runs your one delegate once with the full `From`/`To` range; if you ship many versions you branch on `ctx.FromVersion` yourself (e.g. a `switch` that falls through X→X+1→…→Y). There is no built-in per-version step registry.

6. **Migration rewrites the database, not already-loaded live entities.** The persisted-entity helpers (and raw access to the `"Entities"` collection) operate on stored BSON. A persisted channel that is *already loaded into server memory* (channels stay loaded for the server process's lifetime, even after their last subscriber leaves) is **not** updated by those writes — a subsequent subscribe returns the cached in-memory copy, not the migrated DB value. This is a non-issue in the real flows, where migration runs against a freshly-opened world: a standalone server that restarted on the new build, or an embedded client that migrates at connect time before subscribing to anything (and the gate forbids the migrator from subscribing mid-migration anyway). The practical rule: **migrate before any channel is loaded.** A process that loaded a channel and then migrates in-place (without a restart) would need the world reopened for the change to surface.

---

## 9. Quick reference

| Symbol | Role |
|---|---|
| `GameStateFormat` (`version`, `collections`, `entityTypes`) | What the client declares |
| `GameStateFormatData` (`Version`, `DataChecksum`, …) | Serializable format sent in the handshake |
| `ImpunityUtil.MakeDataChecksum` | MD5 over the serialized format |
| `GameMetadata` (`Version`, `DataFormatChecksum`) | The world's currently-adopted schema, persisted |
| `GameStateServer.EstablishConnection` | The connect-time guard + migration offer ([§3](#3-the-connect-time-guard)) |
| `GameStateServer.UpdateFormat` | Stamps a new schema (metadata + collections) ([§4](#4-adopting-a-new-schema)) |
| `MigrationPhase` (`None`/`Offered`/`Migrating`) | Per-world migration state machine |
| `GameStateDB.BackupForMigration` / `RestoreFromMigrationBackup` / `ReadMigrationMarker` | Snapshot, restore, crash-recovery marker |
| `GameStateDB.GetAllCollectionNames` / `ScanCollectionByName` / `UpsertByName` … | Name-addressed raw API used by migration |
| `BaseGameConnection.PendingMigration` | The offered migration (from/to), set after connecting |
| `EnsureFormatAsync` / `RunMigrationAsync` / `DeclineMigrationAsync` | Client entry points (extensions) |
| `MigrationContext` / `MigrationEntityRow` | The toolkit handed to the migration delegate |
| `ImpunityOptions.RemoteUpgradeAllowed` | Lets remote clients be offered migration / drive adoption |
| `ImpunityOptions.MigrationIdleTimeoutMillis` | Idle timeout for an offered or running migration |
| `ImpunityErrorCode.ServerVersionIncompatible` / `ServerMigrationInProgress` | Rejection codes (ineligible / world reserved) |
