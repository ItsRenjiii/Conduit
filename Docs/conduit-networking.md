# Networking, ownership and sync

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing anything that syncs a variable, transfers ownership, or sends a
> network event.**

`conduit-udonsharp.md` owns the language and behaviour rules, and it owns `VRCUrl` and the string-download
rate limits. This file owns everything about the instance: what travels, who is allowed to change it, when
it arrives, and who can make it happen.

> **Provenance, because most of this cannot be tested in the editor.** A bullet carrying a citation -
> an SDK file and line, a registry spelling, "compiled", "client log", or "VRChat creator docs" - is
> evidence someone actually gathered. **An UNCITED claim about what VRChat DOES is inherited and has NOT
> been verified here** - it is a hypothesis, not a measurement,
> so treat a surprising one as something to test rather than as a wall. (Uncited TECHNIQUE - pack these
> values, join on this delimiter, put the check here - is this project's own practice and makes no claim
> about the platform.) Nothing about sync, ownership or late joiners can be settled in play mode at all
> (`conduit-testing.md` explains why), which is why the line is drawn here rather than left implicit.
> **When you verify or disprove one in a real instance, replace it with what you observed and cite it.**
>
> **"client log" is the strongest tier here and it means a real multi-player instance.** Those bullets were
> read out of the VRChat client's own log - its per-message-type network telemetry, and the event traces of
> a world that logs its own callbacks - which is the only instrument that reaches this subject at all.
> `conduit-testing.md` says where the log is and what it carries. The client BINARY is not a source: it is
> IL2CPP with encrypted metadata and nothing is readable out of it.

## Choosing a sync mode

- **Pick the sync mode from what has to travel: arrays sync only under Manual, interpolation only under
  Continuous, and one behaviour cannot have both.** `[UdonSynced] int[]` under Continuous refuses with "is an
  array which is not supported by Continuous sync", and `[UdonSynced(UdonSyncMode.Linear)]` under Manual
  refuses with "cannot be used with Manual sync". State that needs both is split across two behaviours.
  Third-party references list the syncable types and the interpolation modes with no mention that the two
  are mutually exclusive. (SDK 3.10.5) **The budgets are why arrays land where they do:**
  Continuous carries roughly 200 bytes for the WHOLE behaviour and sends about 10 times a second on its
  own, while Manual carries roughly 280 KB per `RequestSerialization`. Neither figure is an SDK constant
  this project can read back, so treat them as sizing guidance and measure the real payload with
  `SerializationResult.byteCount` below. (VRChat creator docs)
