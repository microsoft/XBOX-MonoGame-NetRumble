# Platform services

Every service behind `IPlatformProvider`, where it is implemented, and how far it has been
proven. Use this page to find the file you want to read.

Use the [walkthroughs](walkthroughs.md) for player actions, expected results and failure
cases. The source files below explain ownership and ordering in more detail.

## Where each integration lives

| Integration | Source |
|---|---|
| GDK sign-in and the exchange for a PlayFab identity | [`GameCoreIdentityService`](../src/NetRumble.Platform.GameCore/Services/GameCoreIdentityService.cs), [`PlayFabOnlineServices`](../src/NetRumble.Platform.PlayFab/PlayFabOnlineServices.cs) |
| Required sign-in and progress screen | [`AcquireUserScreen`](../src/NetRumble.Game/UI/Screens/AcquireUserScreen.cs) |
| PlayFab Party as the match transport | [`GameCorePartyService`](../src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs), [`PartyMatchNetwork`](../src/NetRumble.Core/Net/PartyMatchNetwork.cs) |
| Join-code discovery with PlayFab Lobby | [`PlayFabLobbyClient`](../src/NetRumble.Platform.PlayFab/PlayFabLobbyClient.cs) |
| Multiplayer and communications privilege checks | [`GameCorePrivilegeService`](../src/NetRumble.Platform.GameCore/Services/GameCorePrivilegeService.cs) |
| Per-player mute, block and avoid enforcement | [`GameCorePrivacyService`](../src/NetRumble.Platform.GameCore/Services/GameCorePrivacyService.cs) |
| Text verification before player-authored content is shown | [`GameCoreModerationService`](../src/NetRumble.Platform.GameCore/Services/GameCoreModerationService.cs) |
| Party voice and speech-to-text captions | [`GameCorePartyService`](../src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs), [`SttOverlay`](../src/NetRumble.Game/UI/Elements/SttOverlay.cs) |
| One-shot and incremental achievement progress | [`GameCoreAchievementService`](../src/NetRumble.Platform.GameCore/Services/GameCoreAchievementService.cs), [`AchievementTracker`](../src/NetRumble.Game/Profile/AchievementTracker.cs) |
| Local and cloud save for settings and achievement stats | [`LocalFileGameSaveService`](../src/NetRumble.Platform.GameCore/Services/LocalFileGameSaveService.cs), [`GameCoreGameSaveService`](../src/NetRumble.Platform.GameCore/Services/GameCoreGameSaveService.cs), [`CloudSettingsSync`](../src/NetRumble.Game/Profile/CloudSettingsSync.cs) |
| Local match history | [`MatchHistoryStore`](../src/NetRumble.Game/Profile/MatchHistoryStore.cs) |
| Suspend, resume, constrain and account-change handling | [`LifecycleCoordinator`](../src/NetRumble.Game/LifecycleCoordinator.cs), [`PlmInterop`](../src/NetRumble.Platform.GameCore/Interop/PlmInterop.cs) |
| Activity publishing, presence and join-from-guide invites | [`GameCoreActivityService`](../src/NetRumble.Platform.GameCore/Services/GameCoreActivityService.cs), [`PresencePublisher`](../src/NetRumble.Game/PresencePublisher.cs), [`InviteRouter`](../src/NetRumble.Game/InviteRouter.cs) |
| Connectivity detection before online play is offered | [`IPlatformRuntime`](../src/NetRumble.Platform/Runtime/IPlatformRuntime.cs), [`MainMenuScreen`](../src/NetRumble.Game/UI/Screens/MainMenuScreen.cs) |
| Controller association and loss | [`ControllerWatcher`](../src/NetRumble.Game/ControllerWatcher.cs), [`ControllerDisconnectOverlay`](../src/NetRumble.Game/UI/ControllerDisconnectOverlay.cs) |
| The system keyboard and other platform UI | [`GameCorePlatformUiService`](../src/NetRumble.Platform.GameCore/Services/GameCorePlatformUiService.cs), [`SystemKeyboard`](../src/NetRumble.Game/UI/Elements/SystemKeyboard.cs) |
| Build-version refusal and the peer trust boundary | [`NRProtocol`](../src/NetRumble.Platform/Networking/NRProtocol.cs), [`MessageReader`](../src/NetRumble.Core/Net/Wire/MessageReader.cs) |
| Dispatching selected async completions to the frame pump | [`PumpDispatcher`](../src/NetRumble.Platform.GameCore/Services/PumpDispatcher.cs) |

## Follow sign-in

The acquire-user screen waits for runtime initialization, then asks
`GameCoreIdentityService` for the system's default XBOX user. After acquiring it, the
identity service asks `GameCoreOnlineSession` to exchange that user for a PlayFab entity.
This excerpt from `PlayFabOnlineServices.SignInWithXboxUserAsync` shows the GDK.NET
calls, not the surrounding ownership and error handling:

```csharp
var localUser = PlayFabLocalUser.CreateForXboxUser(config, user);
var login = await localUser.LoginAsync(createAccount, token).ConfigureAwait(false);
```

