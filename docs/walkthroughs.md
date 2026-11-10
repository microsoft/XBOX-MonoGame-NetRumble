# GDK and PlayFab walkthroughs

Follow a player action from the MonoGame screen through `IPlatformProvider` to the GDK.NET
or PlayFab call behind it. Each exercise names what to expect and where to start reading.
These are **expected behaviors**, not a record that every live service has been tested.
Record results in the [manual test plan](manual-test-plan.md); mark an exercise untested
when its accounts, title, devices or hardware are unavailable.

Start with [Getting started](getting-started.md) and
[Configuration](configuration.md). See the [glossary](glossary.md) for unfamiliar terms.

## Practice after XBOX sign-in

**Needs:** a successful build, the GDK runtime and an authorized XBOX account. Practice
uses local simulation, but it does not bypass the account requirement.

**Try:** Run the game, press a key at the sign-in screen, choose **Practice**, then press
**Space** in the lobby to ready up.

```powershell
dotnet run --project src\NetRumble.Game --no-build
```

**Follow:** [`Program.MainCore`](../src/NetRumble.Game/Program.cs) selects the GDK
provider. [`AcquireUserScreen`](../src/NetRumble.Game/UI/Screens/AcquireUserScreen.cs)
asks its identity service to sign in. [`LobbyScreen`](../src/NetRumble.Game/UI/Screens/LobbyScreen.cs)
starts a local practice session instead of creating a Party network.

**Look for:** the signed-in gamertag at the menu and a practice match with the configured
number of bots after readying up. Practice does not exercise PlayFab Lobby or Party.

**If unavailable:** Check the build and runtime prerequisites separately. A missing
runtime or XBOX sign-in failure keeps the player at the sign-in screen; there is no
guest or **Continue Offline** option.

## Sign in to XBOX and PlayFab

