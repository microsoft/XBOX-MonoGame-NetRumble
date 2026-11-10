# Manual test plan

Use this plan to distinguish an implemented path from one actually observed working.
The [walkthroughs](walkthroughs.md) explain the code behind each action. Nothing in this
page is a claim that the listed tests have passed, and it is not a certification report.

## Before a run

Record the commit/build, Windows or console environment, provider mode, GDK/runtime
version where applicable, title/sandbox, and the number and type of test devices and
accounts. Use authorized test accounts and test data. Do not copy tokens, secrets or
private account information into an issue or test report.

For every case record **pass**, **fail**, or **untested**, plus the observable result and
any relevant sanitized logs. Missing access, a second device or console hardware means
**untested**, not pass. Use [Known gaps](known-gaps.md) for the current historical
evidence; do not overwrite it with an assumption based on this plan.

## Environment tiers

| Tier | Setup |
|---|---|
| Build and native-independent checks | Windows, .NET and the GDK.NET source dependency. These checks do not prove live service behavior. |
| GameCore on PC | GDK and native libraries, the intended XBOX/PlayFab title configuration, and authorized test accounts. Multiplayer needs two accounts. |
| Console | GameCore prerequisites plus partner-only console tooling and development hardware. |

Run the smallest applicable tier first; native-independent checks are not a substitute for
XBOX privileges, PlayFab Lobby, Party or account-backed storage.

## Acceptance cases

| Case | Tier | Action | Expected observation |
|---|---|---|---|
| Required sign-in | GameCore | Launch with an authorized account, then separately try without an available default user. | The account reaches the menu. A missing user stays at sign-in or triggers the system-owned sign-in/relaunch flow; no guest option appears. |
| Practice | GameCore | After sign-in, choose **Practice**, then press **Space**. | A solo practice match starts without creating a Party network. |
| Local settings | GameCore | Change an option, exit, and restart with the same account. | The account's local setting is restored. |
| XBOX and PlayFab sign-in | GameCore | Sign in with an authorized account, then attempt the host flow. | XBOX sign-in reaches the menu; hosting exercises the additional PlayFab identity rather than assuming it from the gamertag. Record each failure separately. |
| Lobby and Party | GameCore | Host and join by code on two devices, ready both, and exchange gameplay traffic. | Both players reach the same match; no protocol mismatch or connection error occurs. |
| Invalid or stale code | GameCore | Enter a malformed code, then try a code after its host has left. | The join is rejected or times out with a useful reason; a retry can be made. |
| Host departure | GameCore | Join a host, then have the host leave. | The client is told the session ended rather than remaining in a usable-looking match. |
| Invites | GameCore | Accept an invite while the title is running and from a cold launch. | The receiver joins after sign-in/front-end readiness, or receives an actionable refusal. |
| Communications policy | GameCore | Try voice and text with permitted accounts; repeat with a communication or per-player restriction and self-mute. | Allowed communication works when supported; restricted or muted communication is suppressed according to the policy. |
| Achievement | GameCore | Trigger a configured achievement using a test account and inspect its XBOX service state. | The appropriate progress or unlock is visible on the service, not just in local counters. |
| Cloud roaming | GameCore | Change a setting on one device, allow the platform to synchronize, then sign in on a second device with the same account. | The second device receives the expected setting. A local file or a best-effort push alone is not evidence of roaming. |
| Account isolation | GameCore | Use two test accounts in separate launches on one device, following the platform's user-change flow, and give them different settings. | Each account sees its own data; the first account's progress is not attributed to the second. The title does not switch accounts in session. |
| Focus and controller | PC/Console | On desktop, change window focus during play. On console, disconnect and reconnect the associated controller. | The desktop constrain path responds to focus; the console shows and clears the controller-loss prompt. Record the two environments separately. |
| Suspend and resume | Console | Suspend a live session and resume it using the partner-documented procedure. | Durable local state is committed and the live session is abandoned; the player is informed appropriately on return. |
| User removal | Console | Remove the signed-in test user during a session. | The title commits the departing user's durable state without assigning it to another user; the system may terminate and relaunch the title for sign-in. |

## Automated aids

Build the Release configuration, then run the focused native-independent checks without
a live service:

```powershell
dotnet build NetRumble.slnx -c Release /p:NoWarn=NU1603
dotnet run --project tools\NetRumble.PlatformSpike -c Release --no-build -- --identity-checks-only
dotnet run --project tools\NetRumble.PlatformSpike -c Release --no-build -- --gamesave-files-checks-only
dotnet run --project tools\NetRumble.PlatformSpike -c Release --no-build -- --playfab-checks-only
dotnet run --project tools\NetRumble.GameSaveChecks -c Release --no-build
```

They exercise the simplified user model, temporary-folder save behavior, game-layer save
logic and PlayFab adapter behavior. They do not prove real login, cloud roaming or
connected play. The optional `--playfab-native-smoke` checks native startup and teardown
when the prerequisites are installed; it does not authenticate. See
[Run modes](run-modes.md#platform-spike) for the wider harness and its limitations.
