# Distributed Entities

A guide to Impunity's real-time state replication system: distributed **entities**, the **channels** that contain them, and the **distributed fields** that sync their state between clients through the server.

This document covers the essentials. It assumes you already have a working `GameStateServer` and a client `BaseGameConnection` (a `RemoteGameConnection` over TCP, or a `LocalGameConnection` in-process) — see the companion guide [`Connections.md`](Connections.md) for how connections, the action system, and the database work. See the project `CLAUDE.md` for the build/run layout and the wire protocol.

> **Conventions used here.** "Object" means a `DistributedObjectBase`/`IDistributedObject` instance; "channel" means a `DistributedChannelBase`/`IDistributedChannel`; "entity" means either. Code samples use the field/serializer types from `Impunity.Connection`.

---

## Contents

1. [Mental model](#1-mental-model)
2. [The annotation system: durable type and field ids](#2-the-annotation-system-durable-type-and-field-ids)
3. [Declaring an entity type](#3-declaring-an-entity-type)
4. [Distributed fields](#4-distributed-fields)
5. [Information flow: client → server → client](#5-information-flow-client--server--client)
6. [Subscriptions and the entity lifecycle](#6-subscriptions-and-the-entity-lifecycle)
7. [Creating channels and objects](#7-creating-channels-and-objects)
8. [Client-authoritative objects](#8-client-authoritative-objects)
9. [Persistent objects](#9-persistent-objects)
10. [Locks](#10-locks)
11. [Events](#11-events)
12. [Niche topics](#12-niche-topics) — temporal fields, local-only setters, unguaranteed sends
13. [Quick reference](#13-quick-reference)
14. [Known caveats](#14-known-caveats)

---

## 1. Mental model

The unit of replication is the **channel**. A client **subscribes** to a channel by name; from that point the server pushes the channel's current state and every subsequent change to that client. A channel contains **objects** (its members). Both channels and objects are *entities*, and both carry typed **distributed fields** whose values are kept in sync.

```
                         ┌──────────────────────── Server ────────────────────────┐
   Client A              │   GameStateLive                                         │            Client B
   ┌─────────────┐       │   ┌─────────────────────────────────────────────────┐  │       ┌─────────────┐
   │ "lobby"     │◀──────┼───│ channel "lobby"                                   │──┼──────▶│ "lobby"     │
   │  ├ player1  │  sub  │   │   ├ object player1   (fields: pos, health, …)     │  │  sub  │  ├ player1  │
   │  └ player2  │       │   │   └ object player2                                │  │       │  └ player2  │
   └─────────────┘       │   └─────────────────────────────────────────────────┘  │       └─────────────┘
                         └─────────────────────────────────────────────────────────┘
```

The server is the single source of truth. A client mutates a field locally; the change is sent to the server; the server applies it (and persists it if the field is persisted), then relays it to every other subscriber. **By default a client's own reads do not reflect its own writes until the server echoes the change back** — see [§5](#5-information-flow-client--server--client). Two opt-outs from that rule exist: [client-authoritative](#8-client-authoritative-objects) entities and [local-only setters](#12-niche-topics).

Each client drives the system by calling `connection.Update()` once per frame. That single call dispatches inbound server messages to your entities (firing their callbacks) and flushes your pending outbound changes. Nothing happens between `Update()` calls.

---

## 2. The annotation system: durable type and field ids

Every distributed type and every distributed field is identified on the wire by a small **numeric id**, not by its name. This keeps updates compact, but it makes those numbers a **permanent contract**: once an id ships, it must never change or be reused — see [below](#why-these-ids-are-immutable).

### `[DistributedEntity(typeId)]`

Marks a class as a distributed entity type and assigns its **type id**.

```csharp
[DistributedEntity(TestEntityTypes.PLAYER)]            // typeId = 2
public partial class Player : DistributedObjectBase { … }
```

- `typeId` must be a **positive integer**, unique across all entity types you register, and **permanent** — once shipped it can never change or be reused (see [below](#why-these-ids-are-immutable)). **Id `0` is reserved** for the built-in untyped channel (`GenericDistributedChannel`).
- Optional `FactoryMethod = "Name"` — the name of a `public static` parameterless method on the type returning an `IDistributedEntity`. The manager calls it to construct received instances instead of `Activator.CreateInstance`. Use it for types without a public default constructor or that need custom setup.
- Optional `PersistAs = "key"` — marks the type **persisted** and gives it a database key. See [§9](#9-persistent-objects).
- The class **must be `partial`** (the source generator adds a second part — see below) and must derive from one of the base classes in [§3](#3-declaring-an-entity-type).

### `[Distributed(fieldId)]`

Marks a field as replicated and assigns its **field id**.

```csharp
[Distributed((byte)DistributedPropIds.POSITION)]       // fieldId = 1
public DistributedValue<Vector3, Vector3Serializer> Position;
```

- `fieldId` is a **byte in the range 1–63**. The limit is hard: each field maps to one bit of a 64-bit dirty mask (`1UL << (fieldId - 1)`), so a type may have at most 63 distributed fields. Like the type id, a field id is **wire identity** — fixed for every build sharing a schema version (see [below](#why-these-ids-are-immutable)).
- Optional `PersistAs = "key"` — persists this field's value under the given key. The containing type must itself be persisted, and the field may not be temporal. See [§9](#9-persistent-objects).
- A common convention (see the test entities) is a nested `enum DistributedPropIds : byte { … }` so the ids read meaningfully at the declaration site.

### Why these ids are immutable

The two id systems split cleanly by medium — **short numbers identify things on the wire, `PersistAs` strings identify things at rest** — and carry different immutability rules. Keeping this straight is the single most important thing to understand before you ship:

| Identity | What it identifies | Rule |
|---|---|---|
| **Numeric ids** (`[DistributedEntity(n)]`, `[Distributed(n)]`) | The **wire identity** of every type and field, exchanged on the connect handshake and carried compactly in every update. Numeric ids are never written into saved data. | **Fixed per schema version.** Every build sharing a schema version must agree on the numbers; changing one is a schema change like any other (bump the version and move all builds together). Saved data is unaffected. |
| **`PersistAs` string keys** | The **durable identity** of everything stored in the database: each persisted entity row records its type's key (the stored `t`), and each persisted field value is stored under the field's key. | **Immutable forever** once data exists. Renaming a key orphans everything stored under the old one (recoverable only via a data migration). Entity-level keys must be **unique across all entity types**. |

Practical consequences:

- **The split is deliberate:** compact numbers keep replication traffic small; longer meaningful strings make the data at rest self-describing (and easy to identify in migration code, which sees raw BSON rows).
- **Renaming and reorganizing code is free** — and is the whole reason identity isn't the class or field name. You can rename a `[DistributedEntity]` class or a `[Distributed]` field, move it to a different namespace, or rename the `enum` that labels the ids, and neither the wire nor the database notices.
- **Treat `PersistAs` keys as your permanent storage schema.** Choose them deliberately; once a save exists they can never change. Keys may not be empty and may not start with `_` (reserved), and a type's key may not be shared with another type (throws at registration).
- **Renumbering a numeric id is a coordinated wire change, not a data change** — it alters the format checksum, so it needs a version bump and all builds updated together, but existing saves reload untouched. Reusing a number *within* a version, or between builds that must interoperate, silently misreads updates — don't.

### The source generator

Entity types are `partial` because the `ImpunityCodeGenerator` Roslyn source generator emits the other half at compile time. For each `[DistributedEntity]` class it generates:

- An `override void InitializeDistributedFields()` that wires every `[Distributed]` field to its owning entity and id: `Position._imp_Initialize(this, 1);`. This runs from the entity's base constructor, so fields are ready to use immediately after `new`.
- Six private `_imp_…Wrapper_<FieldName>` methods per field that the `ClientEntityManager` invokes by reflection to (de)serialize the field (write changes, read initial, read change, skip, get-as-BSON, set-from-BSON).

You never write or call these. You only need to remember: **the class must be `partial`, and a field becomes live the moment its entity is constructed** (so it is safe to subscribe to a field's `OnChanged` in your constructor — the test entities do exactly this).

---

## 3. Declaring an entity type

Derive from the base class that matches the role:

| Base class | Implements | Role |
|---|---|---|
| `DistributedObjectBase` | `IDistributedObject` | An object that lives inside a channel |
| `DistributedChannelBase` | `IDistributedChannel` | A channel that contains objects |
| `DistributedMonoBehvaiourObjectBase` | `IDistributedObject` | A `MonoBehaviour` object (Unity scene object) |
| `DistributedMonoBehvaiourChannelBase` | `IDistributedChannel` | A `MonoBehaviour` channel |

A complete object:

```csharp
[DistributedEntity(TestEntityTypes.PLAYER, FactoryMethod = "Create")]
public partial class Player : DistributedObjectBase
{
    enum FieldIds : byte { Position = 1, Health = 2, Inventory = 3 }

    public static IDistributedEntity Create() => new Player();

    [Distributed((byte)FieldIds.Position)]
    public DistributedValue<Vector3, Vector3Serializer> Position;

    [Distributed((byte)FieldIds.Health)]
    public DistributedValue<int, Int32Serializer> Health;

    [Distributed((byte)FieldIds.Inventory)]
    public DistributedStringDictionary<int, Int32Serializer> Inventory;   // itemName → count

    public Player()
    {
        // Fields are already initialized here; safe to subscribe to change events.
        Health.OnChanged += (oldHp, newHp) => { /* update UI */ };
    }
}
```

A channel is the same, deriving from `DistributedChannelBase`:

```csharp
[DistributedEntity(TestEntityTypes.ZONE)]
public partial class Zone : DistributedChannelBase
{
    enum FieldIds : byte { Status = 1, Chat = 2 }

    [Distributed((byte)FieldIds.Status)]
    public DistributedValue<string, StringSerializer> Status;

    [Distributed((byte)FieldIds.Chat)]
    public DistributedQueue<string, StringSerializer> Chat;   // last N chat lines
}
```

### Registering types

The set of entity types is part of your `GameStateFormat`. The connection registers them with its `ClientEntityManager` for you when you construct it:

```csharp
var format = new GameStateFormat(
    version: 1,
    collections: new[] { new GameStateCollection { Index = 1, Name = "Items" } },
    entityTypes: new[] { typeof(Player), typeof(Zone) });

var connection = RemoteGameConnection.MakeTCPRemoteConnection(endpoint, "MyGame", key, format, options);
connection.Connect(err => { /* connected */ });
```

Under the hood the manager's `RegisterEntityTypes(Type[])` walks each type by reflection, validates ids, and produces the `GameStateEntityTypeDef[]` sent to the server during the handshake. The server adopts that format, so client and server share one definition of every type.

> A type with **no** distributed fields is legal (see `TestEmptyObj`) — useful as a bare presence/identity object or a marker channel.

---

## 4. Distributed fields

A distributed field is a generic struct `Field<T, S>` where `T` is the value type and `S` is a **serializer struct** that knows how to read/write `T`. You always supply both:

```csharp
[Distributed(1)] public DistributedValue<int, Int32Serializer> Score;
[Distributed(2)] public DistributedValue<Vector3, Vector3Serializer> Position;
```

### The field types

| Type | Shape | Key methods | Change events |
|---|---|---|---|
| `DistributedValue<T,S>` | single value | `Get()`, `Set(v)` | `OnChanged(old, new)` |
| `DistributedTemporalValue<T,S>` | single value + timestamp/cooldown | `Get()`, `Set(v)`, `Set(v, cooldown)` | `OnChanged`, `OnInitialized(v, age)` |
| `DistributedArray<T,S>` | fixed-size array | `Init(n)` / `Replace(coll)`, `Get(i)`, `Set(i, v)` | `OnChanged(i, old, new)`, `OnReplaced(old, new)` |
| `DistributedQueue<T,S>` | bounded FIFO (evicts oldest) | `Init(cap)` / `Replace(cap, vals)`, `Add(v)` | `OnChanged(v)`, `OnReplaced(old, new)` |
| `DistributedIntDictionary<T,S>` | `int`-keyed map | `Init()` / `Replace(map)`, `Get(k)`, `Add(k, v)` | `OnChanged(k, old, new)`, `OnReplaced(old, new)` |
| `DistributedStringDictionary<T,S>` | `string`-keyed map | `Init()` / `Replace(map)`, `Get(k)`, `Add(k, v)` | `OnChanged(k, old, new)`, `OnReplaced(old, new)` |

All field types implement `IDistributedField`; collections also implement the matching read-only collection interface (`IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyDictionary<K,T>`), so you can enumerate them directly.

### The read/write model

Reading and writing a value field are deliberately asymmetric:

```csharp
player.Health.Set(80);          // queues an update to the server, marks the field dirty
int hp = player.Health.Get();   // returns the last server-CONFIRMED value (still the old one!)
```

`Get()` always returns the **last value confirmed by the server**. A pending `Set()` is *not* visible through `Get()` until the server echoes the change back and the field applies it (firing `OnChanged`). This gives every subscriber — including the writer — a single consistent view that matches the server.

The exceptions, where a `Set()` is applied to the local value immediately:

- the entity is [client-authoritative](#8-client-authoritative-objects), or
- the entity has no connected manager (an offline/editor instance).

`Set(value)` returns `false` (and does nothing) if `value` equals the current value, unless you pass `force: true`.

### Collections must be initialized before use

Collection fields start empty and **must be initialized** with `Init(…)` or `Replace(…)` before you `Get`/`Set`/`Add` (those throw otherwise). Initialization queues a full-state send; subsequent `Set`/`Add` calls send compact per-element/per-key **deltas**.

> **Timing gotcha.** After you subscribe to or create a channel, tick a few frames before mutating a collection you populated, so the initial full-state send is flushed first and later mutations travel as deltas. Modifying in the same frame as initialization can fold the change into the initial send. (This is also noted in `CLAUDE.md`.)

### Serializers

The `S` parameter is a zero-size struct implementing `IDistributableValueSerializer<T>`. Built-in serializers cover the primitives and common Unity types:

- **Primitives:** `BoolSerializer`, `Int8/16/32/64Serializer`, `UInt8/16/32/64Serializer`, `FloatSerializer`, `DoubleSerializer`, `DecimalSerializer`, `CharSerializer`, `StringSerializer`, `BlobSerializer` (`ArraySegment<byte>`), `DateTimeSerializer`, `DateTimeOffsetSerializer`, `TimeSpanSerializer`, `GuidSerializer`.
- **Unity types:** `Vector2/3Serializer`, `DVector4Serializer`, `Vector2Int/Vector3IntSerializer`, `ColorSerializer`, `Color32Serializer`, `QuaternionSerializer`, `Matrix4x4Serializer`.
- **Arbitrary types:** `BsonSerializer<T>` and `BsonSmallSerializer<T>` serialize any type via UltraLiteDB's BSON mapper (use `BsonSmall` for compact small payloads).

To support a custom type, implement the interface — write the binary form, the BSON form, and report a `ValueType` tag:

```csharp
public struct MovementSerializer : IDistributableValueSerializer<MovementState>
{
    public void WriteTo(MovementState v, BinaryWriter w) { w.Write(v.X); w.Write(v.Y); }
    public MovementState ReadFrom(BinaryReader r) => new MovementState(r.ReadSingle(), r.ReadSingle());
    public BsonValue ToBsonValue(MovementState v) => /* … */;
    public MovementState FromBsonValue(BsonValue b) => /* … */;
    public GameStateEntityPropertyValueType ValueType => GameStateEntityPropertyValueType.CustomSmallNullable;
}
```

`T` must be `IEquatable<T>` (the field uses equality to suppress no-op `Set`s). The `ValueType` tag (`Custom`/`CustomSmall` and nullable variants for complex values, or a primitive tag) is reported to tools via `ClientEntityManager.GetFieldSchema`.

---

## 5. Information flow: client → server → client

A field change makes a full round trip. Here is the life of one `Set`:

```
   Client A (writer)                    Server                         Client B (subscriber)
   ─────────────────                    ──────                         ─────────────────────
   field.Set(v)
     → PendingValue = v
     → entity.SetDirty(bit, guaranteed) ── marks entity dirty in the manager

   connection.Update()  (next frame)
     → SendUpdates() serializes every
       dirty entity's changed fields,
       bumps the entity's SendSeq,
       sends one UpdateEntity action ───▶ UpdateProps:
                                            · reject if locked by someone else
                                            · drop stale (per-field seq check)
                                            · apply to server value
                                            · persist if field is persisted
                                            · bump OutSeq, relay  ──────────▶ HandleEntityUpdate
                                              (to all subscribers;                · drop stale (per-field seq)
                                               EXCEPT the writer if the            · apply → field.ReadChangesFrom
                                               entity is client-authoritative)     · fires OnChanged
                                                  │
                          writer's own echo ◀─────┘  (only when NOT client-authoritative)
                            · HandleEntityUpdate → field applies → Get() now returns v
```

Key points:

- **Batched per frame.** `Set` only marks the field dirty; the actual send happens in `SendUpdates()`, called from `connection.Update()`. Multiple `Set`s to the same field between frames collapse to the latest value; multiple fields on one entity travel in one message.
- **Sequence numbers guard against staleness.** Each entity has an outgoing `SendSeq`; the server tracks a per-field received-sequence and ignores out-of-order updates; the server stamps relays with an `OutSeq`; each client tracks a per-field `FieldRecvSeq` and ignores stale inbound updates. This matters because updates can be sent unguaranteed (best-effort) and arrive out of order — see [§12](#12-niche-topics).
- **Guaranteed vs. unguaranteed.** `Set` flags the update **guaranteed** (reliable delivery). `SetUnguaranteed` flags it best-effort. If any dirty field on an entity in a frame is guaranteed, that frame's update for the entity is sent reliably.
- **The writer's echo is what updates its own `Get()`** for non-authoritative entities. For client-authoritative entities the server deliberately does *not* echo to the writer (it already applied locally), avoiding a redundant round trip.

Everything inbound — creates, updates, events, locks, deletes — is dispatched on the thread that calls `connection.Update()` (the main/Unity thread), so your callbacks run where you can safely touch game state and UI.

---

## 6. Subscriptions and the entity lifecycle

### Subscribing

```csharp
connection.EntityManager.SubscribeToChannel<Zone>("lobby", createIfNeeded: null, (err, zone) =>
{
    if (err != null) return;
    // `zone` is live: its current members already arrived, and updates will keep flowing.
});
```

On subscribe the server sends a **snapshot**: the channel-create, then an object-create for every member already in the channel, each carrying that entity's full current field state. After that you receive live deltas. If you are already subscribed, the callback returns the existing channel immediately. Pass a non-null `createIfNeeded` instance to create the channel if it doesn't exist (its fields supply the initial values).

> Coroutine variants exist for all of these (`SubscribeToChannelYield`, `CreateObjectYield`, `UnsubscribeFromChannelYield`, …) in `Impunity.Unity`, so you can `yield return` them instead of nesting callbacks.

### Lifecycle callbacks

Every entity (override the method, or subscribe to the paired `…Event`) receives:

| Callback | Fires when |
|---|---|
| `OnFullyInitialized` | The entity has been created locally and its initial field values applied — it is ready to use. For a channel with existing members, this fires on the channel *before* its members are created. |
| `OnObjectAdded(obj, newlyCreated)` *(channels)* | An object joins the channel. `newlyCreated` is `false` for members in the initial snapshot, `true` for objects created later while you watch — **including objects you create yourself** via `CreateObject` (see [§7](#7-creating-channels-and-objects)). |
| `OnObjectRemoved(obj)` *(channels)* | An object leaves the channel. *(See [§14](#14-known-caveats).)* |
| `OnEventTriggered(type, data)` | A one-shot [event](#11-events) is fired on the entity. |
| `OnLocked` / `OnUnlocked` | The entity's [lock](#10-locks) is taken / released (by anyone). |
| `OnDeleted(deleteData)` | The entity is deleted on the server. Always followed by `OnUndistributed`. |
| `OnUndistributed` | The entity stops being replicated to you — you unsubscribed, the channel was deleted, or the entity was deleted. Release any references here. |

### Unsubscribing

```csharp
zone.Unsubscribe(onComplete, immediate: false);
```

- **Deferred (`immediate: false`, the default).** The channel and its objects stay live and keep receiving updates until the server acknowledges the unsubscribe; then each gets `OnUndistributed` and all references are released. This is the safe default: no updates are lost mid-teardown.
- **Immediate (`immediate: true`).** The channel and its objects are unregistered synchronously and all further incoming updates for them are suppressed (no lifecycle callbacks fire). Use it when you must stop processing a channel *right now* (e.g. tearing down a scene); you are responsible for cleaning up your own references. The manager correctly drops in-flight creates/updates that were already queued for that channel.

---

## 7. Creating channels and objects

```csharp
// Create a channel (does NOT subscribe you to it — subscribe separately if you want updates).
connection.EntityManager.CreateChannel("lobby", new Zone(), replace: false,
    channelObjects: null, (err, ok) => { });

// Create an object inside a channel you have a reference to.
var player = new Player();
player.Position.Set(spawnPoint);                 // seed initial field values before creating
connection.EntityManager.CreateObject(player, zone, replace: false, (err, created) =>
{
    if (err == null) { /* `created` is now registered and live */ }
});
```

- **Initial state.** Whatever you `Set` on the instance before `CreateObject`/`CreateChannel` is serialized as its initial field state and sent with the create.
- **`UniqueName`.** An object may have a `UniqueName` that is unique within its channel (it may not contain `/`). `replace: true` replaces an existing same-named object. For persisted objects the unique name doubles as the database key (a GUID is generated if you leave it null).
- **Channels can be created pre-populated** by passing `channelObjects` to `CreateChannel`.
- **The creating client gets the same creation callbacks as everyone else.** When `CreateObject` succeeds, the creator raises `OnDistributedObjectCreated`, the channel's `OnObjectAdded(obj, newlyCreated: true)`, and the object's `OnFullyInitialized` — the same notifications a subscriber receives for a replicated object — *before* your `onComplete` fires. (The server does not echo the create back to its originator; the client raises these locally to close that gap, so the object also lands in the channel's object collection.) Your seeded field values are kept as-is rather than re-applied from the wire, so they are already readable in these callbacks. This means an object you create is delivered through both `onComplete` **and** `OnObjectAdded`/`OnDistributedObjectCreated`; if a handler must not double-process your own creations, use `onComplete` for the creator-specific path and treat the shared callbacks as idempotent.

---

## 8. Client-authoritative objects

Set `IsClientAuthoritative = true` on an instance **before** creating it to request client authority:

```csharp
var bullet = new Projectile { IsClientAuthoritative = true };
connection.EntityManager.CreateObject(bullet, zone, replace: false, onComplete);
```

What client authority changes:

- **The server locks the entity to the creating connection** on creation. Other clients cannot update or delete it.
- **The server does not echo the owner's updates back to it.** Instead, the owner's `Set` is applied to the **local value immediately** (so `Get()` reflects your writes right away, and `OnChanged` fires locally). Other subscribers still receive the relayed changes normally.
- This is the model for things one client owns outright: its own avatar, its projectiles, its cursor.

Constraints:

- **Client-authoritative and persisted are mutually exclusive** — creating an entity that is both throws.
- `IsClientAuthoritative` is meaningful as a *request set before creation*. (See [§14](#14-known-caveats) for a note on how it is represented on entities you receive from the server.)

The same immediate-local-apply behavior also applies to any entity whose manager has **no connection** — i.e. an offline or editor-built instance. This lets you build and manipulate entities outside a live session.

### Delete-on-disconnect objects

Set `DeleteOnDisconnect = true` on an instance **before** creating it to scope the entity to the lifetime of the creating connection. When that client disconnects, the server automatically deletes the entity and pushes the normal delete to every subscriber (`OnDeleted` then `OnUndistributed`), and frees its name for reuse.

```csharp
var marker = new PlayerMarker { DeleteOnDisconnect = true };
connection.EntityManager.CreateObject(marker, zone, replace: false, onComplete);
```

- Works for both objects and channels — set it on the instance passed to `CreateObject` or `CreateChannel`. Deleting a channel deletes its member objects too. (Note: the `createIfNeeded` overload of `SubscribeToChannel` does not currently carry this flag — use `CreateChannel` to make an ephemeral channel.)
- **Complements `IsClientAuthoritative`**: an entity that is both client-authoritative and delete-on-disconnect is owned and updated by one client and disappears when that client leaves — the model for transient per-client state like an avatar, cursor, or presence marker.
- **Mutually exclusive with persistence** — it makes no sense to store an entity that is deleted on disconnect, so creating one that is both `DeleteOnDisconnect` and persisted throws. This is enforced on the client and re-checked on the server.
- Reuses the server's existing *ephemeral ownership* mechanism (the same one that cleans up [named locks](#10-locks) on disconnect).

---

## 9. Persistent objects

Persistence stores an entity's marked fields in the server's database so they survive restarts and reloads.

To make a type persistent:

1. Give the **type** a `PersistAs` key: `[DistributedEntity(id, PersistAs = "player")]`.
2. Give each field you want stored a `PersistAs` key: `[Distributed(id, PersistAs = "hp")]`.

```csharp
[DistributedEntity(TestEntityTypes.PERSISTED_ZONE_OBJECT, PersistAs = "zobj")]
public partial class ZoneObject : DistributedObjectBase
{
    [Distributed(1, PersistAs = "pos")]   // stored
    public DistributedValue<Vector2Int, Vector2IntSerializer> Position;

    [Distributed(2)]                       // replicated but NOT stored
    public DistributedValue<Vector3, Vector3Serializer> Direction;
}
```

Rules the server and manager enforce:

- A field can only be persisted if its **declaring type** is persisted (has a `PersistAs`). Persistence does **not** inherit: a subclass of a persisted type is only persisted if it declares its own `PersistAs`. A non-persisted subclass may still inherit persisted fields from its base — on that subclass they are simply replicated-only.
- A persisted type's `PersistAs` key must be **unique across all entity types** (it is the durable type identity — this throws at registration).
- A persisted **type** must have **at least one** persisted field (otherwise it would store nothing — this throws at registration).
- A persisted **object** must be created in a **persisted channel**.
- **Temporal fields cannot be persisted.**
- A persisted object with no `UniqueName` gets a server-generated GUID as its database key.

Only persisted fields are written; non-persisted fields are replicated live but never stored. Each stored entity records its **type's `PersistAs` key** (never the numeric type id), and its field values are stored under their field-level keys. When a persisted channel is loaded, the server resolves each stored type key back to the registered entity type and re-applies the field values by their keys — which is exactly why those keys are your [durable schema](#why-these-ids-are-immutable).

### Working with persisted state directly

`ClientEntityManager` exposes two helpers, handy for offline tools and editor workflows:

- `GetPersistedFieldsAsBson(entity)` → a `BsonDocument` of the entity's persisted fields keyed by their `PersistAs` names.
- `ApplyPersistedFieldsFromBson(entity, doc)` → applies such a document back onto an entity.

Because an entity resolves its type id from its `[DistributedEntity]` attribute at construction, these work even on a freshly-`new`'d instance, as long as its type is registered with the manager.

---

## 10. Locks

A lock is a server-enforced exclusive claim on an entity. While an entity is locked by one connection, the server **rejects property updates and delete requests from every other connection**.

```csharp
entity.TryLock((err, gotIt) =>
{
    if (gotIt) { /* we hold the lock; others can't modify the entity */ }
    else       { /* someone else holds it */ }
});

entity.Unlock((err, released) => { });
```

- `TryLock` succeeds if the entity is unlocked or you already hold it; it fails (`false`) if another connection holds it.
- `WaitForLock` tries to lock and, if another client holds it, defers your callback until the lock is released — then fires with `LockWaitResult.Unlocked`. **That result signals availability only; it does not re-acquire the lock for you.** Call `TryLock` again to claim it. *(See [§14](#14-known-caveats).)*
- `Unlock` releases a lock you hold (`released: false` if you didn't hold it).
- `IsLocked` reflects whether the entity is currently locked by anyone; `OnLocked`/`OnUnlocked` fire as that state changes (and are kept in sync with the server).
- Locks held by a connection are released automatically when it disconnects.

Client-authoritative objects are simply locked to their creator from the moment they are created.

---

## 11. Events

An event is a one-shot, fire-and-forget message attached to an entity — no stored state, not persisted. The server relays it to **every** subscriber of the entity's channel, **including the sender**.

```csharp
// send
entity.TriggerEvent(eventType: 1, eventData: new BsonDocument { ["msg"] = "hi" }, onComplete);

// receive (override or subscribe to OnEventTriggeredEvent)
public override void OnEventTriggered(int eventType, BsonValue eventData) { … }
```

Use events for transient signals — a hit, a sound cue, a one-off notification — anything that shouldn't live in a field.

---

## 12. Niche topics

### Temporal fields (`DistributedTemporalValue<T,S>`)

A temporal value is a single value that also carries timing information and an optional **cooldown lock**. It exists for state that several clients update cooperatively, where you care *when* it last changed.

```csharp
[Distributed(15)]
public DistributedTemporalValue<MovementState, MovementSerializer> Movement;

// On first load you learn how old the value is:
Movement.OnInitialized += (value, age) => { /* extrapolate `value` forward by `age` */ };
```

Two extra capabilities over a plain `DistributedValue`:

1. **Age on load.** When the field's initial state arrives, `OnInitialized(value, age)` reports how long ago (server time) the value was last modified — so a late joiner can extrapolate or interpolate rather than snapping to a stale value. The field also exposes `LastModifiedTime`.

2. **Cooldown lock (shared control without ownership).** Set a value with a lockout:

   ```csharp
   Movement.Set(newState, updateLockout: TimeSpan.FromMilliseconds(500));
   ```

   Once the server accepts this update, it **silently drops any entity update that touches this field — from any client, including the sender — until the lockout expires.** First update to reach the server wins; the rest are ignored until the window passes. This lets multiple clients share write access to one object with no explicit lock or owner: whoever acts first "holds" the value for the cooldown. Inspect the active lock with `IsCooldownLocked` and `CooldownRemaining`.

   > **Subtlety:** the server's drop applies to the *whole update message*. If a temporal field under an active cooldown is batched with other field changes in the same frame, the entire batch for that entity is dropped. Keep cooldown-locked temporal writes separate from unrelated field writes if you don't want them suppressed together.

   Temporal locking currently applies to single values; the collection field types do not have temporal variants.

### Local-only setters (`SetLocalOnly`)

Every value field has `SetLocalOnly(value)`, which updates the **local** current value and fires `OnChanged` **without sending anything to the server**:

```csharp
ghost.Position.SetLocalOnly(predictedPosition);   // client-side prediction; not authoritative
```

Use it for client-side prediction, cosmetic/interpolated state, or any value you want to reflect locally now and reconcile later from the authoritative server stream. Because it bypasses the network, the next genuine server update for that field will overwrite whatever you set.

### Unguaranteed sends (`SetUnguaranteed`)

`SetUnguaranteed(value)` behaves like `Set` but flags the update as best-effort (it may be sent over an unreliable transport and may be dropped or reordered). Use it for high-frequency, self-correcting streams — positions, rotations — where the latest value matters and a missed intermediate frame is harmless. The per-field sequence numbers ensure a late straggler never clobbers a newer value. For temporal values, `SetUnguaranteed` has the same cooldown overload as `Set`.

---

## 13. Quick reference

### Declaring

```csharp
[DistributedEntity(typeId, FactoryMethod = "…", PersistAs = "…")]   // typeId > 0, 0 reserved
public partial class Foo : DistributedObjectBase   // or DistributedChannelBase, or the MonoBehaviour bases
{
    [Distributed(fieldId, PersistAs = "…")]        // fieldId 1–63
    public DistributedValue<T, TSerializer> Bar;
}
```

### Field types

`DistributedValue` · `DistributedTemporalValue` · `DistributedArray` · `DistributedQueue` · `DistributedIntDictionary` · `DistributedStringDictionary` — each `<T, S>` with a serializer `S`.

### Set semantics

| Call | Sends? | Local apply? | Delivery |
|---|---|---|---|
| `Set(v)` | yes | only if client-authoritative / offline | guaranteed |
| `SetUnguaranteed(v)` | yes | only if client-authoritative / offline | best-effort |
| `SetLocalOnly(v)` | no | always | — |
| `Set(v, cooldown)` *(temporal)* | yes | only if client-authoritative / offline | guaranteed, with server cooldown lock |

### Operations (on `IDistributedEntity`)

`TriggerEvent` · `Delete` · `TryLock` · `WaitForLock` · `Unlock` — all callback-based. Channels add `Unsubscribe`. The manager adds `CreateObject`, `CreateChannel`, `SubscribeToChannel`, `UnsubscribeFromChannel`, `GetFieldSchema`, `GetPersistedFieldsAsBson`, `ApplyPersistedFieldsFromBson`.

### Per-frame

Call `connection.Update()` every frame. It dispatches inbound messages (firing your callbacks) and flushes outbound dirty fields.

---

## 14. Known caveats

A few sharp edges to be aware of (these reflect the current implementation and are candidates for cleanup):

- **`IsClientAuthoritative` is not restored from server flags** on entities you *receive* (only `IsPersisted` is). Treat it as reliable only on the connection that created the entity.
- **`WaitForLock` does not re-acquire.** Its deferred `Unlocked` result means "the lock became free," not "you now hold it" — call `TryLock` again. `LockWaitResult.Timeout` is defined but not currently produced (no timeout is applied).

---

*This manual covers the essentials. For exact signatures and behavior, the XML doc comments on `IDistributedEntity`/`DistributedEntityBase` (`Client/Connection/ClientEntityTypes.cs`), `ClientEntityManager`, the field types (`Client/Connection/DistributedFields.cs`), and the server's `GameStateLive` are the authoritative reference.*
