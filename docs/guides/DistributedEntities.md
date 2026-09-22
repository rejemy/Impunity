# Distributed Entities

A guide to Impunity's real-time state replication system: distributed **entities**, the **channels** that contain them, and the **distributed fields** that sync their state between clients through the server.

This document covers the essentials. It assumes you already have a working `GameStateServer` and a client `BaseGameConnection` (a `RemoteGameConnection` over TCP, or a `LocalGameConnection` in-process) — see the companion guide [`Connections.md`](Connections.md) for how connections, the action system, and the database work. See the project `CLAUDE.md` for the build/run layout and the wire protocol.

> **Conventions used here.** "Object" means a `DistributedObjectBase`/`IDistributedObject` instance; "channel" means a `DistributedChannelBase`/`IDistributedChannel`; "entity" means either. Code samples use the field/serializer types from `Impunity.Connection`.

---

## Contents

1. [Mental model](#1-mental-model)
2. [Type ids, field detection, and automatic field ids](#2-type-ids-field-detection-and-automatic-field-ids)
3. [Declaring an entity type](#3-declaring-an-entity-type)
4. [Distributed fields](#4-distributed-fields)
5. [Information flow: client → server → client](#5-information-flow-client--server--client)
6. [Subscriptions and the entity lifecycle](#6-subscriptions-and-the-entity-lifecycle)
7. [Creating channels and objects](#7-creating-channels-and-objects)
8. [Client-authoritative objects](#8-client-authoritative-objects)
9. [Persistent objects](#9-persistent-objects)
10. [Locks](#10-locks)
11. [Conditional actions](#11-conditional-actions) — moving items between the world (create, delete, update) and the database without duplicates
12. [Events](#12-events)
13. [Niche topics](#13-niche-topics) — exclusive updates, temporal fields, local-only setters, unguaranteed sends
14. [Quick reference](#14-quick-reference)
15. [Known caveats](#15-known-caveats)

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

The server is the single source of truth. A client mutates a field locally; the change is sent to the server; the server applies it (and persists it if the field is persisted), then relays it to every other subscriber. **By default a client's own reads do not reflect its own writes until the server echoes the change back** — see [§5](#5-information-flow-client--server--client). Two opt-outs from that rule exist: [client-authoritative](#8-client-authoritative-objects) entities and [local-only setters](#13-niche-topics).

Each client drives the system by calling `connection.Update()` once per frame. That single call dispatches inbound server messages to your entities (firing their callbacks) and flushes your pending outbound changes. Nothing happens between `Update()` calls.

---

## 2. Type ids, field detection, and automatic field ids

Every distributed type and every distributed field is identified on the wire by a small **numeric id**, not by its name, which keeps updates compact. You assign the **type** id; fields need no annotation at all — they are recognised by their type and numbered automatically (see [below](#how-field-ids-are-assigned)). Both id kinds are wire-only — neither is ever written into saved data.

### `[DistributedEntity(typeId)]`

Marks a class as a distributed entity type and assigns its **type id**.

```csharp
[DistributedEntity(TestEntityTypes.PLAYER)]            // typeId = 2
public partial class Player : DistributedObjectBase { … }
```

- `typeId` must be a **positive integer**, unique across all entity types you register, and **permanent** — once shipped it can never change or be reused (see [below](#wire-ids-vs-durable-keys)). **Id `0` is reserved** for the built-in untyped channel (`GenericDistributedChannel`).
- Optional `FactoryMethod = "Name"` — the name of a `public static` parameterless method on the type returning an `IDistributedEntity`. The manager calls it to construct received instances instead of `Activator.CreateInstance`. Use it for types without a public default constructor or that need custom setup.
- Optional `PersistAs = "key"` — marks the type **persisted** and gives it a database key. See [§9](#9-persistent-objects).
- The class **must be `partial`** (the source generator adds a second part — see below) and must derive from one of the base classes in [§3](#3-declaring-an-entity-type).

### Declaring a distributed field

**There is no attribute.** A field is distributed if — and only if — its type implements `IDistributedField`, which every `DistributedValue` / `DistributedArray` / `DistributedQueue` / `DistributedStack` / `Distributed*Dictionary` (and their `Temporal` variants) does. Declaring one *is* the declaration of intent; there was never a reason to hold such a field and not replicate it.

```csharp
public DistributedValue<Vector3, Vector3Serializer> Position;      // replicated

[PersistAs("name")]
public DistributedValue<string, StringSerializer> Name;            // replicated AND stored
```

- Only **instance** fields count. A `static` or `const` field of a distributed type is ignored, not replicated.
- Field visibility is irrelevant — a `private` distributed field replicates like any other.
- A type may have at most **63 distributed fields across its whole inheritance chain**. The limit is hard: each field maps to one bit of a 64-bit dirty mask (`1UL << (id - 1)`). Exceeding it throws at registration, naming the type.
- Distributed field names must be **unique across the inheritance chain** — a subclass may not shadow an inherited distributed field (throws at registration).

> **Adding a distributed field changes the wire schema.** With no attribute in the way, a new field declaration is a schema declaration: it shifts ids, changes the format checksum, and needs a version bump. That is the deliberate trade for not having to mark anything up.

### `[PersistAs("key")]`

Stores a distributed field's value in the database under `key`. This is the one thing a field's *type* cannot express, so it is the only field-level attribute left.

```csharp
[PersistAs("hp")]
public DistributedValue<int, Int32Serializer> Health;
```

- The declaring entity type must itself be persisted (have its own `PersistAs`), and the field may not be temporal. See [§9](#9-persistent-objects).
- The key must be non-empty and must not start with `_`.
- Applying it to a field that is not a distributed field is a compile error (`IMP5`).

Note the two spellings are different things: `[DistributedEntity(id, PersistAs = "…")]` is a *named property* on the type-level attribute, while `[PersistAs("…")]` is the field-level attribute.

### How field ids are assigned

`Impunity.Connection.DistributedFieldIds` derives every field's wire id from the entity type itself, walking the inheritance chain **base class first**, then by **ordinal field name** within each class, numbered from 1.

The ids are **per concrete entity type**. Because each registered type gets its own property table on the server, sibling subclasses never have to agree: an inherited field may hold a different id in each subclass, and that is fine. This is what removes the old requirement that ids be unique across a class *and all of its parents* — the bookkeeping that made deep hierarchies painful.

What follows from the ordering:

| You do this | Effect |
|---|---|
| Add a field to a subclass | Inherited fields keep their ids; the new field is numbered after them |
| Reorder or move declarations in source | Nothing changes — declaration order is not used |
| Add or remove a distributed field | Later fields *in the same class* renumber — a schema change (bump the version) |
| **Rename** a field | It renumbers — a schema change (bump the version) |

Two details worth knowing. The ordering is **imposed, not inherited from reflection**: `Type.GetFields` does not guarantee an order, so the chain is walked one level at a time and sorted. And the walk uses `BindingFlags.DeclaredOnly` per level, because a flattened `GetFields` silently omits a base type's `private` fields — which would leave such a field holding a dirty bit that nothing ever serializes.

The same table is used in both places a field id is needed, so they cannot disagree: the generated `InitializeDistributedFields` (which turns the id into the field's dirty bitmask) and `ClientEntityManager.RegisterEntityType` (which puts it on the wire).

### Wire ids vs. durable keys

The two identity systems split cleanly by medium — **numbers identify things on the wire, `PersistAs` strings identify things at rest** — and carry very different rules. Keeping this straight is the single most important thing to understand before you ship:

| Identity | What it identifies | Rule |
|---|---|---|
| **Numeric ids** (`[DistributedEntity(n)]`, and the auto-assigned field ids) | The **wire identity** of every type and field, exchanged on the connect handshake and carried compactly in every update. Numeric ids are never written into saved data. | **Fixed per schema version.** Every build sharing a schema version must agree on the numbers; changing one is a schema change like any other (bump the version and move all builds together). Saved data is unaffected. |
| **`PersistAs` string keys** | The **durable identity** of everything stored in the database: each persisted entity row records its type's key (the stored `t`), and each persisted field value is stored under the field's key. | **Immutable forever** once data exists. Renaming a key orphans everything stored under the old one (recoverable only via a data migration). Entity-level keys must be **unique across all entity types**. |

Practical consequences:

- **The split is deliberate:** compact numbers keep replication traffic small; longer meaningful strings make the data at rest self-describing (and easy to identify in migration code, which sees raw BSON rows).
- **Moving and reorganizing code is free.** You can move a `[DistributedEntity]` class to a different namespace or reorder its fields and neither the wire nor the database notices. Renaming is *nearly* free: renaming a **class** changes nothing, but renaming a **field** renumbers it, which is a wire change (bump the version) though never a data change.
- **Treat `PersistAs` keys as your permanent storage schema.** Choose them deliberately; once a save exists they can never change. Keys may not be empty and may not start with `_` (reserved), and a type's key may not be shared with another type (throws at registration).
- **Renumbering is a coordinated wire change, not a data change** — it alters the format checksum, so it needs a version bump and all builds updated together, but existing saves reload untouched. A format change also unloads the server's live state so the world is re-read through the new schema (see [`SchemaMigration.md`](SchemaMigration.md) §8).

### The source generator

Entity types are `partial` because the `ImpunityCodeGenerator` Roslyn source generator emits the other half at compile time. For each `[DistributedEntity]` class it generates:

- An `override void InitializeDistributedFields()` that wires every distributed field to its owning entity and its assigned id, resolved through `DistributedFieldIds.ForType(GetType())`. It uses the *runtime* type, because an inherited field's id depends on the full field set of the object actually being constructed. This runs from the entity's base constructor, so fields are ready to use immediately after `new` — including on offline instances that are never registered.
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
    public static IDistributedEntity Create() => new Player();

    public DistributedValue<Vector3, Vector3Serializer> Position;

    public DistributedValue<int, Int32Serializer> Health;

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
    public DistributedValue<string, StringSerializer> Status;

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

Under the hood the manager's `RegisterEntityTypes(Type[])` walks each type by reflection, assigns every field its wire id, and produces the `GameStateEntityTypeDef[]` sent to the server during the handshake. The server adopts that format, so client and server share one definition of every type.

> A type with **no** distributed fields is legal (see `TestEmptyObj`) — useful as a bare presence/identity object or a marker channel.

---

## 4. Distributed fields

A distributed field is a generic struct `Field<T, S>` where `T` is the value type and `S` is a **serializer struct** that knows how to read/write `T`. You always supply both:

```csharp
public DistributedValue<int, Int32Serializer> Score;
public DistributedValue<Vector3, Vector3Serializer> Position;
```

### The field types

| Type | Shape | Key methods | Change events |
|---|---|---|---|
| `DistributedValue<T,S>` | single value | `Get()`, `Set(v)` | `OnChanged(old, new)` |
| `DistributedTemporalValue<T,S>` | single value + last-modified timestamp | `Get()`, `Set(v)` | `OnChanged`, `OnInitialized(v, age)` |
| `DistributedArray<T,S>` | fixed-size array | `Init(n)` / `Replace(coll)`, `Get(i)`, `Set(i, v)` | `OnChanged(i, old, new)`, `OnReplaced(old, new)` |
| `DistributedQueue<T,S>` | bounded FIFO (evicts oldest) | `Init(cap)` / `Replace(cap, vals)`, `Add(v)` | `OnChanged(v)`, `OnReplaced(old, new)` |
| `DistributedStack<T,S>` | LIFO stack (starts empty, no `Init`) | `Push(v)`, `Pop()`, `SetTop(v)` (pushes if empty), `Peek()` / `TryPeek(out v)`, `Replace(vals)` (last value = top), `Clear()` | `OnPushed(v)`, `OnPopped(v)`, `OnTopChanged(old, new)`, `OnReplaced(old, new)` |
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

`Set(value)` returns `false` (and does nothing) if `value` equals the current value, unless you pass `force: true`. For mutable value types this deduplication is switched off — see below.

### Mutable values: treat `Get()` as read-only

> **The rule: never modify a value you got from `Get()`. Copy it, change the copy, and `Set` the copy.**

This matters when `T` is something whose contents can be changed in place — an object serialized with `BsonSerializer<T>`/`BsonSmallSerializer<T>`, a byte buffer behind a `BlobSerializer`, or any custom serializer over a mutable type. `Get()` hands back the field's **own state, not a copy**, so modifying it writes straight through the field's records:

```csharp
// WRONG — mutates the field's own state
var loadout = player.Loadout.Get();
loadout.Weapon = "axe";
player.Loadout.Set(loadout);

// RIGHT — the field's state is only ever replaced, never edited underneath it
var loadout = player.Loadout.Get().Clone();   // or: new Loadout(player.Loadout.Get()) { Weapon = "axe" }
loadout.Weapon = "axe";
player.Loadout.Set(loadout);
```

A blob is the same hazard wearing a struct: `ArraySegment<byte>` is a value type, but it only points at an array, and its `Equals` compares the array reference, offset and count — never the bytes. Rewriting the buffer you handed to `Set` is exactly as invisible to a comparison as editing an object's members, which is why `BlobSerializer` is `Mutable`.

Each field's serializer declares which kind of value it holds via `IDistributableValueSerializer<T>.ValueSemantics`:

| | `Immutable` | `Mutable` |
|---|---|---|
| Serializers | primitives, `StringSerializer`, `DateTime*`, `Guid`, Unity structs, custom serializers over immutable types | `BsonSerializer<T>`, `BsonSmallSerializer<T>`, `BlobSerializer`, custom serializers over mutable types |
| Requires meaningful `Equals` | yes — see [Serializers](#serializers) | no, the value is never compared |
| Unchanged `Set` | returns `false`, field stays clean | returns `true`, field is marked dirty |

A field over a `Mutable` serializer **skips the unchanged check entirely**. It has no retained baseline to compare against, so when you pass back a value you modified in place, `value` and the current value are the same object and any equality comparison — however carefully `T.Equals` is written — compares the object with itself and reports "unchanged". Rather than silently swallow that update, the field treats every `Set` of a mutable value as a change. (This is also why `force: true` is no longer needed for these fields.)

The cost is that re-setting an unchanged mutable value sends an update anyway, so **doing it every frame sends every frame**. That works correctly, but it is an anti-pattern: `Set` when the value actually changed.

Following the copy-then-`Set` rule also avoids three related hazards that the always-dirty behavior cannot fix:

- **Modify and never `Set`.** Nothing marks the field dirty, so the change stays local forever — there is no call for the field to intervene at.
- **Modify after `Set`.** The pending value is a reference, and it is not serialized until the frame's update flush, so edits made in between are sent too.
- **Modifying on a non-client-authoritative entity.** `Get()` is contractually the last **server-confirmed** value; editing it in place makes local reads disagree with the server before any echo, and leaves nothing to fall back to if the server rejects the update (for example because another client holds the entity's lock). It self-corrects on the next update for that field.

`OnChanged(old, new)` also receives the same reference for both arguments if you modified the value in place, so comparing them detects nothing.

### Collections must be initialized before use

Collection fields start empty and **must be initialized** with `Init(…)` or `Replace(…)` before you `Get`/`Set`/`Add` (those throw otherwise). Initialization queues a full-state send; subsequent `Set`/`Add` calls send compact per-element/per-key **deltas**.

> **Timing gotcha.** After you subscribe to or create a channel, tick a few frames before mutating a collection you populated, so the initial full-state send is flushed first and later mutations travel as deltas. Modifying in the same frame as initialization can fold the change into the initial send. (This is also noted in `CLAUDE.md`.)

### Serializers

The `S` parameter is a zero-size struct implementing `IDistributableValueSerializer<T>`. Built-in serializers cover the primitives and common Unity types:

- **Primitives:** `BoolSerializer`, `Int8/16/32/64Serializer`, `UInt8/16/32/64Serializer`, `FloatSerializer`, `DoubleSerializer`, `DecimalSerializer`, `CharSerializer`, `StringSerializer`, `BlobSerializer` (`ArraySegment<byte>`), `DateTimeSerializer`, `DateTimeOffsetSerializer`, `TimeSpanSerializer`, `GuidSerializer`.
- **Unity types:** `Vector2/3Serializer`, `DVector4Serializer`, `Vector2Int/Vector3IntSerializer`, `ColorSerializer`, `Color32Serializer`, `QuaternionSerializer`, `Matrix4x4Serializer`.
- **Arbitrary types:** `BsonSerializer<T>` and `BsonSmallSerializer<T>` serialize any type via UltraLiteDB's BSON mapper (use `BsonSmall` for compact small payloads). They use Impunity's shared mapper, which writes no `_type` names and allows none, so a replicated value can only ever be read as its declared member types, whatever another client sends. Members declared as a base class, interface or `object` therefore don't keep their derived type. If you need that, give the serializer a mapper with `IncludeFullType` on and your own data types allowed (`BsonSerializer<T>.Mapper = new BsonMapper { IncludeFields = true }.AllowTypes("MyGame.Data.*")`). Never `AllowAllTypes`: every other client's values go through it.

To support a custom type, implement the interface — write the binary form, the BSON form, and report a `ValueType` tag and a `ValueSemantics` kind:

```csharp
public struct MovementSerializer : IDistributableValueSerializer<MovementState>
{
    public void WriteTo(MovementState v, BinaryWriter w) { w.Write(v.X); w.Write(v.Y); }
    public MovementState ReadFrom(BinaryReader r) => new MovementState(r.ReadSingle(), r.ReadSingle());
    public BsonValue ToBsonValue(MovementState v) => /* … */;
    public MovementState FromBsonValue(BsonValue b) => /* … */;
    public GameStateEntityPropertyValueType ValueType => GameStateEntityPropertyValueType.CustomSmallNullable;
    public DistributedValueSemantics ValueSemantics => DistributedValueSemantics.Immutable;
}
```

`T` has no constraints. Field types formerly required `T : IEquatable<T>`; that requirement is gone, because `ValueSemantics` now decides whether the field compares values at all. Equality for `Immutable` values runs through `EqualityComparer<T>.Default`, which dispatches to `IEquatable<T>` when the type implements it and falls back to `Equals(object)` otherwise.

**Null is a valid value** for the nullable value types — `String`, `Blob`, `CustomSmallNullable` and `CustomNullable` (the last two being what `BsonSerializer<T>` and `BsonSmallSerializer<T>` report). On the wire, `FramingSerializer` writes a single false byte and reads it back as null. In the BSON path, `ToBsonValue`/`FromBsonValue` map null to and from `BsonValue.Null`, and a stored null survives a database round trip: `ApplyPersistedFieldsFromBson` applies it, clearing the field.

The other value types have no null to represent. A null stored against one of them is ignored rather than applied, because their serializers would unbox a null onto a struct and throw; `FramingSerializer.IsNullable(valueType)` is the check. A field the document does not mention at all is also left unchanged, which is what lets a document written before that field existed still load — absence and a stored null are distinct, so the manager probes with `ContainsKey` rather than trusting the indexer (`BsonDocument`'s indexer returns `BsonValue.Null` for a missing key).

If you write a custom serializer over a nullable type, handle null in both BSON methods — the binary framing covers null for you, but nothing guards your BSON conversions.

> **`Immutable` is an assertion that your type has meaningful value equality.** The compiler no longer checks for an `Equals` implementation, so a class that inherits reference equality and is declared `Immutable` will fail to deduplicate — the exact silently-dropped-update bug the `Mutable` path exists to prevent. Structs are safe by default (`ValueType.Equals` compares fields); classes need an explicit `Equals`, or a `Mutable` declaration.

The `ValueType` tag (`Custom`/`CustomSmall` and nullable variants for complex values, or a primitive tag) is reported to tools via `ClientEntityManager.GetFieldSchema`.

`ValueSemantics` answers one question: **could a caller change this value's contents without constructing a new one?** Answer `Mutable` if so — a class with settable members, or a struct that only points at mutable storage (this is why `BlobSerializer` is `Mutable` even though `ArraySegment<byte>` is a struct: its `Equals` compares the array reference, offset and count, never the bytes). Answer `Immutable` only when the type genuinely cannot be modified in place. Both this and `ValueType` are compile-time constants on a concrete serializer struct, so the branches they drive fold away in specialized generics — declaring `Mutable` costs nothing at runtime.

> Getting this wrong in the `Immutable` direction reintroduces the silently-dropped-update bug for your type, so when in doubt, declare `Mutable`.

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
- **Sequence numbers guard against staleness.** Each entity has an outgoing `SendSeq`; the server tracks a per-field received-sequence and ignores out-of-order updates; the server stamps relays with an `OutSeq`; each client tracks a per-field `FieldRecvSeq` and ignores stale inbound updates. This matters because updates can be sent unguaranteed (best-effort) and arrive out of order — see [§13](#13-niche-topics).
- **Guaranteed vs. unguaranteed.** `Set` flags the update **guaranteed** (reliable delivery). `SetUnguaranteed` flags it best-effort. If any dirty field on an entity in a frame is guaranteed, that frame's update for the entity is sent reliably.
- **The writer's echo is what updates its own `Get()`** for non-authoritative entities. For client-authoritative entities the server deliberately does *not* echo to the writer (it already applied locally), avoiding a redundant round trip.
- **`UpdateExclusive` short-circuits the batch and adds an optimistic-concurrency guard.** It flushes one entity's dirty fields immediately (not on the next `SendUpdates()`) and carries the client's known per-field seqs; the server applies and relays the update only if the client has seen the latest change to every written field, else it rejects the whole update (`ActionStaleData`) with nothing applied. See [§13](#13-niche-topics).

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
| `OnObjectRemoved(obj)` *(channels)* | An object leaves the channel. *(See [§15](#15-known-caveats).)* |
| `OnEventTriggered(type, data)` | A one-shot [event](#12-events) is fired on the entity. |
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
- `IsClientAuthoritative` is meaningful as a *request set before creation*. (See [§15](#15-known-caveats) for a note on how it is represented on entities you receive from the server.)

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
2. Give each field you want stored a `PersistAs` key: `[PersistAs("hp")]`.

```csharp
[DistributedEntity(TestEntityTypes.PERSISTED_ZONE_OBJECT, PersistAs = "zobj")]
public partial class ZoneObject : DistributedObjectBase
{
    [PersistAs("pos")]                     // stored
    public DistributedValue<Vector2Int, Vector2IntSerializer> Position;

                                           // replicated but NOT stored
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
- `WaitForLock` tries to lock and, if another client holds it, **queues you on the server**. When the current holder releases, the lock is handed to the longest-waiting connection and your callback fires with `LockWaitResult.Locked` — you hold it, there is no race to win. No timeout is applied, so a holder that never releases leaves the callback pending; use `RunExclusive` if you need a deadline.
- `Unlock` releases a lock you hold (`released: false` if you didn't hold it).
- `IsLocked` reflects whether the entity is currently locked by anyone; `OnLocked`/`OnUnlocked` fire as that state changes (and are kept in sync with the server). `OnUnlocked` fires only when the lock is released with nobody queued — when it is handed straight to a waiter the entity never becomes free, so listeners see `OnLocked` for the new holder instead. **Don't build a retry loop on `OnUnlocked`** (see below).
- Locks held by a connection are released automatically when it disconnects, and any queue it was sitting in is left cleanly.

> **`TryLock` polling can starve.** Because the queue is served first, a client that loops on `TryLock` — retrying when it fails, or waking on `OnUnlocked` — is never handed the lock and may never see it free while anyone is queued. `OnUnlocked` will not even fire on a handoff. If you need the lock, get in line: use `WaitForLock` or `RunExclusive`. `TryLock` is for "take it if it happens to be free right now".

Client-authoritative objects are simply locked to their creator from the moment they are created.

A lock is the right tool when a client needs *exclusive* access across several operations or some time. For the common case of a one-shot contended write — two players grabbing the same item, flipping the same switch — an explicit lock/unlock round trip is heavy boilerplate for a contention that is rare in practice. `UpdateExclusive` ([§13](#13-niche-topics)) handles that case optimistically: everyone just writes, and the server lets exactly one win. (A lock holder's `UpdateExclusive` bypasses the staleness check — the lock is the stronger guarantee.)

### `RunExclusive` — the scoped form

Taking a lock by hand means getting several things right: waiting for it, releasing it on every exit path including exceptions, and — the subtle one — making sure the edits you made under the lock actually reach the next holder before it starts. `RunExclusive` does all of that.

```csharp
chest.RunExclusive(() =>
{
    // Runs only while we hold the lock. No other client can be inside its own
    // scope on this entity at the same time.
    chest.Gold.Set(chest.Gold.Get() - 10);
    chest.Contents.Add("torch");
},
(err, result) =>
{
    if (result == RunExclusiveResult.Ran)      { /* the logic ran, lock released */ }
    else if (result == RunExclusiveResult.TimedOut) { /* never got the lock; nothing ran */ }
    else                                       { /* Failed — err says why */ }
},
timeoutSeconds: 5f);
```

What it guarantees:

- **Fair waiting.** If another client is inside its scope, you join the server's queue and are handed the lock in turn — first come, first served.
- **The handoff guarantee.** Your body is guaranteed to observe every edit the *previous* holder made under the lock. (This one is not exclusive to `RunExclusive`: `Unlock` flushes the entity's pending edits before sending the release, so a hand-rolled `TryLock` / edit / `Unlock` gets the same guarantee. It matters because field writes otherwise only mark dirty bits and are flushed on the next `Update()` — without the flush, the release would reach the server *ahead* of the write and the next holder would read stale values.)
- **The lock is always released** — when the body returns, and equally when it throws. A thrown exception is reported as `RunExclusiveResult.Failed` with the message in `err`; edits made before the throw are still flushed, since the entity has already been mutated locally.
- **Your own scopes serialize.** Two `RunExclusive` calls on the same entity from the same connection run one after the other, in call order, never nested.

The timeout covers **acquiring** the lock only — the body's own running time is never counted against it. A negative value (the default) uses `ClientEntityManager.DefaultExclusiveTimeoutSeconds` (10s); `0` means "only if the lock is free right now".

`RunExclusiveAsync` and `RunExclusiveYield` also accept a body that spans frames — `Func<Task>` and, in Unity, `Func<IEnumerator>` with a host `MonoBehaviour` to run it on. The lock is held for the whole body, so keep it short: everyone else is sitting in the queue.

```csharp
var result = await chest.RunExclusiveAsync(async () =>
{
    var doc = await conn.FindDocumentAsync(Collections.LOOT, chest.LootId.Get());
    chest.Contents.Add(doc["item"].AsString);
}, timeoutSeconds: 5f);
```

Not covered: fields written with `SetUnguaranteed`. Those are sent unreliably, outside the ordered path the guarantee rests on, so they may reach the next holder late or not at all. That is by design — they exist for lossy client-authoritative movement data.

---

## 11. Conditional actions

Moving an item between the world and a player's inventory touches two stores: a live entity and a database document, which the server handles on two different threads. Sent as two separate requests, a connection that drops between them — or a client bug — leaves only one of them done, and the item either vanishes or is duplicated. And when two players reach for the same item, exactly one of them must get it.

`CreateObject`, `Delete`, and entity updates can carry a **conditional action**: a database action that travels in the same message and that the server runs only if the entity operation succeeded for this client.

```csharp
// Pickup: remove the item from the world, and add it to my inventory only if I was the one who removed it.
worldItem.Delete(null,
    (err, deleted) => { /* true: it's gone from the world */ },
    inventory.MakeInsertAction(new InventoryItem { Id = worldItem.ItemId.Get() },
        (err, id) => { /* fires after the delete's callback, with the insert's own result */ }));

// Drop: create the item in the world, and remove it from my inventory only if the create worked.
var dropped = new WorldItem();
dropped.ItemId.Set(item.Id);
manager.CreateObject(dropped, zone, false,
    (err, obj) => { },
    inventory.MakeDeleteAction(item.Id, (err, removed) => { }));
```

The raw connection methods take the same parameter: `connection.CreateObject(…, onComplete, onCreatedAction)` and `connection.DeleteEntity(…, onComplete, onDeletedAction)`, as do the `…Async` and `…Yield` wrappers.

### Updates: replicated actions

For an entity whose *fields* change — a potion's effect on the player, an item going into a chest — attach the action to the entity with `AddReplicatedAction`. It rides the entity's next update:

```csharp
// Potion: the effect lands on my (client-authoritative) player, and the potion leaves my inventory with it.
player.Health.Set(player.Health.Get() + 50);
player.AddReplicatedAction(inventory.MakeDeleteAction(potion.Id, (err, removed) => { }));

// Chest: put the item in with an optimistic exclusive update; it leaves my inventory only if the update is accepted.
chest.Contents.Add(item.Kind);
chest.UpdateExclusive(err => { /* ActionStaleData: someone else changed the chest; retry */ },
    inventory.MakeDeleteAction(item.Id, (err, removed) => { }));
```

- **"Next update" is whichever send comes first:** the per-frame sweep in `Update()`, an `UpdateExclusive`, or the flush before `Unlock` (so an action attached inside `RunExclusive` rides the update sent under the lock). The field changes pending at that moment travel with it. `UpdateExclusive(onComplete, action)` is shorthand for attaching and flushing in one call.
- **Nothing needs to be dirty.** If no field changed (a `Set` to the same value is a no-op), an empty update is sent just to carry the action. It still gets the server's verdict: a foreign lock rejects it.
- **Always reliable.** An update carrying an action is sent over TCP, even if its only dirty fields were written with `SetUnguaranteed`.
- **Several actions attached before one send** travel together as a single `CompoundDatabaseAction`, and each action's own callback still fires with its own result. They all run even if one fails.
- **A rejected update skips its actions.** That covers a stale `UpdateExclusive`, an entity locked by another client, and an entity that is gone. The skip is reported as `ActionConditionNotMet`. The actions are used up, so **a retry must attach them again** (passing the action to `UpdateExclusive` makes a retry loop do that naturally). For a plain update, which has no callback of its own, the skip is the only sign that the server dropped it.
- **The entity going away** (deleted, unsubscribed) before its next update is sent fails its pending actions locally with `ActionConditionNotMet`; they are never sent.
- **Entities owned by another client.** You can't update another player's client-authoritative entity — an NPC they control, say. Their lock refuses the update, so the action is skipped: safe, but it won't happen.

### How it runs

- **The entity operation goes first, on the live thread.** That thread is single-threaded, so two clients racing to pick up the same item are serialized there: one delete wins, the other fails (`ActionNotFound` if the entity is already gone, `false` if another client holds its lock), and the loser's conditional never runs.
- **The conditional runs next, on the database thread.** The live thread does not wait for it.
- **One message.** The server either receives both halves or neither.
- **Persisted entities.** The entity's own row write, delete, or persisted-field update is queued ahead of the conditional, and database work runs in order, so the world row always lands first.

"Succeeded" means no error for a create, a `true` result for a delete, and for an update, that the server applied it.

### Results

The conditional is an ordinary action with its own callback, and it gets its own reply:

- **The entity operation's callback always fires first**, then the conditional's. With `manager.CreateObject`, the new object is already registered by the time the conditional's callback runs.
- **A skipped conditional still gets a callback.** If the entity operation did not succeed, the conditional is not run and its callback receives `ActionConditionNotMet`. Every conditional callback fires exactly once.
- **A conditional without a callback still runs.** If it fails, the server logs a warning.
- **A `TimeoutError` on the conditional does not mean it didn't run.** It can just mean the database queue was slow.

### What can be a conditional

Any document action: insert, update, upsert, merge-into, merge-insert, delete, find, or list. Build them directly (`new InsertDocumentAction(collectionId, doc, callback)`), or use the typed builders on `GameStateDBCollection<T>`: `MakeInsertAction`, `MakeUpdateAction`, `MakeUpsertAction`, `MakeDeleteAction`.

For several actions, wrap them in a `CompoundDatabaseAction`. It is not atomic: it runs every sub-action even if an earlier one fails, and a delete that finds nothing returns `false` rather than an error.

Anything else (live actions, migration actions) is rejected: the whole request fails with `ActionBadRequest`, and the entity operation is not performed. `AddReplicatedAction` refuses such an action up front instead, throwing `ArgumentException`, because the plain update it would ride has no callback to report the rejection, and its field changes would be dropped silently.

### What it does not guarantee

It is **not a transaction**. The two halves are separate operations and separate database commits.

- **A failed conditional does not undo the entity operation.** A pickup's insert practically can't fail. But a drop whose inventory delete finds nothing still leaves the world item created — a duplicate. That happens with stale or repeated drops: a double click, a retry after a timeout, a second device on the same account, two players emptying one shared container. Mitigations:
  - Block the drop UI until the callback arrives.
  - Re-read the inventory before retrying after a timeout.
  - For placements that can legitimately collide, give the world entity a `UniqueName` derived from the item's instance id. A concurrent second drop then fails with `ActionUniqueNameExists` and skips its conditional. Uniqueness is checked per channel, and it stops protecting you once the first copy has been picked up again.
- **Crash window.** The entity half always commits first. If the server dies between the two commits, the database half is lost. So if the database half is the one that *removes* the item (drop, putting it in a chest, drinking a potion), a crash duplicates it; if it's the one that *adds* it (pickup, taking from a chest), a crash loses it.
- **Choose world-item flags with care:**
  - `ClientAuthoritative` items are auto-locked to their creator, so nobody else can pick them up: their delete returns `false`.
  - `DeleteOnDisconnect` items vanish when the player who dropped them leaves, so the item is lost.
  - Non-persisted items are lost on a server restart.
  - For dropped items, use persisted objects in a persisted channel.

---

## 12. Events

An event is a one-shot, fire-and-forget message attached to an entity — no stored state, not persisted. The server relays it to **every** subscriber of the entity's channel, **including the sender**.

```csharp
// send
entity.TriggerEvent(eventType: 1, eventData: new BsonDocument { ["msg"] = "hi" }, onComplete);

// receive (override or subscribe to OnEventTriggeredEvent)
public override void OnEventTriggered(int eventType, BsonValue eventData) { … }
```

Use events for transient signals — a hit, a sound cue, a one-off notification — anything that shouldn't live in a field.

---

## 13. Niche topics

### Optimistic exclusive updates (`UpdateExclusive`)

The problem: two players pick the same berry. Each sets `bush.HasBerry = false` and the update replicates — both writes "succeed" and both players get a berry. A lock ([§10](#10-locks)) fixes it but is heavy boilerplate for a contention that is rare 99% of the time.

`UpdateExclusive` solves it optimistically. Set your fields as usual, then flush them with a callback instead of waiting for the per-frame sweep:

```csharp
bush.HasBerry.Set(false);
bush.UpdateExclusive(err =>
{
    if (err == null)
        player.Inventory.Add(berry);          // we won — the berry is ours
    // else: someone beat us to it (err.ErrorCode == ActionStaleData); do nothing
});
```

How it works: the update carries the client's known per-field sequence numbers. The server applies and relays it **only if this client has seen the latest change to every field being written**; otherwise it rejects the *whole* update (nothing is applied) and the callback receives an `ActionStaleData` error. Because the loser's write never applies, exactly one of the racing players wins.

Details worth knowing:

- **Written fields only.** Staleness is checked only for the fields in this update. A concurrent change to some *other* field (a constantly-updated position, say) never causes a false conflict.
- **All-or-nothing.** If any written field is stale, the entire update is dropped — batched sibling fields included. There is no partial apply.
- **The winning value is already local when your callback runs.** On both success and rejection, any winning broadcast has been applied before the callback fires, so it is safe to read the entity's current values inside the callback. Wait for the callback before issuing another exclusive update to the *same* field (your own echo hasn't advanced your known seq until then).
- **Locks and client-authoritative entities bypass the check.** If you hold the entity's lock the update always applies (the lock is the stronger guarantee). Client-authoritative entities are auto-locked to their creator, so their creator's exclusive updates always pass.
- **Locked by another connection** yields an `ActionBlockedByLock` error in the callback (unlike ordinary updates, which the server drops silently).
- **Reliable delivery.** Exclusive updates are always sent guaranteed (over TCP), even if the fields were set with `SetUnguaranteed`.
- Yield/async wrappers: `entity.UpdateExclusiveYield()` (coroutine) and `entity.UpdateExclusiveAsync()` (faults on rejection).

### Temporal fields (`DistributedTemporalValue<T,S>`)

A temporal value is a single value that also carries the server timestamp of its last modification — for state where a late joiner cares *how old* the value is.

```csharp
public DistributedTemporalValue<MovementState, MovementSerializer> Movement;

// On first load you learn how old the value is:
Movement.OnInitialized += (value, age) => { /* extrapolate `value` forward by `age` */ };
```

One capability over a plain `DistributedValue`: **age on load.** When the field's initial state arrives, `OnInitialized(value, age)` reports how long ago (server time) the value was last modified — so a late joiner can extrapolate or interpolate rather than snapping to a stale value. The field also exposes `LastModifiedTime`. (For shared write access to one value without an owner, use `UpdateExclusive` above.)

### Local-only setters (`SetLocalOnly`)

Every value field has `SetLocalOnly(value)`, which updates the **local** current value and fires `OnChanged` **without sending anything to the server**:

```csharp
ghost.Position.SetLocalOnly(predictedPosition);   // client-side prediction; not authoritative
```

Use it for client-side prediction, cosmetic/interpolated state, or any value you want to reflect locally now and reconcile later from the authoritative server stream. Because it bypasses the network, the next genuine server update for that field will overwrite whatever you set.

### Unguaranteed sends (`SetUnguaranteed`)

`SetUnguaranteed(value)` behaves like `Set` but flags the update as best-effort (it may be sent over an unreliable transport and may be dropped or reordered). Use it for high-frequency, self-correcting streams — positions, rotations — where the latest value matters and a missed intermediate frame is harmless. The per-field sequence numbers ensure a late straggler never clobbers a newer value.

---

## 14. Quick reference

### Declaring

```csharp
[DistributedEntity(typeId, FactoryMethod = "…", PersistAs = "…")]   // typeId > 0, 0 reserved
public partial class Foo : DistributedObjectBase   // or DistributedChannelBase, or the MonoBehaviour bases
{
    [PersistAs("…")]                               // optional: also store it
    public DistributedValue<T, TSerializer> Bar;
}
```

### Field types

`DistributedValue` · `DistributedTemporalValue` · `DistributedArray` · `DistributedQueue` · `DistributedStack` · `DistributedIntDictionary` · `DistributedStringDictionary` — each `<T, S>` with a serializer `S`.

### Set semantics

| Call | Sends? | Local apply? | Delivery |
|---|---|---|---|
| `Set(v)` | yes | only if client-authoritative / offline | guaranteed |
| `SetUnguaranteed(v)` | yes | only if client-authoritative / offline | best-effort |
| `SetLocalOnly(v)` | no | always | — |

Flush a set of writes immediately with an optimistic-concurrency guard via `entity.UpdateExclusive(onComplete)` — the server rejects the whole update (`ActionStaleData`) if any written field changed since this client last saw it. See [§13](#13-niche-topics).

### Operations (on `IDistributedEntity`)

`TriggerEvent` · `UpdateExclusive` · `Delete` · `TryLock` · `WaitForLock` · `Unlock` — all callback-based. Channels add `Unsubscribe`. The manager adds `CreateObject`, `CreateChannel`, `SubscribeToChannel`, `UnsubscribeFromChannel`, `GetFieldSchema`, `GetPersistedFieldsAsBson`, `ApplyPersistedFieldsFromBson`.

`Delete` and the manager's `CreateObject` take an optional trailing database action that runs only if the entity operation succeeded — the world ↔ inventory move. `AddReplicatedAction(action)` attaches one to an entity's next update (`UpdateExclusive(onComplete, action)` attaches and flushes). See [§11](#11-conditional-actions).

### Per-frame

Call `connection.Update()` every frame. It dispatches inbound messages (firing your callbacks) and flushes outbound dirty fields.

---

## 15. Known caveats

A few sharp edges to be aware of (these reflect the current implementation and are candidates for cleanup):

- **`IsClientAuthoritative` is not restored from server flags** on entities you *receive* (only `IsPersisted` is). Treat it as reliable only on the connection that created the entity.
- **The `RunExclusive` handoff guarantee covers guaranteed fields only.** Fields written with `SetUnguaranteed` leave the ordered path for best-effort delivery, so they can arrive after the next holder's body has already run, or not at all. Do not carry state the next holder depends on in an unguaranteed field.
- **A `TryLock` retry loop can starve under contention.** Waiters queued via `WaitForLock`/`RunExclusive` are served before anyone polling, and a handoff raises `OnLocked` rather than `OnUnlocked`, so a poller watching for the release is not woken at all. Queue instead of polling.
- **Conditional actions are not transactions.** A failed conditional does not undo the create, delete, or update it rode with, and a server crash between the two commits can lose or duplicate an item. See [§11](#11-conditional-actions).
- **Object unique names are checked per channel but registered server-wide.** `CreateObject` rejects a duplicate `UniqueName` only within the same channel, yet the server's name index (and a persisted object's database key) is global. The same name in two channels, or an object named like a channel, silently displaces the earlier entry. Keep unique names globally unique, e.g. by prefixing them with the channel name.
- **A named lock shares its namespace with channels.** `TryToLock("foo")` when a channel named `foo` exists locks *that channel*, not a separate mutex. Waiting is not offered in that case (the request just fails), because the grant is entity-shaped and a caller waiting on a name has nothing to match it against.

---

*This manual covers the essentials. For exact signatures and behavior, the XML doc comments on `IDistributedEntity`/`DistributedEntityBase` (`Client/Connection/ClientEntityTypes.cs`), `ClientEntityManager`, the field types (`Client/Connection/DistributedFields.cs`), and the server's `GameStateLive` are the authoritative reference.*
