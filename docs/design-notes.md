# Design notes

The reasoning behind the decisions in this sample, and the findings that cost real time to
discover. Read this when you want to know *why* something is shaped the way it is, or when
you are making the same decisions in your own game.

[Platform abstraction](platform-abstraction.md) describes the boundary itself.
[Troubleshooting](troubleshooting.md) covers concrete failures and their fixes.

## Platform boundary

### Keep native detail on one side of a stable contract

`NetRumble.Platform` contains interfaces, records and enums with no dependencies of its
own. No `IntPtr`, `HRESULT`, `XUserHandle`, `XAsyncBlock` or SDK structure appears in any
signature. Native errors become a `PlatformResult` carrying a `PlatformStatus`.

The payoff was measurable. A hand-written P/Invoke provider was replaced by a managed GDK
projection without a single change in `NetRumble.Core` or `NetRumble.Game`.

The transferable rule: native lifetime, error translation and ABI detail belong on one side
of a contract, and the game belongs on the other.

### Enforce your threading model at the boundary

Every task a provider hands out and every event it raises resolves inside
`IPlatformRuntime.Pump()`, which runs once per frame.

The original provider bought that guarantee at the task queue by using manual dispatch.
XBOX GDK.NET completes on the thread pool, so the guarantee moved up to `PumpDispatcher`
at the provider boundary instead. The rule survived a change of SDK because it was stated
as a contract rather than as an implementation detail.

The result is that gameplay, UI and netcode contain no locks and no dispatcher.

### An absent platform is a supported configuration

A machine with no GDK installed is how the front end and the practice match are developed,
so the provider reports availability and capabilities explicitly rather than pretending to
succeed. Multiplayer is gated with a reason the player can read. Achievements, saves and
presence degrade to working no-ops.

A capability flag means the whole path works end to end. The provider does not advertise
`Invites` or `Presence` merely because an entry point binds, because a flag that means
"this export resolved" is worse than no flag.

### Compose orthogonal services as layers

PlayFab cloud save and lobby discovery are independent of both the transport and the
platform runtime, so they compose over whichever provider is active. That is what makes
`--platform=lan --playfab-title=<id>` work without an enum value for every combination of
transport and cloud tier.

## Connectivity and lifecycle

### Connectivity describes the device rather than the account

The GDK sources a connectivity hint from the runtime's networking surface, and it is a
property of the machine rather than of the signed-in user. It therefore lives on
`IPlatformRuntime` as `IsConnectivityKnown`, `IsOnline`, `OfflineReason` and
`ConnectivityChanged`, rather than in a service of its own.

The LAN provider deliberately does not relay that hint, because "can this machine reach the
internet" and "can this machine reach a peer on the LAN" are different questions.

### Connectivity fails open

`IsOnline` reports offline only on an authoritative condition, such as an uninitialized
network stack or an explicit "no connectivity" answer. Unknown, local-access-only and
constrained states stay optimistic.

The asymmetry is deliberate. A false offline result locks a player with a working
connection out of the game. A false online result costs one connection-error dialog the
game already has to show anyway.

### One gate and one grace period

The proactive check folds into a single `MultiplayerDenialAsync` call on the main menu, so
Host, Join and anything added later are covered without a new check per menu row.

The reactive half gives a reported connectivity loss an eight-second grace period and
re-checks live before it ends an online match, so a brief network flap does not kill a
match the transport would have ridden out. A sustained loss ends the match at exactly eight
seconds. A four-second flap does not.

### Suspend runs inline and resume goes through the pump

A suspend callback cannot queue work for a later frame, because the process is frozen
immediately afterwards and there may be no later frame. The suspend path therefore does
cheap straight-line work only: commit local state, drop the session, stop audio.

Resume has no deadline, so it can return through the pump like everything else.

### Constraining freezes offline simulation only

A constrained networked host that stops simulating stops every other player in the match.
`ConstrainFreezesSimulation()` therefore returns whether the match is offline. Audio mutes
either way, but match state, roster and simulation keep running for a networked match.

### Account change and user change are different events

Signing out, signing back in, a privilege change and a profile change all need different
reactions, so `AccountChanged` carries the kind of change while `UserChanged` keeps its
original meaning.

Platform sign-out is raised inline rather than through the pump, because the title may be
terminated before another frame arrives.

### Controller loss uses an overlay rather than a dialog

A dialog needs input from the controller that has just been disconnected. The overlay
takes no input, holds no focus, does not pause the game and sits above the screen stack
until the controller returns.

