> **Status: deferred (2026-11-14).** Not started. The expected saving on a lightly loaded server is tens of
> microseconds, so this waits until relay latency under load is shown to matter. Step 1 (measurement) is the way
> to find out.

# Unguaranteed relay latency on the server

## Context

Unguaranteed entity updates (movement spam over UDP / WebRTC) should be relayed to channel listeners with as
little server-added delay as possible. Today such an update takes the same path as every other live action:

1. **Receive thread** (UDP listener / WebRTC callback): `DispatchClientMessage` BSON-decodes the
   `UpdateEntityAction`, `GameStateServer.QueueAction` puts it on `LiveActionQueue`.
2. **Live thread** (`LiveWorkerThead`): strict FIFO behind every other live action. `GameStateLive.UpdateEntity`
   → `ApplyProps` → `SendUpdateToListeners`, which adds one `PendingSend` per listener to `PendingWrite`.
3. **Writer thread** (`WriterThreadMain`): strict FIFO behind every other outbound message (replies, large
   channel-create pushes being BSON-encoded). Encodes once, then `QueueMessage` → `SendUnguaranteedNow`.

So there are two FIFO queues an update can sit behind and two thread wake-ups. Sequencing is already sound on
both hops and needs no change: the server skips a field the same writer already superseded
(`ApplyProps`, `RecvSeq`/`LastWriter`), and the client skips a relayed field older than what it has
(`ClientEntityManager` `FieldRecvSeq` check, line ~1561). A late position never overwrites a newer one.

## Assessment

- **Relay-before-apply from the receive thread: not recommended.** The entity lookup, lock check, listener
  list, `OutSeq` and `RecvSeq` are all live-thread-only state. Relaying off-thread needs concurrent listener
  snapshots and shared ownership of `OutSeq`, and server state can then end up disagreeing with what clients
  were told (a relay takes seq n+1, a queued guaranteed update takes n+2, then the first is applied last). It
  saves one thread wake-up beyond the plan below. Out of scale with the benefit.
- **What is in scale:** keep both single-threaded engines exactly as they are, and let plain unguaranteed
  updates go to the front of each queue. Worst-case wait drops from "everything queued" to "the one action
  already running". Roughly 150 lines plus tests, no new locks on game state.
- **Honest size of the win:** individual live actions are in-memory and short, so on a lightly loaded server
  this likely saves tens of microseconds, small beside a browser frame. It matters when the live or writer
  queue is actually deep (big channel subscribe/load, bursts of transactions, a large create push being
  encoded). Step 1 measures this so the rest can be judged on numbers.

## Plan

### 1. Measure relay latency (do first)

- Stamp `Stopwatch.GetTimestamp()` on the action in `ServerSideNetworkConnectionProxy.DispatchClientMessage`
  (new non-serialized field on `UpdateEntityAction`), carry it to `EntityUpdateMessageAction` in
  `GameStateChannel.SendUpdateToListeners`, and record the elapsed time where the bytes are handed to the
  transport (`SendUnguaranteedNow`).
- Small internal stats holder on `ImpunityServer` (count, max, a few log-scale buckets, `Interlocked` only).
- New dotnet-only test file `ImpunityTests/UnguaranteedRelayLatencyTests.cs` (`Category=RelayLatency`): one
  client spams `SetUnguaranteed` over real loopback UDP while another floods the live thread (repeated
  subscribe/unsubscribe of a channel with many objects, creates). Report p50/p99/max. Run before and after.

### 2. Fast lane on the live thread

`ImpunityUnity/Assets/Scripts/Impunity/Server/GameState/GameStateServer.cs`

- Add `LiveFastQueue` beside `LiveActionQueue`. In `QueueAction` (after the migration gate, unchanged), an
  `UpdateEntityAction` with `Guaranteed == false` goes to the fast queue. That flag is only ever cleared in
  `DispatchClientMessage` when `MayBeUnguaranteed()` holds (no reply, guard, conditional action or note), so
  nothing that must arrive or be ordered can take the lane.
- `LiveWorkerThead` takes from the fast queue first, else waits on both. Cap consecutive fast takes (e.g. 64)
  before serving one normal action, so a flood cannot starve locks and transactions.
- Shutdown: complete and dispose both. Note `BlockingCollection.TakeFromAny` throws `ArgumentException` once a
  collection is completed; either handle that or use one `SemaphoreSlim` over two `ConcurrentQueue`s.
- Applies to every plain unguaranteed update, not only client-authoritative entities: datagrams already arrive
  on a different thread from the reliable stream, so this reordering exists today and is already tolerated.
  Consequences, all graceful: overtaking the sender's own create/subscribe → entity not found, dropped (no
  reply expected; make sure this logs at debug level only); overtaking a pending lock → applied, as if it
  arrived first; overtaking the sender's older guaranteed write to the same field → the older one is skipped
  by the same-writer seq check.

### 3. Send unguaranteed relays straight from the live thread

`ImpunityUnity/Assets/Scripts/Impunity/Server/Networking/ImpunityServer.cs`

- In `ServerSideNetworkConnectionProxy.SendMessageToClient`: if the push is unguaranteed and
  `ClientContext.SupportsUnguaranteed`, encode it there (reuse `ImpunityNetworkingUtil.WriteMessage` with a
  `[ThreadStatic]` scratch `ByteWriter`, since `EncodeWriter` belongs to the writer thread), cache the array in
  `push.EncodedMessage`, and if it fits `MaxUnguaranteedMessageBytes` call `SendUnguaranteedNow` directly.
- Everything else (no datagram path, oversized, guaranteed) goes to `PendingWrite` as today; the writer finds
  `EncodedMessage` already set, so encode-once still holds.
- Confirm during implementation that both transports' `SendUnguaranteedMessageAsync` are safe from several
  threads at once (UDP `SendUdpSessionData`; `WebRtcSession.Send` is already called from the tick thread and
  the writer), and that `Closed`/`CloseWhenFlushed` are read safely off the writer thread.

### 4. Docs

- `AGENTS.md` "Send path" and "Update sequencing" bullets, `docs/guides/Connections.md` §4, and
  `docs/designs/WebRtcTransport.md`: describe the fast lane, the direct send, and the reordering rules above.

## Tests and verification

- `IntegrationTests` (shared, `Category=Sequence`): a test-only live action that blocks on a gate, then queue
  several guaranteed actions and one unguaranteed update; release and assert the unguaranteed relay is
  observed first. Second case: fast-lane flood does not starve a queued lock request.
- `ServerSendBatchingTests`: unguaranteed push to a UDP client is sent without the writer thread running
  (no `StartScheduledWrites` needed); non-UDP listener of the same push still gets it reliably, same array.
- `./test.sh --filter "Category=Sequence|Category=Unguaranteed|Category=SendBatching|Category=WebRtc|Category=RelayLatency"`,
  then full `./test.sh`.
- Compare step 1 numbers before/after. If the "before" p99 under load is already negligible, stop after
  step 1 and keep only the measurement.