**Needs:** the GDK runtime, the sample title's sandbox and an authorized XBOX test account,
or an appropriately configured title of your own. See
[Configuration](configuration.md#xbox-title-and-service-configuration).

**Try:** Launch the GameCore provider and press a key at the acquire-user screen:

```powershell
dotnet run --project src\NetRumble.Game --no-build -- --platform=gamecore
```

The sample requests the system's default user silently, then attempts to obtain a
PlayFab identity for that XBOX user. The simplified user model does not offer a
title-driven account picker or in-session account switch.

**Follow:** [`GameCoreIdentityService`](../src/NetRumble.Platform.GameCore/Services/GameCoreIdentityService.cs)
owns the XBOX `User` and calls
[`GameCoreOnlineSession`](../src/NetRumble.Platform.GameCore/Services/GameCoreOnlineSession.cs).
[`PlayFabOnlineServices.SignInWithXboxUserAsync`](../src/NetRumble.Platform.PlayFab/PlayFabOnlineServices.cs)
uses GDK.NET's `PlayFabLocalUser` and keeps the resulting PlayFab entity for Lobby and
Party operations. This is an excerpt from that method, not a standalone program:

```csharp
var localUser = PlayFabLocalUser.CreateForXboxUser(config, user);
var login = await localUser.LoginAsync(createAccount, token).ConfigureAwait(false);
```

The surrounding code owns and disposes native objects. Account creation is permitted only
when the XBOX age group allows it; an existing account can still be used when creation is
not allowed.

**Look for:** the XBOX gamertag at the main menu. A completed XBOX login alone does not
prove the PlayFab exchange succeeded; follow the host/join exercise to exercise that
second identity.

**If unavailable:** The sign-in screen shows the stage and offers a retry. The system may
relaunch a loose-deployed PC title through its sign-in bootstrapper when no default user
exists. A PlayFab failure is non-fatal to XBOX sign-in, but the missing entity prevents
online Party operations. Check title, sandbox, account access and sanitized logs rather
than interpreting a visible gamertag as proof of PlayFab access.

## Discover and join a Party match

**Needs:** two authorized XBOX test accounts and working GameCore/PlayFab access for the
same configured title. The current native PlayFab migration still needs an authenticated
two-device pass.

**Try:** Sign in on both devices. Choose **Host Match** on one and share its five-character
code. On the other choose **Join Match**, enter the code and ready both players.

**Follow:** `LobbyScreen` checks connectivity and multiplayer/communications privileges
before calling `IPartyService.HostAsync` or `JoinAsync`.
[`GameCorePartyService`](../src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs)
creates or joins the Party network.
[`PlayFabLobbyClient`](../src/NetRumble.Platform.PlayFab/PlayFabLobbyClient.cs)
publishes and searches the code; Lobby finds a match, while Party carries match traffic.
See [Multiplayer](multiplayer.md) for the ordered host and join sequences.

**Look for:** the code on the host, both players in the roster, and synchronized ready-up.
The two builds must advertise the same protocol version.

**If unavailable:** Distinguish an XBOX account without a PlayFab entity, a denied
privilege, an invalid or expired code, a protocol mismatch, and a network/service failure.
The sample has no queue-based PlayFab Matchmaking, host migration, or join-in-progress.

## Invite a friend

**Needs:** XBOX activity and invite support, an authorized pair of accounts and a joinable
GameCore session.

**Try:** From a hosted lobby, select an empty roster slot to open the invite composer,
then accept the invitation on the second account. Repeat with the receiving title already
running and with a cold launch.

**Follow:** [`ActivityCoordinator`](../src/NetRumble.Game/ActivityCoordinator.cs)
publishes the connection string through
[`GameCoreActivityService`](../src/NetRumble.Platform.GameCore/Services/GameCoreActivityService.cs).
[`InviteRouter`](../src/NetRumble.Game/InviteRouter.cs) holds activation until sign-in
and the front end are ready, then opens `LobbyScreen` with the connection string.

**Look for:** the receiver joining the advertised lobby, or being asked before leaving an
existing match. Earlier console invite testing predates the native PlayFab migration;
it is not evidence that the new end-to-end path has passed.

**If unavailable:** Check the XBOX title's activation and activity configuration, account
permissions and protocol version.

## Voice, text, privileges and privacy

**Needs:** a GameCore session with accounts permitted to communicate and the appropriate
audio devices.

**Try:** In an online lobby, toggle your own mute with **T**. In a match, speak to a
second player and press **T** to open text chat. Try the transcription option, then repeat
with a test account whose communication or per-player privacy settings restrict the
interaction.

**Follow:** [`GameCorePrivilegeService`](../src/NetRumble.Platform.GameCore/Services/GameCorePrivilegeService.cs)
checks multiplayer and communications.
[`GameCorePrivacyService`](../src/NetRumble.Platform.GameCore/Services/GameCorePrivacyService.cs)
combines per-player permissions with mute and avoid lists.
[`GameCorePartyService`](../src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs)
applies the restrictions to Party chat; [`GameplayScreen`](../src/NetRumble.Game/UI/Screens/GameplayScreen.cs)
and [`SttOverlay`](../src/NetRumble.Game/UI/Elements/SttOverlay.cs) handle text and captions.
Player-authored text is verified through
[`GameCoreModerationService`](../src/NetRumble.Platform.GameCore/Services/GameCoreModerationService.cs).

**Look for:** no voice or text transmission when communications are denied, and the
appropriate per-player restriction when privacy denies it. Chat starts muted until the
communications privilege is confirmed.

**If unavailable:** A failed privilege or privacy check is not a grant. Audio and live
policy behavior require real accounts and have not been established by the local harness.

## Saves and achievements

**Needs:** an authorized XBOX account and the corresponding configured service. Roaming
needs a second device. Use test accounts and data.

**Try:** Change an option, exit, and restart with the same account. For a roaming test,
allow the platform to synchronize and then sign in on another device. Exercise an
achievement condition and inspect the account's achievement state.

**Follow:** [`PlayerProfile`](../src/NetRumble.Game/Profile/PlayerProfile.cs) keeps local
settings; [`CloudSettingsSync`](../src/NetRumble.Game/Profile/CloudSettingsSync.cs) pulls
and pushes their cloud copy.
[`GameCoreGameSaveService`](../src/NetRumble.Platform.GameCore/Services/GameCoreGameSaveService.cs)
acquires the user's `XGameSaveFiles` folder through GDK.NET and reads or writes files
there. The GDK manages synchronization outside the title process.
[`AchievementTracker`](../src/NetRumble.Game/Profile/AchievementTracker.cs) maps
gameplay progress to
[`GameCoreAchievementService`](../src/NetRumble.Platform.GameCore/Services/GameCoreAchievementService.cs).

**Look for:** local settings returning for the same account. A cloud test passes only
when the second device receives the expected data, and an achievement test only when the
XBOX service shows the update. Match history uses local storage and is not a
cloud-roaming test.

**If unavailable:** Local and cloud saves are different tiers; an absent cloud record or
service failure does not prove a successful upload. Settings pushes are best-effort and
do not show a success confirmation. XGameSaveFiles roaming has no documented two-device
round trip, and no achievement unlock has been observed on XBOX hardware.

## Lifecycle, connectivity and controllers

**Needs:** a desktop window for focus behavior; console lifecycle, account-removal and
controller-association checks need the relevant development hardware and accounts.

**Try:** During a practice match, move focus away from the desktop window and return.
On a development console, test suspend/resume and controller removal with an authorized
test session, recording whether the match or player state changes as expected.

**Follow:** [`GameCoreRuntime`](../src/NetRumble.Platform.GameCore/Services/GameCoreRuntime.cs)
registers lifecycle and connectivity notifications.
[`LifecycleCoordinator`](../src/NetRumble.Game/LifecycleCoordinator.cs) coordinates
durable state and session cleanup;
[`ControllerWatcher`](../src/NetRumble.Game/ControllerWatcher.cs) drives the
controller-loss overlay.

**Look for:** desktop constrain/unconstrain behavior on focus changes and a visible
controller-loss prompt when a tracked controller is removed. Suspend commits local durable
state and abandons a live session rather than waiting for the next frame.

**If unavailable:** Desktop focus is an analogue, not proof of console suspend behavior.
Record hardware-only cases as untested when the device or account is unavailable.