Detection combines the platform's own device-association signal with a per-frame `GamePad`
poll, and reports transitions only. It is seeded silently at startup so a keyboard-only
desktop never prompts.

## Network trust boundary

Reviewing the whole trust boundary started from the assumption that malformed
replication messages could crash the simulation. That premise did not survive reading the
code, and the real problem was one layer further down.

### The codec layer was already hardened

`MessageReader` length-checks every read, cross-checks declared counts against the bytes
actually remaining, rejects NaN and infinity, validates enums through `Enum.IsDefined`,
resolves every wire id with `TryGetValue`, gates every handler on authority, and attributes
ship input to the transport's sender id rather than to anything in the payload.

### The hole was in peer attribution

The transports decided which peer id a datagram carried, which is the fact the entire
authority model rests on, and no amount of codec validation can check it.

- The Party path accepted a five-byte "welcome" control packet from **any** endpoint in the
  session and treated the sender as host. Any player could take authority over another,
  which is enough to teleport ships, rewrite scores or end the match.
- The LAN transport identified peers by source address alone, with no handshake secret.

Both are fixed. The Party path now requires the sender to be the actual host entity,
requires a join to be in flight, refuses to remap an endpoint already assigned to another
peer, and validates the claimed id. The LAN transport issues an eight-byte per-link session
token during the handshake and requires it on every link-scoped datagram.

The transferable lesson: **audit identity attribution before you trust message
validation.** Validating a message perfectly proves nothing if you cannot prove who sent
it.

The control-packet magic value provides framing. It never provided identity, and treating a
public constant as a secret is the mistake to look for.

### What the LAN session token does not do

