# Platform abstraction

The game reaches every platform capability through **`IPlatformProvider`** and nothing
else. This page explains the boundary and the rules a provider has to honour. It is the
part of the sample most likely to be useful in another codebase.

## Why the layer exists

GDK and PlayFab expose native APIs and objects whose lifetimes need managing. A C# game
needs a managed binding to call them; this sample uses XBOX GDK.NET for that boundary.

This sample started with a hand-written P/Invoke layer and later replaced it with
**XBOX GDK.NET**, a managed projection of the GDK, PlayFab and Party. The abstraction
keeps native ownership and SDK details out of gameplay and simulation.

## Assemblies

| Assembly | Contains | Replaceable |
|---|---|---|
| `NetRumble.Platform` | Interfaces, POCOs, enums. No dependencies of its own. | No. This is the contract. |
| `NetRumble.Platform.GameCore` | XBOX services through XBOX GDK.NET, native handles, runtime lifetime | Yes. This is the disposable one. |
| `NetRumble.Platform.PlayFab` | Native GDK.Net authentication and connected Lobby discovery | Yes |
| `NetRumble.Core` | Simulation, match flow, netcode messages | No |
| `NetRumble.Game` | MonoGame rendering, input, audio, UI | No |

The dependency rule: `NetRumble.Core` references the platform contract without a
concrete provider. `NetRumble.Game` references the GameCore assembly to reach
`PlatformProviderFactory`, the composition root, while gameplay and UI use the provider
interfaces instead of native APIs.

## The four invariants

A replacement provider has to honour all four.

### 1. No native type crosses the boundary

No `IntPtr`, `HRESULT`, `XUserHandle`, `XAsyncBlock` or SDK structure appears in any
`NetRumble.Platform` signature. Native errors are translated into a `PlatformResult`,
which carries a `PlatformStatus` the game branches on plus a `Diagnostics` string used
only for logging.

`PlatformUser` is a flat record. The provider keeps the real user handle privately.

The benefit is concrete. Native lifetime, error translation and ABI detail all stay on one
side of a stable contract, so replacing the implementation cannot ripple into game code.

### 2. Asynchronous operations use `Task`

The GDK's `XAsyncBlock` and callback model used to be converted to `Task<T>` by a
hand-written bridge. XBOX GDK.NET returns `Task` for its asynchronous operations, so that
file is gone. Synchronous state, methods and events remain synchronous at the boundary.

### 3. Keep game state on the game thread

`NetRumbleGame.Update` first drains [`GameThreadContext`](../src/NetRumble.Game/GameThreadContext.cs),
then calls `IPlatformRuntime.Pump()` before the rest of the game logic. The platform pump
drains selected dispatched work and processes PlayFab Lobby and Party state changes.
Do not block that frame loop while waiting for an operation that needs the pump.

GDK.NET tasks can complete on a thread-pool thread.
[`PumpDispatcher`](../src/NetRumble.Platform.GameCore/Services/PumpDispatcher.cs) moves
selected completions and callbacks through the pump; it does not marshal every task or
event. A task completed there can still run its continuation asynchronously. MonoGame
installs no synchronization context of its own, so the game installs `GameThreadContext`
to return game and UI continuations to the game thread. Use `ConfigureAwait(false)` only
when the subsequent work is safe off that thread.

Suspend and account-change notifications can arrive on platform callback threads.
Suspend is handled inline because the OS can freeze the process before another frame;
it must not wait for a future pump. See
[`LifecycleCoordinator`](../src/NetRumble.Game/LifecycleCoordinator.cs).

### 4. Report unavailable services explicitly

The GDK provider reports missing dependencies through `IPlatformRuntime.IsRuntimeAvailable`
and `Capabilities`. Services return `PlatformStatus.Unavailable` instead of pretending
to succeed. The game requires GDK sign-in before opening the main menu, including Practice.

A capability flag indicates that a path is implemented and its local dependencies are
available. It does not prove title configuration, account permission or a successful
service round trip.

## Replacing the implementation

The original plan was to add a second provider assembly and delete the first. What
happened was cheaper. Because every service already returned a `PlatformResult` from a
`Task` based method, and because no native handle crossed the boundary, XBOX GDK.NET
could be introduced underneath the existing provider one service at a time. Capability
flags describe locally available paths rather than proving live service access.

Three things the sample supplies that XBOX GDK.NET does not:

- **`Services/PumpDispatcher.cs`**, for invariant 3.
- **`Interop/*RuntimeProbe.cs`**, for invariant 4. Three small self-contained probes that
  answer "is this runtime actually present?" before anything tries to use it. XBOX GDK.NET
  assumes it runs on a machine that has a GDK; probes let this sample show a useful
  sign-in failure instead of crashing when a dependency is missing.
- A binding for process lifecycle notifications, which XBOX GDK.NET does not wrap.

The cost: XBOX GDK.NET is a `ProjectReference` and is not on NuGet, so it became a
build-time prerequisite for the whole solution. That was a deliberate trade.

For another implementation, implement `IPlatformProvider` and wire it through
`PlatformProviderFactory.Create`. Preserve the boundary and verify the new provider's
threading and lifecycle behavior rather than assuming it matches GameCore.

## PlayFab belongs to the GDK session

The GDK provider owns one XBOX-backed PlayFab identity for Party and Lobby. A separate
PlayFab title can be selected with `--playfab-title=<id>`, without changing the platform
provider or its account requirement. Cloud saves use GDK `XGameSaveFiles`.

## Verifying a provider

`tools/NetRumble.PlatformSpike` exercises the boundary without MonoGame. It initializes a
provider, signs in while pumping, round-trips a local save, asserts the ported enum masks,
exercises the transport and measures pump cost.

```powershell
dotnet run --project tools\NetRumble.PlatformSpike
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=gamecore
```

The default and `gamecore` modes currently select the same provider. Inspect each
PASS, FAIL and SKIP line; the broad run does not aggregate every failure into its exit
code. The focused checks and live cases in the [manual test plan](manual-test-plan.md)
provide different evidence.

The harness needs two files staged beside its own binary, and its project file handles
both:

- The vendored PlayFab Party redistributable from `third_party/party/`. Party loads its
  siblings through the normal search order, so all three DLLs have to be adjacent to the
  executable.
- `packaging/MicrosoftGame.config`, which is where an unpackaged GDK process reads its
  title identity. Without it, `XGameGetXboxTitleId` returns `ERROR_NOT_FOUND` and every
  service check behind it fails too.

## Measured cost

An idle `Pump()` measures roughly 0.01 to 0.03 microseconds. Against a 16.6 ms frame
budget that is free, so there is no reason to pump less often than every frame.

## Related pages

- [Platform services](platform-services.md) lists every service, where it is implemented
  and how far it has been proven.
- [Design notes](design-notes.md) records the reasoning behind the decisions summarised
  here.
