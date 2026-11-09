# Run modes

NetRumble picks a platform provider at startup and can be forced onto a specific one from
the command line. Every mode below runs from the same build.

In the lobby, click the ship and color tabs to customize; keyboard navigation does
not change those tabs. Controller navigation is unchanged. The original hotkeys
remain: **Space** toggles Ready, **T** toggles voice mute, and **Esc** opens the leave
confirmation. There are no additional Ready, mute or leave buttons.

## Default

```powershell
dotnet run --project src\NetRumble.Game
```

Resolves the best available provider. On a machine with a Microsoft GDK installed that is
the GDK provider; on a machine without one it falls back to offline.

## Offline

```powershell
dotnet run --project src\NetRumble.Game -- --platform=offline
```

Forces the offline provider even on a machine that has the GDK. Choose **Continue
Offline**, then **Practice**. Every platform service reports as unavailable or degrades
to a local no-op, so this is the fastest way to work on the front end.

## LAN

```powershell
dotnet run --project src\NetRumble.Game -- --platform=lan
```

Run two instances with this flag to play a real match between them. The LAN provider is a
genuine UDP transport with hosting, five-character join codes and text chat, and it needs
no GDK, no PlayFab title and no XBOX account.

Because it bypasses every XBOX check, it cannot validate privacy, achievements, privileges
or cloud save.

> [!WARNING]
> The LAN transport is a development tier and is **not internet safe**. It has no
> encryption, no congestion control and no RTT estimation. See
> [Known gaps](known-gaps.md#networking).

## Separate PlayFab title

```powershell
dotnet run --project src\NetRumble.Game -- --playfab-title=<id>
```

The `NETRUMBLE_PLAYFAB_TITLE_ID` environment variable does the same thing. This composes
with any provider mode. Adding PlayFab to offline or LAN now requires the Gaming Runtime
and native PlayFab redistributables, just like GameCore. In Debug builds, supply
`--pf-user=<developer-id>` for custom-ID sign-in against a title you control; this
authentication path is disabled in Release. LAN without PlayFab still needs no GDK.

Xbox sign-in, user-data calls and Lobby operations all use GDK.Net's native wrappers.
There is no REST fallback. The selected provider must be initialized and pumped every
frame, including while lobby operations are outstanding.

## Unattended playthrough

```powershell
dotnet run --project src\NetRumble.Game -- --autopilot=host
dotnet run --project src\NetRumble.Game -- --autopilot=join
```

Drives two instances into a match without a human. `--autopilot-seconds=<n>` bounds the
run and `--autopilot-file=<path>` writes a transcript. Useful for reproducing a netcode
defect and for capturing frames.

## Platform spike

```powershell
dotnet run --project tools\NetRumble.PlatformSpike
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=offline
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=gamecore
```

`tools\NetRumble.PlatformSpike` exercises the platform contract without MonoGame. It
initializes a provider, signs in while pumping, round-trips a save, exercises the netcode
and measures pump cost. It is the regression gate for any change to the platform boundary
or the multiplayer core. See [Contributing](../CONTRIBUTING.md).

Run just the native-independent PlayFab adapter checks with
`--playfab-checks-only`. These cover save compatibility, size limits, join-code metadata,
error mapping and unavailable/canceled paths; they do not prove live service behavior.
`--playfab-native-smoke` checks native runtime, Core/Services and Lobby startup,
one empty state-change pump and teardown without authenticating or writing service data.

`--platform=offline` reports three failures on a machine that has the GDK installed. That
is a property of the harness rather than of the game. See
[Troubleshooting](troubleshooting.md#the-offline-spike-reports-three-failures-on-a-gdk-machine).

## XBOX Series X|S

Available to XBOX development partners only. See
[Building for XBOX Series X|S](xbox-console-build.md).