Account creation is allowed only for an eligible age group. A failed PlayFab exchange
does not undo a successful XBOX sign-in, so the presence of a gamertag is not proof of
a usable Lobby/Party identity. See the [sign-in walkthrough](walkthroughs.md#sign-in-to-xbox-and-playfab).

## Implementation status

The title uses the **simplified user model** on PC and XBOX. All four package
configurations set `AdvancedUserModel` to `false`; GDK identity acquisition uses only
`UserAddOptions.AddDefaultUserSilently`. The system supplies one default user for the
process lifetime and owns sign-in and user-change relaunches. The title has no account
picker, in-session account switching, or activity-cache reset on account replacement.
Profile refresh, reauthentication, privilege changes and sign-out lifecycle notifications
remain separate from switching accounts and still apply to that one user.

See the [XUserAddAsync simplified-model guidance](https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/system/xuser/functions/xuseraddasync).
For a loose-deployed PC build with no user, the runtime may terminate the process and
launch the PC bootstrapper for sign-in; the next launch uses the established default user.
This is system-owned behavior, not a title-driven picker.
The native-independent `--identity-checks-only` PlatformSpike mode checks the four
manifests, silent-only user acquisition, absence of the picker API and activity
account-switch subscription, GDK-only composition and required sign-in.

"Wired" means the code path is complete and compiles. "Proven" means it has been observed
working. Anything that needs a signed-in account against this sample's own title has not
been proven by anyone without access to that title and its sandbox.

| Service | GDK provider | Proven |
|---|---|---|
| `IPlatformRuntime` | Complete. Lifecycle events bound directly because XBOX GDK.NET wraps none of them. Device kind and a connectivity hint are read from the runtime. | Connectivity hint observed answering. Lifecycle callbacks need XBOX Series X\|S hardware. |
| `IIdentityService` | XBOX sign-in complete. Account changes surface through `AccountChanged`, controller associations through `TracksControllerAssociations`, `HasAssociatedController` and `ControllerAssociationChanged`. One native PlayFab login supplies the entity for Party and Lobby. | XBOX sign-in previously proven; the native PlayFab migration needs authenticated console verification. Account-change and association events need XBOX Series X\|S hardware. |
| `IGameSaveService` | GDK.Net `GameSaveFiles.GetFolderWithUiAsync` and file I/O (`XGameSaveFiles`), independent of PlayFab configuration | Cached-folder file operations and account isolation checked locally. Cross-device roaming remains unverified. |
| `IPrivilegeService` | Complete for the two privileges this title gates on, multiplayer and communications | Needs a signed-in account |
| `IAchievementService` | Complete | No unlock round trip has been observed |
| `IActivityService` | Activation, multiplayer activity and presence are wired and driven from the game | Earlier console invite tests predate the native PlayFab migration. Presence and the current end-to-end invite path need a live pass. |
| `IPrivacyService` | Complete, using per-target permission checks rather than the batch API | Needs a signed-in account |
| `IModerationService` | Complete | Needs a signed-in account |
| `ISocialService` | Complete. It re-queries rather than tracking a live group, so it raises no change event. | Needs a signed-in account |
| `IGameInputService` | A null implementation. It reports no devices and raises no events; MonoGame reads input. | Not applicable |
| `IPlatformUiService` | Complete | Interactive calls unverified end to end |
| `IPartyService` | Party networking, Lobby join-code publishing, messaging and chat are wired | Earlier console join and invite tests predate the native PlayFab migration; the current path needs an authenticated two-device pass. |

Run the native-independent save-file checks with
`dotnet run --project tools\NetRumble.PlatformSpike -- --gamesave-files-checks-only`.
They exercise temporary test folders, not the signed-in user's platform save folder.
These checks also cover pump-delivered results, suspension cancellation and directory
invalidation. The GDK owns synchronization outside the title process; the backend only
acquires the per-user folder and performs file I/O.
Run `dotnet run --project tools\NetRumble.GameSaveChecks -c Release` for the
game-layer initial-load, first-run settings and stale-snapshot regressions. Neither suite
requires native sign-in or initializes a graphics device.

### Why `IGameInputService` is a null implementation

The game reads pads directly through MonoGame `GamePad` and `Keyboard` in `LocalInput` and
`UiInput`, so it needs no separate input service. Controller *association* went onto
`IIdentityService` instead, because which controller belongs to which player is a fact
about an account rather than a reading from a device.

### Party teardown and pump ownership

The six native Party teardown waits share one six-second budget. The deadline can
resume teardown on a worker thread, so manager access, pending-wait registration,
reset and frame pumping share the same gate. A reset detaches its pending waits
atomically. When reset or disposal reenters from a pump callback, native disposal
and detached-wait cancellation finish after native state-change enumeration exits,
not inside it. Cleanup outside a pump completes directly without requiring another
frame. Every reset retires its old native manager, including a host-closing reset
that cancels an already pending leave. Session revisions prevent an older leave
from tearing down a newer session.
Host/join attempts retain their original revision through native continuations
and failure/cancellation cleanup; an old attempt cannot adopt a replacement
session's revision by starting a new parameterless leave.
Connection and authentication completion waits are registered together before the
pump can observe either result, so early authentication cannot be discarded while
the caller is still waiting for the connection.
If a hosted lobby response arrives after its Party session retires, that returned
lobby is closed and left by its own ID instead of being adopted or leaving the
replacement session. Cleanup failures are logged and returned to the caller.

The six-second budget is not an end-to-end leave deadline: PlayFab lobby close and
leave requests retain their separate request timeouts. Run the native-independent
deadline, active-pump, reentrant-reset and stale-session checks with
`dotnet run --project tools\NetRumble.PlatformSpike -c Release -- --playfab-checks-only`.
The apphost's `--playfab-native-smoke` mode additionally exercises local native
Party manager initialization, pumping and deadline cleanup without authentication
or service writes; it does not prove connected play or console behavior.

## Peer trust

The GDK provider accepts a "welcome" control packet only from the actual host entity,
only while a join is in flight, and only when the claimed peer id is valid. Without those
checks any player in the session could take authority over another.

`HostAsync`, `JoinAsync` and `JoinByConnectionStringAsync` all take an explicit
`protocolVersion` rather than reading a static, so no layer can disagree about which
version it is running. An absent version counts as a mismatch and the join is refused
before a Party endpoint is created.

See [Design notes](design-notes.md#network-trust-boundary) for the full reasoning.
