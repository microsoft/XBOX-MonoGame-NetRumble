# Platform abstraction

The game reaches every platform capability through **`IPlatformProvider`** and nothing
else. This page explains the boundary and the rules a provider has to honour. It is the
part of the sample most likely to be useful in another codebase.

## Why the layer exists

No official C# binding ships for PlayFab Party, PlayFab Multiplayer (Lobby), GDK `XUser`
and `XblAchievements`, or GameInput outside the Unity plugins. A C# game therefore has to
supply that binding itself.

This sample started with a hand-written P/Invoke layer and later replaced it wholesale
with **XBOX GDK.NET**, a managed projection of the GDK, PlayFab and Party. The abstraction
is what made that replacement invisible to the game: no file in `NetRumble.Core` or
`NetRumble.Game` changed. It exists so that the next such replacement is equally cheap.

## Assemblies

| Assembly | Contains | Replaceable |
|---|---|---|
| `NetRumble.Platform` | Interfaces, POCOs, enums. No dependencies of its own. | No. This is the contract. |
| `NetRumble.Platform.Offline` | A complete local and no-op implementation | No. This is always the fallback. |
| `NetRumble.Platform.GameCore` | The only assembly that knows the GDK exists: XBOX GDK.NET, native handles, runtime lifetime | Yes. This is the disposable one. |
| `NetRumble.Platform.PlayFab` | Lobby discovery, the authentication exchange, the PlayFab cloud save tier | Yes |
| `NetRumble.Platform.Lan` | A UDP transport that needs no GDK, title or account | Yes |
| `NetRumble.Core` | Simulation, match flow, netcode messages | No |
| `NetRumble.Game` | MonoGame rendering, input, audio, UI | No |

The dependency rule: `NetRumble.Core` and `NetRumble.Game` reference
**`NetRumble.Platform` only**. Neither may reference a provider assembly. The single
exception is `PlatformProviderFactory`, which is the composition root.

## The four invariants

A replacement provider has to honour all four.

### 1. No native type crosses the boundary

No `IntPtr`, `HRESULT`, `XUserHandle`, `XAsyncBlock` or SDK structure appears in any
`NetRumble.Platform` signature. Native errors are translated into a `PlatformResult`,
which carries a `PlatformStatus` the game branches on plus a `Diagnostics` string used
only for logging.

`PlatformUser` is a flat record. The provider keeps the real user handle privately, keyed
by `PlatformUser.LocalId`.

The benefit is concrete. Native lifetime, error translation and ABI detail all stay on one
side of a stable contract, so replacing the implementation cannot ripple into game code.

### 2. Everything is `Task` based

The GDK's `XAsyncBlock` and callback model used to be converted to `Task<T>` by a
hand-written bridge. XBOX GDK.NET returns `Task` directly, so that file is gone. This
invariant was written to make exactly that change cheap.

### 3. Completions land on the pump thread

`IPlatformRuntime.Pump()` is called once per frame from `Game.Update`. **Every** task a
provider hands out and **every** event it raises must resolve inside that call.

The original P/Invoke provider got this for free by creating its task queue in manual
dispatch mode and draining it only from `Pump()`. XBOX GDK.NET completes on the thread
pool instead, so the guarantee moved up to the provider boundary.
`Services/PumpDispatcher.cs` marshals every XBOX GDK.NET task and event onto the pump
thread, and is drained from `Pump()`.

The invariant did not change. Only its enforcement point moved. Any provider built on
thread-pool completions needs the same treatment.

This is the reason gameplay, UI and netcode code contain no locks and no dispatcher.

> [!IMPORTANT]
> MonoGame installs no `SynchronizationContext`, so a bare `await` resumes on a thread-pool
> thread even when the continuation touches screens or transport state. The sample installs
> `GameThreadContext` and drains it at the start of `Update`. Use `ConfigureAwait(false)`
> only for platform work that is meant to stay off the game thread.

### 4. Absence is a normal condition

A machine with no GDK installed is a supported configuration, and it is how the front end
and the practice match are developed. Providers report this through
`IPlatformRuntime.IsRuntimeAvailable` and `Capabilities`. Best-effort services return
`PlatformStatus.Unavailable` instead of throwing.

The sample applies a two-tier policy. Multiplayer is gated and tells the player why.
Achievements, saves and presence degrade to working no-ops.

A capability flag means the whole path works end to end. The provider does not advertise
`Invites` or `Presence` merely because an entry point binds.

## Replacing the implementation

The original plan was to add a second provider assembly and delete the first. What
happened was cheaper. Because every service already returned a `PlatformResult` from a
`Task` based method, and because no native handle ever crossed the boundary, XBOX GDK.NET
was swapped in underneath the existing provider one service at a time. The game ran
against a mix of migrated and unmigrated services throughout, and `Capabilities` reported
only what was really wired, so the UI lit up in step.

Three things the sample supplies that XBOX GDK.NET does not:

- **`Services/PumpDispatcher.cs`**, for invariant 3.
- **`Interop/*RuntimeProbe.cs`**, for invariant 4. Three small self-contained probes that
  answer "is this runtime actually present?" before anything tries to use it. XBOX GDK.NET
  reasonably assumes it runs on a machine that has a GDK, and this sample cannot.
- A binding for process lifecycle notifications, which XBOX GDK.NET does not wrap.

The cost: XBOX GDK.NET is a `ProjectReference` and is not on NuGet, so it became a
build-time prerequisite for the whole solution. That was a deliberate trade.

If a third implementation appears, the recipe is unchanged. Implement `IPlatformProvider`,
add one case to `PlatformProviderFactory.Create`, and delete the assembly you are
replacing.

## PlayFab composes as a layer

PlayFab cloud save and lobby discovery are orthogonal to the transport and to the platform
runtime. They are composed over whichever provider is active rather than being a provider
mode of their own, so `--platform=lan --playfab-title=<id>` works without needing an enum
value for every combination.

## Verifying a provider

`tools/NetRumble.PlatformSpike` exercises the boundary without MonoGame. It initializes a
provider, signs in while pumping, round-trips a local save, asserts the ported enum masks,
exercises the transport and measures pump cost.

```powershell
dotnet run --project tools\NetRumble.PlatformSpike
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=offline
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=gamecore
```

Run it against any new provider. If it passes in the default and `gamecore` modes, the
contract holds.

The harness needs two files staged beside its own binary, and its project file handles
both:

- The vendored PlayFab Party redistributable from `third_party/party/`. Party loads its
  siblings through the normal search order, so all three DLLs have to be adjacent to the
  executable.
- `packaging/MicrosoftGame.config`, which is where an unpackaged GDK process reads its
  title identity. Without it, `XGameGetXboxTitleId` returns `ERROR_NOT_FOUND` and every
  service check behind it fails too.

`--platform=offline` reports three failures on a machine that has the GDK installed. See
[Troubleshooting](troubleshooting.md#the-offline-spike-reports-three-failures-on-a-gdk-machine).

## Measured cost

An idle `Pump()` measures roughly 0.01 to 0.03 microseconds. Against a 16.6 ms frame
budget that is free, so there is no reason to pump less often than every frame.

## Related pages

- [Platform services](platform-services.md) lists every service, where it is implemented
  and how far it has been proven.
- [Design notes](design-notes.md) records the reasoning behind the decisions summarised
  here.
