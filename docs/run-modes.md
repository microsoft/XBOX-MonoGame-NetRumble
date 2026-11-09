# Run modes

NetRumble uses the GDK provider on both PC and XBOX Series X|S. The default `auto` and
explicit `gamecore` startup modes select that same provider. GDK sign-in is required
before entering the main menu.

In the lobby, click the ship and color tabs to customize; keyboard navigation does
not change those tabs. Controller navigation is unchanged. The original hotkeys
remain: **Space** toggles Ready, **T** toggles voice mute, and **Esc** opens the leave
confirmation. There are no additional Ready, mute or leave buttons.

## Default

```powershell
dotnet run --project src\NetRumble.Game
```

Starts the GDK provider. Missing runtime dependencies and sign-in failures are shown on
the sign-in screen; they do not open the main menu.

## Practice

After signing in, choose **Practice** from the main menu for a single-player match
against bots. It uses local simulation and does not create a Party network.

## Separate PlayFab title

```powershell
dotnet run --project src\NetRumble.Game -- --playfab-title=<id>
```

The `NETRUMBLE_PLAYFAB_TITLE_ID` environment variable does the same thing. The GDK
provider exchanges the signed-in XBOX user for a PlayFab identity. The Gaming Runtime
and native PlayFab redistributables are required.

Xbox sign-in and Lobby operations use GDK.Net's native wrappers.
There is no REST fallback. The selected provider must be initialized and pumped every
frame, including while lobby operations are outstanding.

## Unattended playthrough

```powershell
dotnet run --project src\NetRumble.Game -- --autopilot=host
dotnet run --project src\NetRumble.Game -- --autopilot=join
```

Drives two instances into a match without a human. `--autopilot-seconds=<n>` bounds the
run and `--autopilot-file=<path>` writes a transcript. Useful for reproducing a netcode
defect and for capturing frames. Multiplayer runs require the GDK provider, title access
and signed-in accounts.

## Platform spike

```powershell
dotnet run --project tools\NetRumble.PlatformSpike
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=gamecore
```

`tools\NetRumble.PlatformSpike` exercises the platform contract without MonoGame. It
initializes a provider, signs in while pumping, round-trips a save, exercises the netcode
and measures pump cost. It is the regression gate for any change to the platform boundary
or the multiplayer core. See [Contributing](../CONTRIBUTING.md).

Run just the native-independent PlayFab adapter checks with
`--playfab-checks-only`. These cover identity lifetime, join-code metadata,
error mapping and unavailable/canceled paths; they do not prove live service behavior.
`--playfab-native-smoke` checks native runtime, Core/Services and Lobby startup,
one empty state-change pump and teardown without authenticating or writing service data.

## XBOX Series X|S

Available to XBOX development partners only. See
[Building for XBOX Series X|S](xbox-console-build.md).
