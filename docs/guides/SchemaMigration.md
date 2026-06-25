# Schema Migration & Versioning

How Impunity guards a saved game against incompatible client versions, how it currently adopts a new schema, and what a full **data migration** story still needs.

> **⚠️ Status: partially implemented — this is a design-and-state-of-play document, not a finished feature.**
> What works today is the *versioning guard*: client and server agree on a schema **version + checksum**, and the server will **adopt** a new schema when it is safe to do so. What does **not** exist yet is **data transformation** — nothing rewrites existing stored documents or persisted entities into a new shape, and there is no coordinated "lock the world and migrate" flow for multi-client / standalone hosting. Sections below are tagged **(implemented)** or **(not yet implemented)** so the two are never confused.

This guide builds on [`Connections.md`](Connections.md) (the handshake and the action system) and [`DistributedEntities.md`](DistributedEntities.md) (especially the *durable id* discussion in its §2). Read those first.

---

## Contents

1. [The problem](#1-the-problem)
2. [The format: version + checksum](#2-the-format-version--checksum) — *(implemented)*
3. [The connect-time guard](#3-the-connect-time-guard) — *(implemented)*
4. [Adopting a new schema](#4-adopting-a-new-schema) — *(implemented)*
5. [What survives a schema change](#5-what-survives-a-schema-change)
6. [Standalone server mode: the hard case](#6-standalone-server-mode-the-hard-case)
7. [The intended design](#7-the-intended-design-not-yet-built) — *(not yet built)*
8. [Open questions](#8-open-questions)
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
if (NewConnectionsDisabled)          → fatal: ServerUnavailable
if (!ValidateFormat(clientFormat))   → the client's version+checksum don't match the world's:
       if   world is brand new (Metadata.Version == 0)   → adopt the client's format
       elif this is the only connection (NumConnections == 0) → adopt the client's format
       else                                              → fatal: ServerVersionIncompatible
ConnectionOpened(...)
```

`ValidateFormat` is a strict equality check on both version and checksum. So a connecting client is in one of three situations:

| Situation | Result |
|---|---|
| Format matches the world | Connects normally. |
| Format differs, and the world is empty (or brand new) | The world **adopts** the client's format ([§4](#4-adopting-a-new-schema)), then the client connects. |
| Format differs, and other clients are already connected | Rejected with a fatal `ServerVersionIncompatible`. |

This is the entire safety model today: **the first client into an empty world sets the schema; everyone after must match, or be turned away.** There is no negotiation and no partial compatibility.

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

So "adopt" today means "accept the new schema label and make room for new collections" — **not** "migrate the data." For additive changes (new collection, new field, new entity type) that is often fine, because old data is read through code that simply finds the new field absent. For destructive or transforming changes (rename, retype, split/merge a field) there is no support yet — see [§7](#7-the-intended-design-not-yet-built).

> **Brand-new-world gotcha.** Because a fresh world has `Metadata.Version == 0`, the *very first* connection always triggers `UpdateFormat`. For a **remote** first client that means initializing a brand-new world **requires `RemoteUpgradeAllowed = true`** (otherwise the first client is rejected). This is exactly why the standalone server sets it — see [§6](#6-standalone-server-mode-the-hard-case). A local connection can always initialize a world.

---

## 5. What survives a schema change

This is the practical "what can I safely change" table for the *additive / re-label* changes that work today. It combines the immutable-id rules from [`DistributedEntities.md`](DistributedEntities.md) §2 with what `UpdateFormat` actually preserves.

| Stored thing | Identity that must not change | Free to change | Hazard |
|---|---|---|---|
| **Documents** in a collection | the collection's **name** (in the DB) + each doc's `_id` | add collections; add fields to a document type; rename the C# types you map to BSON | renaming or removing a collection orphans its data |
| **Distributed field values** of a persisted entity | the field's **numeric id** *and* its **`PersistAs`** string | rename the C# field; reorder fields in source | renumbering the field id breaks the wire/identity contract; renaming a `PersistAs` key orphans the stored value |
| **A persisted entity's type** | its **numeric type id** (also written to disk as `t`) *and* its **`PersistAs`** key | rename or move the class | ⚠️ renumbering the type id breaks reload — the stored `t` is resolved against the *current* registry (`GameStateLive.GetEntityType`), so it maps to the wrong type or falls out of range |

As [`DistributedEntities.md`](DistributedEntities.md) §2 spells out, **numeric type ids and field ids are immutable forever** — never renumber or reuse them. Persistence makes this concrete and unforgiving: a persisted entity's numeric type id is written into the database (`t`) and resolved against the current registry on load, so changing it doesn't merely break the wire — it breaks reload of existing saves. Class names, field names, and namespaces are *not* part of the identity, so rename and relocate them freely; only the numbers and the `PersistAs` keys must stay put.

---

## 6. Standalone server mode: the hard case

In a self-hosted/local setup the host *is* the authority and usually the first (local) connection, so it naturally sets the schema before anyone else joins. The **standalone server** breaks that assumption, and is where the unsolved problems live.

How it works today (`ImpunityStandaloneServer`):

- At startup `WorldService` opens **every** configured world with `GameStateServer.OpenOrCreate` and keeps them running for the process lifetime.
- `ConnectionService` hosts them all through one `ImpunityServer`, with `RemoteUpgradeAllowed = true` (set in `Program.cs`).

Put that together with the guard ([§3](#3-the-connect-time-guard)) and the picture is:

- A world is empty until the first client connects. **The first client to reach an empty world wins** — its format is adopted. Every later client must match it.
- If a new-build client and an old-build client race for an empty (or freshly restarted) world, whichever lands first sets the schema and the other is rejected with `ServerVersionIncompatible`.
- There is **no coordination primitive** to say "I am about to migrate this world; hold all other connections until I'm done." The pieces that *would* support it exist but are inert:
  - `GameStateServer.NewConnectionsDisabled` is checked in `EstablishConnection` but is **never set anywhere** — there is no API to raise it.
  - There is no "migrating" state on a world, no migration lock, and no way for a client to *request* exclusive migration access.

So the requirement you described — *"one client must lock the game and do the migration before any other clients connect"* — is **not yet expressible.** The closest thing today is the accidental "first into an empty world wins," which is a race, not a lock. The intended flow that fixes this is [§7](#7-the-intended-design-not-yet-built).

---

## 7. The intended design (not yet built)

This is the target flow. **None of it is implemented yet** — it is recorded here so the build has a spec and so the open problems ([§8](#8-open-questions)) have context.

Guiding principle: **Impunity is data-agnostic; the client owns migration.** The server cannot know whether or how stored data needs to change — only the game's own code does. So the server's job is purely to *coordinate* (decide who migrates, keep everyone else out, and provide a safety net), while the **client performs every transformation through ordinary database operations.**

### The happy path

1. A client built at version **Y** connects to a world whose stored version is **X < Y** (detected by the version/checksum guard, [§3](#3-the-connect-time-guard)).
2. Instead of adopting the format immediately, the server grants that client an exclusive **migration lock** on the world and refuses all other connections (`ServerUnavailable`, "try again later") for the duration.
3. Before any change, the server **snapshots the database** (the pre-migration backup) and records a persistent "migration in progress" marker (`from X`, `to Y`, owner, timestamp).
4. The client's **migrations manager** runs **stepwise** transforms — X→X+1→X+2→…→Y — each step a batch of database operations that reads old-shaped data and writes new-shaped data. Stepwise means a save that skipped releases is carried forward one version at a time.
5. When all steps succeed the client **commits**: the server stamps the new version/checksum (`UpdateFormat`, [§4](#4-adopting-a-new-schema)), discards the backup and marker, releases the lock, and reopens the world to everyone. The migrating client is now a normal connected client at version Y.

### Abort & recovery (the hard part — see [§8](#8-open-questions))

Because the migrating client does the work, the dangerous case is **it goes away mid-migration**, leaving the database half-converted. The safety net is the backup: on any abort the server **restores the pre-migration snapshot**, returning the world to a clean version X so the next eligible client can retry from scratch. (Restoring the whole DB means individual steps need not be resumable or idempotent — a retry simply starts over.) Three abort triggers:

- **The migrator disconnects.** The migration lock should be *ephemeral* (tied to the connection, released on disconnect — the mechanism that already cleans up [named locks](Connections.md#8-named-locks) and delete-on-disconnect entities). On release-without-commit the server restores the backup.
- **The migrator stalls.** A connected-but-wedged client needs a **timeout** so the world isn't locked forever; on expiry the server aborts and restores. (Timeout policy is open — see [§8](#8-open-questions).)
- **The server itself restarts mid-migration.** On startup it finds the marker plus a still-uncommitted version X and restores the backup before accepting connections.

### What has to be built

- **`EnsureFormat` / `EnsureFormatAsync`** on `BaseGameConnection` — the client entry point ("bring this world up to my format, migrating if needed"). It is already referenced (commented out in `GameConnectionAsyncExt.cs` and the legacy test) but does not exist; it is the natural driver of the whole dance, calling back into the client's migrations manager.
- **A per-world migration state** (`Open → Migrating(owner, X→Y) → Open` on commit, or back to `Open` on abort) with an **ephemeral, exclusive lock**. `GameStateServer.NewConnectionsDisabled` is the existing-but-inert blunt instrument; this likely wants to be a richer per-world state than one global boolean.
- **Backup / restore** of the UltraLiteDB file, plus the persistent in-progress marker for crash recovery, plus the **timeout**.
- **The client-side migrations manager** — the registry of ordered version-to-version steps and the engine that runs them via DB operations.
- **Access, from migration code, to pre-migration data** — including collections and persisted entities the *current* format may no longer declare ([§8](#8-open-questions)).

---

## 8. Open questions

The design in [§7](#7-the-intended-design-not-yet-built) settles the big shape (client-driven, stepwise, connect-and-lock, snapshot-and-restore). These are the decisions still genuinely open — the working agenda.

1. **Timeout policy for a stalled migrator.** A flat wall-clock deadline is simple but risks killing a legitimately slow migration of a large save. A progress-based timer (reset on each completed step, or on any migration DB activity) tolerates long migrations while still catching a true hang. Disconnect detection already covers crashes; the timeout only needs to catch "connected but wedged." *Leaning: idle/progress-based with a generous bound.*

2. **How does migration code read pre-migration data?** The client is built at version Y, but it must read X-shaped data — including **collections or persisted entities the Y format no longer declares**. This implies collection indices must be **immutable** like type/field ids, and the migration engine needs raw access to *any* collection id (not just those in the current format). Do we expose an "all collections" view during migration, or have each step declare the (old) collection layout it operates on?

3. **Migrating persisted live entities, not just documents.** Document collections are easy to rewrite through the public DB API. Persisted *distributed entities* live in the reserved internal "Entities" collection (index 1) with an internal row layout (`_id`, `ch`, `t`, and `entityId/PersistAs` property rows). Migrating those via "ordinary database operations" needs either documented raw access to that collection or a dedicated migration API. Is live-entity migration in scope for v1, or do we start with document collections only?

4. **Backup mechanism specifics.** A file-copy snapshot of the single UltraLiteDB file before migration is the obvious approach; restore = close, replace the file, reopen (safe because the world is exclusively locked). Confirm that's acceptable versus per-step transactions, and decide where the snapshot + in-progress marker live on disk.

5. **Numeric id durability.** Today's rule is that numeric type ids and field ids are immutable forever ([§5](#5-what-survives-a-schema-change)). Keep that as the permanent contract (simplest — document and enforce it, e.g. reject id reuse at registration), or eventually allow an id-remap as part of a migration step? Same question for changing a persisted field's serializer / value type, which also changes the stored and wire shape.

---

## 9. Quick reference

**Types & members that make up today's system**

| Symbol | Role |
|---|---|
| `GameStateFormat` (`version`, `collections`, `entityTypes`) | What the client declares |
| `GameStateFormatData` (`Version`, `DataChecksum`, …) | Serializable format sent in the handshake |
| `ImpunityUtil.MakeDataChecksum` | MD5 over the serialized format |
| `GameMetadata` (`Version`, `DataFormatChecksum`) | The world's currently-adopted schema, persisted |
| `GameStateServer.ValidateFormat` | Strict version+checksum equality check |
| `GameStateServer.EstablishConnection` | The connect-time guard ([§3](#3-the-connect-time-guard)) |
| `GameStateServer.UpdateFormat` | Adopts a new schema (metadata only) ([§4](#4-adopting-a-new-schema)) |
| `ImpunityOptions.RemoteUpgradeAllowed` | Lets remote clients drive adoption/initialization |
| `GameStateServer.NewConnectionsDisabled` | Dormant connection-pause flag (never set today) |
| `ImpunityErrorCode.ServerVersionIncompatible` / `ServerUnavailable` | The two relevant rejection codes |

**Dormant / planned hooks:** `BaseGameConnection.EnsureFormat` (referenced, not implemented), `NewConnectionsDisabled` (checked, never set).

---

*This document describes work in progress. The behavior in §2–§6 reflects the current code (`GameStateServer`, `GameStateLive`, `GameStateDB`, `ImpunityUtil`, and `ImpunityStandaloneServer`); §7–§8 describe intent and are expected to change as the system is built out.*