The session token raises the cost of impersonation from "know two public constants" to
"read this link's traffic". It is not encryption and it stops no on-path observer. See
[Known gaps](known-gaps.md#networking).

### Bounds belong next to the parsing

Alongside the identity fixes, the review added a networked projectile ceiling, a world-size
sanity range, an inbox cap of 4096 packets and a 256-slot bound on reliable reordering.
These are resource limits rather than correctness fixes, and they belong next to the code
that parses untrusted input.

## Protocol versioning

### Refuse before you connect

`NRProtocol.VersionString()` is published in the PlayFab lobby row and carried in the
invite envelope. A mismatched version, **or an absent one**, is refused before a transport
endpoint is created. Treating a missing version as a mismatch means an old client cannot
join by simply omitting the field.

### Pass the version down rather than reading a static

`HostAsync`, `JoinAsync` and `JoinByConnectionStringAsync` all take `protocolVersion`
explicitly. Reading it from a static in each layer lets two layers disagree about which
version the process is running, which is the failure mode that makes version checks
useless.

## Saves and achievements

### One container with one blob per key

Connected storage uses a single synchronized container holding one blob each for settings,
achievement stats and match history. Splitting them across containers would let the
platform resolve each key from a different device independently, producing a state no
device ever had.

A missing blob returns an empty payload rather than an error, because an absent-blob first
run is the normal case. A title-specific size cap catches caller bugs before the service
does.

### Merge achievements monotonically

Statistics merge by taking the maximum of each counter and the union of each bitmask, never
last-writer-wins. Offline progress on one device cannot erase offline progress on another.

### Do not write anonymous state into an account-scoped store

Once a user is removed, the achievement tracker has no owner and refuses to persist until a
user is loaded or synchronized again. Without that refusal, the zeroed state left behind
after sign-out overwrites the departed player's saved progress.

### Derive enum-backed masks from the enum

An achievement mask that was written as a literal when the game had four weapons would have
completed at four weapons after the arsenal grew to twenty. Deriving the mask from
`Enum.GetValues<T>().Length` makes that class of stale-bound bug impossible.

## Choosing an API

### Prefer the shape you can marshal correctly

The batch privacy API returns nested variable-stride unmanaged arrays. The single-target
call returns a fixed-size result. This sample uses the single-target call and accepts the
extra round trips, because a marshalling bug in a privacy check is worse than latency in a
roster refresh.

Revisit that trade only if per-refresh latency actually matters.

### Reading a binary checks a signature and cannot derive one

`PartyInitialize` was originally declared as taking a title-id string, because its x64
prologue shows a pointer arriving in `rcx`. It takes a pointer to a configuration structure
whose first field is the title id, which is indistinguishable in a prologue and an access
violation at runtime.

Seven further declaration bugs were found the same way: reversed argument order, wrong
option-flag values, a flag that does not exist, shifted enum values, an asynchronous
function that is actually synchronous, a missing option value, a wrong return type, and a
gamertag buffer sized for 25 bytes when the modern component needs 97.

This class of bug is the strongest argument for using a maintained managed projection
instead of hand-written interop. None of those signatures is this repository's to get wrong
any more.

### Do not conclude a native SDK is usable from its export list

The native PlayFab DLLs export the authentication functions you would want to call. They
export no `XAsync*` or `XTaskQueue*` symbols, so managed code cannot complete the async
operations those functions return. The sample speaks PlayFab's documented HTTPS JSON API
instead, which needs no native dependency at all.

## Testing lessons

### A passing test that proved nothing

A test that forged a UDP datagram to prove the LAN session token was enforced passed even
when the token check was disabled. The attacker socket used a different ephemeral source
port, so the datagram was rejected for having no link at all, before token validation was
ever reached.

**Disable the defence and confirm the test fails.** A security test that has never failed
has never been shown to test anything. The fix was to rename the test to describe what it
actually proves, add direct checks on the framing layer, and pin the maximum fragment
payload against the token prefix that now precedes it.

### Test the consumer as well as the data

Every one of 32 pickup rows existed, and all 32 appeared in 200,000 weighted rolls, so the
library tests passed. The game could still spawn only three of them, because the world held
bodies for three pickup types and silently returned when a roll produced any other. A
weighted-table test cannot prove the consumer can instantiate every row.

### Drive the real game rather than a stand-in

The first two-process automated run reported a successful networked match. The match never
left `PlayersJoining`, no input was ever applied and no replication occurred. A harness
calling the transport and the world directly would have proved the netcode again and the
game not at all.

After the wiring defects were fixed, the same run showed each endpoint observing motion
simulated by the other: the host saw the client's ship travel 837 units while the client
saw 841, and the client saw the host's ship travel 2039 units while the host saw 2024.

Three defects had survived a fully passing check suite before that run.

## Findings worth knowing

Short facts that cost time to establish.

- `XGameRuntime.dll` in `System32` is the GDK's runtime host on every Windows machine. Its
  six exports are a private surface, and the documented flat API is resolved through the
  static library instead. See
  [Troubleshooting](troubleshooting.md#gdk-entry-points-cannot-be-resolved-with-dllimport).
- `MicrosoftGame.config` has to sit beside an unpackaged executable, not only in the
  packaging source tree.
- `XblInitialize` accepting a service configuration id proves only that the string
  marshalled. It contacts no service and confirms nothing about whether the id belongs to
  the title.
- The connectivity hint is a best-effort device signal, not endpoint reachability.
- MonoGame has no controller-disconnect event, so controller loss has to be detected by
  polling `GamePad` every frame.
- MonoGame installs no `SynchronizationContext`.
- `SpriteFont` silently substitutes `DefaultCharacter` for undeclared characters, so the
  compiled `.xnb` is the artifact worth checking.
- `SpriteBatch` custom effects apply per batch rather than per object, so anything needing
  its own effect needs its own pass, which changes draw ordering.
- `UnreliableSequenced` delivery has to reject an older message, or a late snapshot moves
  the simulation backwards.
- A sanitizer that clamps a hostile value has to distinguish "strictly positive" from
  "meaningfully zero". Clamping a scale of `-5` to `0` produces a projectile that looks
  valid and expires immediately.
- PlayFab creates a statistic on first write with last-value-wins aggregation, so an
  unconfigured high-score table ranks the most recent score rather than the best one. It
  cannot be set from code.
- PlayFab user-data values are capped at 1,000 characters, which is roughly 750 bytes after
  Base64 encoding.

## Measured results

| Measurement | Result |
|---|---|
| Idle `Pump()` cost | 0.01 to 0.03 microseconds against a 16.6 ms frame budget |
| Practice bots | Eight-player start in 0 ms, ten seconds of simulation in 38 ms |
| LAN transport | A 31-object world snapshot survived fragmentation and reassembly intact |
| Connectivity grace period | Sustained loss ended the match at exactly 8.0 s; a 4.0 s flap did not |
| Constrain handling | Elapsed time held at 0.00 s for four seconds, then resumed normally after unconstrain |
| Controller overlay | Match clock ran 9:55 to 9:52 underneath the overlay, with a bot scoring during it |

## What has never been observed

See [Known gaps](known-gaps.md#never-observed-end-to-end). Nothing in this sample should be
read as a claim of live service validation.