- **The syncable types are a closed list the SDK holds literally, and it is worth reading rather than
  guessing.** `UdonNetworkTypes.cs` declares three `HashSet<Type>` sets and three predicates over them -
  `CanSync`, `CanSyncLinear`, `CanSyncSmooth` - so this is the authority, not a docs page.
  **Syncable (33 entries):** `bool`, `char`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`,
  `ulong`, `float`, `double`, `string`; an array of every one of those; `Color`, `Color32`, `Vector2`,
  `Vector3`, `Vector4`, `Quaternion` and their arrays; and `VRCUrl` plus `VRCUrl[]`. Nothing else - no
  `decimal`, no `DateTime`, no `GameObject`, no `Transform`, no `VRCPlayerApi`, no type of your own.
  (`UdonNetworkTypes.cs:13-54`, SDK 3.10.5)
- **And the interpolation sets are narrower than the sync set, which no summary mentions.** Being
  syncable does NOT mean `UdonSyncMode.Linear` or `.Smooth` applies:
  - **`Vector4` is syncable but supports NEITHER interpolation mode.** It is absent from both sets, so a
    `[UdonSynced(UdonSyncMode.Linear)] Vector4` is asking for something that does not exist - and a
    rotation carried as a `Vector4` rather than a `Quaternion` silently loses smoothing.
  - **`Color` and `Color32` support Linear but NOT Smooth.**
  - **`string`, `char`, `bool` and EVERY array type support neither** - which is the same boundary as
    "arrays are Manual only", seen from the other side.
  - Everything else numeric, plus `Vector2`, `Vector3` and `Quaternion`, supports both.
  (`UdonNetworkTypes.cs:56-88`, SDK 3.10.5)
- **A count that fits in a `byte` should not be synced as an `int`**, and eight flags belong in one
  bit-packed `int` rather than eight `bool` fields.
- **A whole `string[]` that changes as a UNIT is cheaper as one joined string than as a synced array** -
  one field, one header, one `OnDeserialization` - and `U+2029 PARAGRAPH SEPARATOR` is the delimiter to
  join on: invisible, absent from urls and display names, and not on any keyboard. Split it back on
  receipt. This is the same join-and-split shape that `PlayerData.GetKeys` forces in
  `conduit-udonsharp.md`, used deliberately here.
- **An uninitialised synced array stops the whole behaviour syncing.** `[UdonSynced] private int[] _x;`
  left null does not merely sync as empty - it takes the behaviour's serialization down with it, so
  UNRELATED fields on the same behaviour stop arriving too. Give every synced array an initialiser
  (`= new int[0]` is enough). The symptom is a behaviour where "some things sync and some do not", which
  sends you hunting the wrong field entirely.
- **`BehaviourSyncMode.None` silently kills `SendCustomNetworkEvent` - use `NoVariableSync` for an
  event-driven behaviour.** UdonSharp's own attribute source says of `None`: "Nothing is synced and
  SendCustomNetworkEvent will not work on the behaviour", and of `NoVariableSync`: "SendCustomNetworkEvent()
  will still work on this behaviour". Both enforce zero synced variables and both compile clean, so the wrong
  choice ships a feature that never fires and never explains itself.
  (`UdonSharpAttributes.cs`, SDK 3.10.5)
- **Two different enums each spell a member `None`, and the two mean opposite things.**
  `BehaviourSyncMode.None` on the CLASS means nothing syncs at all; `UdonSyncMode.None` on a FIELD means
  "synced with no interpolation", which is identical to a bare `[UdonSynced]`. Reading either as the other
  inverts the intent, and neither spelling complains. (`UdonSharpAttributes.cs`, SDK 3.10.5)
- **Fewer variables beats smaller variables - every synced field carries its own header.** Eight
  separate `byte` fields cost about 16 bytes (8 of data, 8 of per-variable overhead); the same eight
  values packed into one `ulong` cost about 9. So halving a field's TYPE saves less than removing the
  field does, and a behaviour with many small synced values is paying mostly for metadata. This is also
  exactly why a payload cannot be estimated by adding up type widths, and why `byteCount` is the only
  honest number. **Measured in live instances, a whole Udon serialization never came in under 7 bytes** -
  the client's `net_serialize_byte_count UdonSync` counter reported a minimum of 7 and a maximum of 17
  bytes per event across three sessions of ordinary public worlds. So the per-send floor is real and
  small, and the practical lesson is the one above: the count of fields dominates, and almost nothing
  real approaches a byte budget - **frequency is the constraint that actually bites, not size.**
  (Client log)
- **Pack several small values into ONE synced field when budget matters, and know the exact limit.** A
  `Vector3` carries three independent floats, so three small integers travel as one field - and a float's
  24-bit mantissa means every integer up to 16,777,216 survives the round trip EXACTLY, with
  `Mathf.RoundToInt` on unpack absorbing the noise. An `int` used as a bit field replaces up to 32 `bool`
  fields. Pack in `OnPreSerialization` and unpack in `OnDeserialization` so the packed form never leaks
  into game logic. Two costs to accept before packing: **`FieldChangeCallback` fires on the PACKED field**,
  so it can no longer tell you which logical value changed, and **a value that needs interpolation must
  not be packed** - the components interpolate independently and every intermediate frame is meaningless.
- **Shift a value's RANGE before packing it, not just its type.** A quantity that only ever sits between
  200 and 210 needs four bits once you store `value - 200`, and a signed -50 to +50 becomes an unsigned
  0 to 100 by adding 50 before packing. Keep the offset in a `const` beside the field, apply it on pack
  and remove it on unpack; it costs nothing at runtime and often saves a whole type width.
- **Interpolation is a RECEIVER-side setting and changes nothing about send rate.** `UdonSyncMode.Linear`
  and `.Smooth` only decide how a receiving client fills the gap between two updates; how often updates
  leave the owner is decided by the behaviour's `BehaviourSyncMode`. Reaching for `Linear` to make
  something "sync more smoothly" when the real problem is update frequency changes nothing.

## Ownership

- **`Networking.SetOwner` is locally immediate and arbitrates nothing.** `IsOwner(gameObject)` returns
  true synchronously on the calling client the moment the call returns, and `OnOwnershipTransferred` fires
  synchronously inside the `SetOwner` stack there - so a local read can never disagree with the call and
  can never tell you the transfer was accepted. When two clients call it in the same frame BOTH succeed
  locally and both write synced variables; VRChat settles the durable owner by network arrival order and
  the loser's write is silently overwritten when the winner's serialization lands. The tell is a value
  that "sometimes does not take" under contention, with no error anywhere.
  **Observed: a world's own trace logs its `SetOwner` call, then `OnOwnershipRequest`, then
  `OnOwnershipTransferred`, all stamped at the SAME frame time** - the two callbacks really do run inside
  the calling frame, so there is no window in which a local read could reflect a refusal.
  (VRChat creator docs + client log)
- **A `SetOwner` naming the CURRENT owner is discarded, and the client says so in its log:**
  `[NetworkProcessing] Ignoring TrySetOwner attempt on <object> because <player> already owner`. It is
  harmless, but a handler that calls `SetOwner` unconditionally every interaction fills the log with it
  and buries the lines you actually wanted - test `IsOwner` first. (Client log)
- **`OnOwnershipRequest` runs on both the requester and the current owner, and both must return the same
  answer.** It is not a server-side gate: each side evaluates it locally, so a decision made from state
  only one of them has - a local timer, an un-synced flag, anything in a private field - makes the two
  clients disagree about who owns the object. Gate it on SYNCED state only, and keep the rejecting window
  short, because a requester can still read the pre-write value until the owner's serialization arrives.
  It is also NOT invoked at all when the current owner has already disconnected; VRChat assigns directly.
  **Both halves are observed, not inherited.** In a live instance a world that logs every callback
  recorded `OnOwnershipRequest / requesting=<remote player>` ON THE CURRENT OWNER'S CLIENT - so the
  callback genuinely runs on a client that did not ask - followed by `OnOwnershipTransferred`. And in the
  same log, TWICE, an owner disconnecting produced `OnPlayerLeft` -> the client's own
  `[NetworkProcessing] Transferred ownership of <object>` -> `OnOwnershipTransferred`, with **zero
  `OnOwnershipRequest` in either window** from the same behaviour that demonstrably logs one whenever it
  fires. That is a controlled absence, not a gap in the logging. (SDK 3.10.5 registry + client log)
- **A `VRCPlayerObject` is already owned by its player - do not call `SetOwner` on one.** VRChat spawns
  one instance per player and assigns ownership automatically, so the whole slot-allocation, master-
  coordination and handoff apparatus a shared manager needs simply does not apply. Late-joiner
  restoration comes free with it too. Reach for a manager-held synced table only when there is genuine
  cross-player arbitration to do, such as a capacity cap.
- **Never compute a shared value independently on each client and assume they match.**
  `UnityEngine.Random` is not guaranteed to produce the same sequence on every client even from the same
  seed, and anything derived from `Time.time` is per-client by definition. So a "each client works out
  the same answer" design drifts silently - clients disagree with no error and no sync traffic to
  inspect. Compute ONCE on the owner and sync the RESULT, or derive it from something genuinely shared
  like `GetServerTimeInSeconds()` or an authored Inspector value.
- **Ownership is per GAMEOBJECT, not per component**, so every behaviour on one object shares one owner
  and one owner check. Two systems that need independent ownership need independent objects.
- **When an owner leaves, VRChat picks the new one and does not tell you the rule.** Do not assume the
  local player inherits. `OnOwnershipTransferred` fires with the new owner, synced variables are NOT
  reset, and the correct response from the inheriting client is to `RequestSerialization()` so everyone
  converges. Clean up a departing player's data in `OnPlayerLeft`, which is a different event with a
  different argument.
- **`isMaster` is not an authorization boundary.** VRChat documents instance master as a gameplay and
  session-arbitration role, not a security one: it moves when the master leaves, and nothing vouches for
  it. Gate a privileged action on the object OWNER, or on an owner-controlled synced role field.
  Master has two failure modes worth naming: during a handoff `IsMaster` reads FALSE on every client at
  once, so a timed action gated on it is simply dropped; and two clients mid-handoff can both read TRUE
  and act twice. Reserve it for idempotent session bookkeeping that does not care about either.
  **That handoff window is real enough that VRChat's OWN code trips over it:** immediately after a player
  leaves, the client log fills with `[NetworkProcessing] Master player was null for ownership transfer`
  and `[NetworkProcessing] Attempted to check ownership modification permissions for a null player`. If
  the client cannot resolve a master there, neither can your behaviour. (Client log)
- **An owner check inside a uGUI `OnClick` handler makes the button dead for everyone but the owner.**
  The click fires LOCALLY on whoever pressed it, so `if (!Networking.IsOwner(gameObject)) return;` at the
  top of the handler silently discards every non-owner press and the button reads as broken rather than as
  refused. Either take ownership in the handler when the press should act immediately, or hand the work to
  the owner with `SendCustomNetworkEvent(NetworkEventTarget.Owner, ...)` and check ownership THERE. The
  check belongs where the state is mutated, never where the input arrives.
- **And the same guard in an `Update` is the opposite call - there it is mandatory.** A condition that
  becomes true from the world's own state becomes true on EVERY client at once, so an unguarded
  `Update` has all of them call `SetOwner`, write the field and serialize together - which is the
  contention case above, self-inflicted. Gate the LOGIC on `Networking.IsOwner(gameObject)` and let every
  other client react in `OnDeserialization` instead. **The two rules look identical and point opposite
  ways because the INPUT differs:** a click already happened on exactly one client, while an `Update`
  condition fires on all of them, so the first must not be filtered and the second must.

## Serialization, and what a late joiner actually receives

- **`OnDeserialization` DOES fire in play mode, but its `DeserializationResult` carries fixed values, so
  anything derived from it is meaningless there.** ClientSim calls
  `OnDeserialization(new DeserializationResult(0, 0, true))`, and the struct's fields are `sendTime`,
  `receiveTime`, `isFromStorage`: a latency computed as `receiveTime - sendTime` is always zero, and
  `isFromStorage` is always TRUE, so a late-joiner branch keyed on it takes the storage path every time in
  the editor and can take the other path in VRChat. Third-party references say serialization is simply not
  simulated; it is delivered, and it lies, which is worse.
  (`ClientSimUdonEncodeDecode.cs:178`, SDK 3.10.5)
- **AND `DeserializationResult` carries no late-joiner flag in the first place**, so the editor's lying
  `isFromStorage` is not even the member you wanted. The three fields are the two timestamps and the
  storage origin; none of them answers "is this my first sync". **Track it yourself with a
  `_hasReceivedState` bool** set on the first `OnDeserialization`, and gate every side effect on it.
  Without that gate a late joiner receives the current value of every synced variable at once and replays
  the world's entire history in one frame - every sound, every particle burst, every animator trigger.
  Applying the visual state is always safe; it is the effects that must be skipped exactly once.
  (SDK 3.10.5)
- **`OnDeserialization` does not fire on the client that wrote the value.** You do not deserialize your
  own serialization, so an owner who writes a field, calls `RequestSerialization()`, and refreshes the
  display inside `OnDeserialization` updates everyone's screen EXCEPT their own. The symptom is "it works
  for everyone but me", which sends you hunting a sync bug that is not there. Either apply the change
  locally right after writing it, or drive the refresh from a `[FieldChangeCallback]`, whose setter runs
  on assignment and therefore fires locally AND remotely. **Observed with both a positive and a negative
  case from ONE behaviour:** while the local client owned the object it logged its owner-side write twice
  and `OnDeserialization` NEVER followed; `OnDeserialization` appeared only after ownership had moved to a
  remote player and that player serialized. (Client log)
- **On the first player's client `OnDeserialization` never fires at all - there is no prior state to
  receive.** So a behaviour that only draws itself from `OnDeserialization` renders NOTHING for whoever
  opens the instance, and is correct for everyone who joins afterwards. This is the single most misleading
  shape in VRChat testing, because testing alone exercises exactly the path that has no deserialization in
  it. Render defaults in `Start()`, and let `OnDeserialization` take over from there. **Observed across
  two separate instance joins** where the local player was first in: the behaviour logged its start, its
  own join and its owner-side write, and produced NO `OnDeserialization` until a second player had taken
  ownership and serialized. (Client log)
- **And a `[FieldChangeCallback]` setter can run before `Start()` has returned**, when the owner
  serializes at about the moment a late joiner arrives. The setter then touches references `Start()` has
  not assigned yet. **That is why the guard is TWO flags, not one:** `_isInitialized`, set at the END of
  `Start()`, suppresses setter side effects until the behaviour is actually built; `_hasReceivedState`,
  set on the first `OnDeserialization`, runs one-time setup exactly once. They answer different questions
  and neither substitutes for the other.
- **The join sequence is fixed, and you are always LAST.** A late joiner gets `OnPlayerJoined` for every
  player already present, in join order, with their own event at the end - then `OnDeserialization`.
  **Synced values are NOT populated when `Start()` runs**, so reading one there yields the type default
  for a late joiner and the authored value for the instance creator, which is how a bug appears only for
  the second person in. Read synced state in `OnDeserialization`, never in `Start()`.
- **A network event can overtake the serialization it was paired with.** Writing a synced field,
  calling `RequestSerialization()`, and then sending an event to announce it does NOT guarantee the
  receiver sees the new value when the event runs - the two travel independently and the event frequently
  wins. The receiver reads the OLD value, and it does so intermittently, which is the worst possible
  shape to debug. **Do not pair them: put the reaction on the DATA.** `[UdonSynced,
  FieldChangeCallback(nameof(Prop))]` runs the property setter when the value actually lands, locally and
  remotely, which is ordered by construction and needs no second message.
  **The mechanism is visible in the client's own accounting: `udon_network_call` and `udon_serialization`
  are separate message types with separate byte and event counters.** They are not two things in one
  stream that could be ordered - they are different traffic, so nothing sequences one against the other
  and "it arrived in the order I sent it" is never a property you have. (Client log)
- **A `[FieldChangeCallback]` does not fire when you write the backing field - only the property.** The
  attribute redirects the DESERIALIZED write into the setter; a local `_myValue = 10;` bypasses it
  completely, so every remote client reacts and the writer does not. Assign through the property
  everywhere, including inside the behaviour that owns the field - half-wiring it this way presents as
  "it works for them and not for me", which is the same symptom as the owner-side rule above and a
  different cause.
- **Choose between `OnDeserialization` and `FieldChangeCallback` on ONE question: do you need to know
  WHICH value changed?** `OnDeserialization` fires once per received packet and cannot tell you - it is
  the right answer for one or two fields where refreshing everything is cheap. `FieldChangeCallback` fires
  per field with the new value already in place, which is what three or more independently-reacting values
  need, and it costs a property per field. Mixing both on one behaviour is fine and common: the callbacks
  drive the per-field reactions, `OnDeserialization` does the one-time late-joiner work.
- **Delta sync serves nobody who was not already there.** Syncing "the last move" rather than the whole
  board is the right call for bandwidth and the wrong call on its own: a late joiner receives the latest
  delta with nothing to apply it to, and reconstructs a world that never existed. A world using deltas
  keeps the FULL state synced as well, and the delta is an optimisation on top of it, never a replacement.
- **Synced variables replay to late joiners; network events NEVER do.** A joiner is handed the current
  value of every synced field and gets `OnDeserialization`; every `SendCustomNetworkEvent` fired before
  they arrived is simply gone. So anything a late joiner must know is STATE in a synced field, and an
  event is only ever the announcement of a change that state already records. A world whose phase lives
  only in an event is a world that is broken for everyone who was not there at the start.
- **Measure a behaviour's synced payload instead of estimating it: `OnPostSerialization` hands you a
  `SerializationResult` carrying `success` and `byteCount`.** That is the only honest number for "how big is
  this sync", and it beats adding up field widths, because per-variable header overhead is real and is not
  in any type-size table. `Networking.IsClogged` is exposed too
  (`VRCSDKBaseNetworking.__get_IsClogged__SystemBoolean`), so a behaviour can defer its own
  `RequestSerialization` rather than adding to a queue that is already backed up. Neither can be exercised
  in play mode. (SDK 3.10.5)
- **Sync a start time and compute the rest locally, rather than syncing a running value.**
  `Networking.GetServerTimeInSeconds()` returns a `double` and `GetServerTimeInMilliseconds()` an `int`,
  both agreed across clients, so one synced `double` start stamp replaces a timer that would otherwise
  serialize every frame. The same reduction applies to anything derivable: sync the seed and the start,
  never the sequence. (SDK 3.10.5 registry)
- **A non-owner write to a synced field is purely local, and `RequestSerialization()` from a non-owner
  is a no-op.** Nothing errors: the field visibly changes on that client, the call returns normally, and
  the value is silently replaced the moment the real owner next serializes. This is the commonest Udon
  networking bug there is, and it presents as "it works for me but not for them", or as a value that
  reverts a second after it changed. Guard every synced write with `Networking.IsOwner(gameObject)` and
  take ownership first where taking it is legitimate.
- **`VRCObjectSync` going to sleep is reported to freeze `[UdonSynced]` variables on behaviours sharing
  that GameObject too**, so a `RequestSerialization` on a resting pickup
  silently never transmits. Not verified here - it needs two live clients. The remedy it proposes is the
  one this project already follows for other reasons: sync logic lives on its own object, not on the
  physics object.
- **Teleporting a `VRCObjectSync` object without `FlagDiscontinuity()` makes it slide there on every
  other client.** Sync interpolates between the positions it receives, so a jump reads as very fast
  travel; `FlagDiscontinuity()` tells it this gap is not motion. Call it on the same frame you move the
  transform, and prefer `TeleportTo(Transform)`, which is the built-in that does both.
- **`AllowCollisionOwnershipTransfer` on a `VRCObjectSync` hands ownership over on a physics collision,
  with nothing in your code asking for it** - so an object changes hands by itself, and this is the first
  thing to check before debugging a mystery owner change. `conduit-udonsharp.md` carries that component's
  full Udon surface. (SDK 3.10.5)
- **And the SDK hard-refuses the two configurations that reach it: `VRCObjectSync` may not share a
  GameObject with a manually synced UdonBehaviour, nor with a `VRCObjectPool`.** Both are RED
  `OnGUIError`s that block Build & Test and Build & Upload, each offering only a Select button and no
  fix. The first is SDK-side corroboration for the unverified claim above - VRChat treats ObjectSync plus
  manual sync on one object as incoherent rather than merely unwise. The check is per GAMEOBJECT, so a
  child object is the whole fix: transform and physics on the ObjectSync object, your synced state on its
  own. (`VRCSdkControlPanelWorldBuilder.cs:868-876`, SDK 3.10.5)

## What fails silently, and how to tell which one you hit

Every overrun here is quiet, and the three fail in DIFFERENT shapes - so the symptom identifies the cause.

- **High-frequency Udon sync is sent unreliably and is dropped routinely - this is normal operation,
  not an overrun.** The client splits Udon sync into TWO message types and counts them separately:
  `udon_serialization` and `udon_unreliable_serialization`. **Only the unreliable one carries a
  `net_event_dropped` counter, and in the field that counter is far from zero** - 584 and 333 dropped
  serializations in two ordinary sessions, peaking at 122 in a single sampling interval. In the same
  window the unreliable channel carried 1,328 events to the reliable channel's 7, which is what marks it
  as the continuous, every-few-frames path. **So a Continuous-synced variable is a LATEST-VALUE channel,
  never a stream of every value it held.** Anything that counts, accumulates, toggles, or steps a state
  machine on each arrival is built on sand - a dropped update is invisible and unrecoverable, and the
  world simply ends up in a different state for different players. Sync the resulting state, not the
  increment. There is no drop counter for `udon_network_call`, so this is specific to sync traffic.
  (Client log)

- **An over-budget synced string is truncated, not refused.** Writing stops at the byte limit and the
  receiver gets a valid, shorter, wrong string, with no error on either side. UTF-16 means 2 bytes per
  character, so a Continuous behaviour's ~200 bytes is about 100 characters BEFORE any other synced field
  takes its share. Cap the length at the assignment, and move anything genuinely long to Manual.
- **An over-budget packet is dropped whole.** Manual sync never partially delivers: exceed the
  serialization limit and the entire payload is discarded, so every receiver silently keeps the values it
  already had. The tell is the opposite of truncation - not corrupt data but STALE data - and since
  nothing reports it, a version or counter field receivers can compare is the only cheap way to notice.
- **An over-frequent `RequestSerialization` costs the whole instance, not just you.** The Udon budget is
  roughly 11 KB/s shared, so one behaviour serializing per frame starves every other system in the world.
  Throttle to what the data needs - 10 Hz is generous for position, 1-2 Hz covers most game state - skip
  the call entirely when the value has not meaningfully changed, and check `Networking.IsClogged` before
  serializing, backing off with `SendCustomEventDelayedSeconds` rather than retrying inside `Update`.
- **`RequestSerialization()` under Continuous is redundant, and an interpolation attribute under Manual
  does nothing.** Neither combination warns, and both read as deliberate to the next person. A behaviour
  that appears to want both is two behaviours.

## Network events

- **`NetworkEventTarget` has exactly four members: `All` (0), `Owner` (1), `Others` (2), `Self` (3)**, all
  present in this SDK. `Others` is the one that stops a sender double-playing its own effect, and `All`
  DOES include the sender, which is the usual cause of a doubled sound. (SDK 3.10.5)
- **`NetworkEventTarget.Owner` means the owner of the sending behaviour's own GameObject - not the owner
  of whatever object the handler is about.** Delegating work to "the owner of that pool / that door / that
  manager" only reaches the intended client when the SENDER sits on that same GameObject. Put the
  forwarding behaviour on the object whose owner should act, or take ownership locally instead. Nothing
  reports the mismatch - the event is delivered, to the wrong client, and the handler's own `IsOwner`
  guard then quietly refuses it, so the feature simply does nothing for most players.
  (VRChat creator docs)
- **A behaviour can read its OWN outgoing send queue.**
  `NetworkCalling.GetQueuedEvents((IUdonEventReceiver)this, nameof(_Method))` returns the depth for one
  event and `GetAllQueuedEvents()` the total for this client, so a sender can decline to add to a backlog
  instead of discovering it as latency. Both count THIS client's outgoing sends - neither says anything
  about work arriving at receivers. (SDK 3.10.5)
- **`Networking.SimulateNetworkLatency` is NOT callable from Udon** - absent from the registry entirely,
  so a world cannot test itself from the inside. (SDK 3.10.5)
- **A network event naming a method the target does not have is not silent - the client logs it, and
  only the client does.** The message is `Unable to send network event '<name>' as it does not exist on
  the target Udon behaviour '<object>'`, on the SENDER's side, naming both the event and the GameObject.
  It is the fastest way to catch a renamed handler, a typo, or an event aimed at the wrong behaviour on a
  shared object - and because it lands in the client log rather than the Unity console
  (`conduit-testing.md`), a world can emit hundreds of them while the editor looks perfectly clean.
  **Check the client log for this line before theorising about why a remote call "did nothing".**
  (Client log)
- **A legacy network event fires on every UdonBehaviour on the target GameObject, not only the one you
  addressed.** An unparameterised `SendCustomNetworkEvent` with no `[NetworkCallable]` behind it is
  dispatched by walking `behaviour.gameObject.GetComponents<UdonBehaviour>()` and running the named event
  on each one whose `SyncMethod` is not `None`, so two behaviours sharing an object and a method name both
  fire and the unintended one is invisible at the call site. That `SyncType.None` skip is also the
  mechanism behind the `BehaviourSyncMode.None` rule above. An attributed event does NOT fan out - it
  resolves through the behaviour's own metadata. Read from ClientSim's dispatcher, whose own comment says
  it mirrors the client's checks; ClientSim has lied before (see `OnDeserialization` above), so treat the
  fan-out as the reason behaviours belong on their own objects rather than as a mechanism to build on.
  (`ClientSimNetworkCalling.cs:63-70`, SDK 3.10.5)
- **`[NetworkCallable]` accepts only the simplest possible declaration, and the compiler enforces THIRTEEN
  separate refusals.** It must be `public`, take at most 8
  parameters, and have NO return type at all. It may not be generic, extern, an operator, virtual,
  abstract, an override, vararg, async, static, sealed, or an explicit interface implementation, and no
  parameter may be `ref`/`out`, `params`, or carry a default value. Overloading is refused outright -
  including against a NON-callable method that merely shares the name - and marking a built-in Udon event
  is its own separate error. Each failure names the method and the exact reason, so read the message
  instead of bisecting the signature. (`CompilationContext.cs:490`, SDK 3.10.5)
- **A parameter may only be a SYNCABLE type** - the list above - so `GameObject`, `Transform`,
  `VRCPlayerApi` and any class of your own cannot cross. Send the `playerId` and let each receiver resolve
  it locally, and treat that id as ROUTING, never as identity: it says which client should act, and it
  proves nothing about who sent it.
- **NEVER re-verify that `VRC.SDK3.UdonNetworkCalling` exists.** `NetworkCalling.InNetworkCall` and
  `NetworkCalling.CallingPlayer` compile in this project (SDK 3.10.4), and a security API is not
  rolled back. Design with them rather than spending a session confirming them.

## Every unprefixed entry point is public to the instance

- **THE `_` prefix on a public method is a security boundary, not a naming style.** VRChat refuses to
  deliver `SendCustomNetworkEvent` to a method whose name begins with `_`, and that refusal is the ONLY
  thing standing between your behaviour and a remote caller. **Every public method WITHOUT the prefix is an
  entry point any client in the instance can fire, at any time, in any order, as often as it likes** - and
  a hacked client doing exactly that is in circulation NOW. Prefix every public method unless it is
  deliberately a network endpoint. `conduit-code-style.md` states the naming rule; this is why it is not
  cosmetic. **AND `[NetworkCallable]` re-opens it - an underscore method carrying the attribute is
  remotely callable.** The underscore test runs only where the behaviour has no network-calling metadata
  for that name, so the attribute never reaches it; UdonSharp says so in its own remarks on every
  `SendCustomNetworkEvent` overload. The prefix is a default, not a lock, and an audit that greps for
  unprefixed methods alone misses every attributed one.
  (`UdonSharpBehaviour.cs:103`, `ClientSimNetworkCalling.cs:47-56`, SDK 3.10.5)
- **`[NetworkCallable(N)]` is not a defence - it paces one sender, not the receiver.** The attribute
  carries `MaxEventsPerSecond` (default 5 per event, max 100), and VRChat's own documentation states that a
  rate-limited event "will not prevent other queues from draining", so other senders skip the queue. N
  callers therefore deliver up to N times the limit to one method. The receiver's own bounds - a fixed-size
  array, an idempotent write, a state check - are the only thing that caps the work, and they are what the
  rule below is about. Local calls and `NetworkEventTarget.Self` bypass the pacing entirely.
  (SDK 3.10.5, VRChat creator docs)
- **A method that MUST receive network events is untrusted input.** It cannot carry the prefix, so it is
  reachable by anyone and must be written for that: validate the state it depends on INSIDE the method,
  keep it idempotent and cheap enough to survive being spammed, and never let it transfer ownership, spawn,
  teleport, mutate synced state or start audio purely because it was called. "Only our own button calls
  this" is not true of any unprefixed method.
- **An owner check is not an authorization boundary - a caller takes ownership first and then passes it.**
  `SetOwner` arbitrates nothing and succeeds locally for anybody, so
  `CallingPlayer.playerId == Networking.GetOwner(gameObject).playerId` is beaten by two calls in a row: take
  ownership, then call the endpoint. **It is a COHERENCE check, not a permission one** - it stops a stale or
  confused client writing, and it reads exactly like security, which is what makes it dangerous. Where the
  wrong writer is a real failure, authorize the CALLER against an identity list you control and treat
  ownership as a delivery detail.
- **Let a direct local call through when the method is ALSO called locally, and refuse one when it is not.**
  `SendCustomNetworkEvent(All, …)` also runs on the sender's own client, so a blanket "reject unless
  `InNetworkCall`" swallows the sender's OWN event and the feature dies quietly. A method that is ONLY ever a
  network endpoint should do the opposite and refuse a local call outright, which turns a wiring mistake into
  an error rather than a silent success. Decide which of the two it is and say so in its summary.
- **Owner-side deduplication state must be synced, or an ownership handoff erases it.** A "one vote per
  player" guard, a claimed-slot list, a per-player cooldown - anything the owner keeps to decide whether a
  request was already honoured - lives in a LOCAL field by default, and a local field is empty on whoever
  becomes owner next. The bypass is trivial and needs no hacked client: act, wait for the owner to leave,
  act again. Put the accepted-caller list in `[UdonSynced]` state alongside the value it protects, so it
  travels with the authority it belongs to. A fixed-size `int[]` of player ids doubles as the capacity
  bound the receiver needs anyway.
- **`InNetworkCall` is reported to stay TRUE for the whole call tree, not just the entry method** - set
  through nested and cross-behaviour calls until the network entry method returns. Not verified here, and
  it matters: a shared helper cannot use `InNetworkCall` to tell "a
  remote client asked for this" from "our own code asked for this" once anything above it was network
  entered. Decide trust at the entry point and pass it down, rather than re-asking deeper in.

## Carrying state that has to know WHO wrote it

Everything above assumes the value is what matters. When the WRITER matters too - a whitelist, a moderation
list, an admin panel, any state where the wrong author is the failure rather than the wrong number - the
architecture changes shape. The rules below were read out of a shipped implementation of this pattern
rather than derived here, including the hole at the end that it could not close.

- **`[UdonSynced]` cannot tell you who wrote the value, so authorized state travels by network event.**
  `OnDeserialization` hands you the data and NOTHING about its origin - there is no sender on it, and the
  object's owner is not evidence because ownership is takeable. Only a network call carries identity, in
  `NetworkCalling.CallingPlayer`. So state that must be authorized is sent as a `[NetworkCallable]` event,
  structured payloads serialised to a string, while ordinary state keeps the synced-variable path. **The
  price is late-joiner replay**, which synced variables give free and events never do, and the next two
  bullets are what paying it costs.
- **Address ONE player by broadcasting and letting everyone else drop it.** `SendCustomNetworkEvent` targets
  only `All`, `Owner`, `Others` and `Self` - there is no "this specific player" - so carry a `receiverId`
  parameter, use a sentinel such as `-1` for everyone, and have each receiver return immediately when the id
  is neither the sentinel nor its own `playerId`. Send the current state that way from `OnPlayerJoined` to
  replace the replay you gave up. It costs every client the message to deliver it to one, so keep such
  payloads small and infrequent.
- **A joiner can receive authorized state before it is able to judge the sender, and the answer is a
  bounded retry.** The list that decides who is allowed is itself synced, so a client can hold the data and
  not yet hold the means to validate it - and rejecting outright loses legitimate state on every join.
  Keep the LATEST payload, re-attempt validation on a delay a fixed number of times, and **discard a pending
  payload the moment a newer one arrives** so the newest state always wins. After the last attempt, DROP the
  data rather than applying it unvalidated: the failure mode has to be losing an update, never accepting an
  unauthorized one.
- **Re-validate the OWNER when ownership moves, and move it again if they are not permitted.** Ownership
  lands on whoever grabbed it or on whoever VRChat picked after a disconnect, so the client that ends up
  sending initial state to future joiners may be one that is not allowed to write at all. Have the master
  check the new owner on `OnOwnershipTransferred` and hand ownership to a permitted player when it is not.
- **Gate a send on the QUEUE rather than on a timer.** `NetworkCalling.GetQueuedEvents(this, nameof(Method))`
  returns this client's outstanding sends for that event, so deferring while it is non-zero paces the sender
  against real backlog instead of a guessed interval, and pairs with `[NetworkCallable(N)]` declaring the
  queue depth the method expects.
- **And the model has a hole nobody has closed - do not build as though it does not.** The recovery above
  is driven by the master, and master is not a trust anchor: a client can take ownership, become master, and
  then drive the master-only path itself. The obvious fix - having the departing owner hand off cleanly -
  **is not available, because a client cannot send a network event as it leaves the instance.** So a
  determined attacker can still force a reset or strand ownership. Treat this class of system as raising the
  cost of interference, never as access control, and keep anything that genuinely must not be forged off the
  instance entirely.
- **Neither key for an identity list is clean: a display name is user-changeable, and a `playerId` is only
  unique within the instance** and is reassigned across sessions. A name list survives a rejoin and breaks
  when somebody renames; an id list is stable for the session and meaningless the moment it ends. Pick per
  lifetime - ids for in-session bookkeeping, names for anything that must persist - and never treat either
  as proof of who somebody is.

## Network IDs, and the utility nobody runs

- **Synced scene objects carry network IDs, those IDs are scene data, and a hierarchy edit can break
  them.** `VRCSceneDescriptor` holds a `NetworkIDCollection` and a `NetworkIDLookup`, so they travel with
  the scene exactly like the collision matrix does - and a duplicated scene, a large reparent or a merged
  branch can leave them duplicated or missing. The client's own words for the failure are
  `Failed to assign network IDs, <n> errors encountered!` and `Ran out of Network IDs!`
  (`ClientSimNetworkingUtilities.cs:158, 299`, SDK 3.10.5).
- **The repair tool is `VRChat SDK > Utilities > Network ID Import and Export Utility`, whose WINDOW is
  titled "Network ID Utility"** - so a reference telling you to open "Network ID Utility" is naming the
  window rather than the menu item, and you will not find it by that name. Run it after any large
  hierarchy change to a world using `VRCObjectSync` or pools, and before the upload that follows one.
  (`VRCNetworkIDUtility.cs:15, 138, 162`, SDK 3.10.5)

## Instantiation and object pools

- **WRITE `Instantiate(template)` or `Object.Instantiate(template)`. The old `VRCInstantiate(...)` HELPER
  is obsolete** - `UdonSharpBehaviour` marks it
  `[Obsolete("This method is obsolete, use Object.Instantiate(gameObject) instead")]` and its body just
  forwards to `Instantiate`. Old worlds and old references are full of the helper spelling; it still
  works, and new code should not use it. (`UdonSharpBehaviour.cs:337`, SDK 3.10.5)
- **Underneath, there is exactly ONE instantiation node and it is not going anywhere.** A search of all
  35,222 definitions returns only `VRCInstantiate.__Instantiate__UnityEngineGameObject__UnityEngineGameObject`
  - `UnityEngine.Object.Instantiate` has no node of its own, so the C# call you write compiles down to
  that one extern, whose wrapper module is live and unmarked (`ExternVRCInstantiate.cs`). **Do not read
  "the VRCInstantiate helper is obsolete" as "instantiation is going away"** - the deprecated thing is the
  method name on the behaviour, not the mechanism.
- **The template must be an object in the scene, not a project prefab asset**, because Udon cannot load an
  asset at runtime. Keep it disabled and clone from it.
- **What it produces is LOCAL, and nothing makes it synced.** A runtime-instantiated object has no network
  identity, so anything that must exist for everybody is PRE-PLACED in the scene and pooled - enable and
  reposition a dormant object rather than creating one. Instantiation is for purely local, cosmetic,
  per-client objects.
- **`VRCObjectPool` is the synced answer: it syncs each held object's active state, late joiners
  included.** `TryToSpawn()` returns a `GameObject` (or null when the pool is empty), `Return(obj)` puts
  one back, and `Shuffle()` randomises the available order - all three confirmed in the registry, with
  `Pool` readable and writable as a `GameObject[]`. `Shuffle` is real despite being absent from the public
  API docs. (SDK 3.10.5)
- **All three pool methods are owner-only and silently no-op for everyone else.** A non-owner calling
  `TryToSpawn()` gets no exception and no warning - the call simply does nothing, which in an `Interact()`
  handler means the pool works for whoever happens to own it and is dead for every other player. Forward
  to the owner (from a behaviour ON the pool's own GameObject - see the `NetworkEventTarget.Owner` rule
  above) or take ownership of the pool first.
- **AND `TryToSpawn()` does not give the spawned object to anybody.** The object activates still owned
  by the pool's owner, so a per-player spawn needs an explicit
  `Networking.SetOwner(player, spawned)` afterwards. Do not read assignment back off the spawned object
  either: its ownership can change before `OnPlayerLeft`, so the manager keeps its own synced table of
  which player holds which pool index and treats THAT as the truth.
- **Pooled objects react through `OnEnable` / `OnDisable`, not `OnSpawn`** - `OnSpawn` is deprecated. Note
  the ordering: when the pool activates an object `OnEnable` runs BEFORE `Start()`, so anything `OnEnable`
  touches must not depend on `Start()` having run.
