# Platform services

Every service behind `IPlatformProvider`, where it is implemented, and how far it has been
proven. Use this page to find the file you want to read.

The source is the detailed documentation. Each file listed below carries XML comments that
explain the decisions behind it.

## Where each integration lives

| Integration | Source |
|---|---|
| GDK sign-in and the exchange for a PlayFab identity | `src/NetRumble.Platform.GameCore/Services/GameCoreIdentityService.cs`, `src/NetRumble.Platform.PlayFab/PlayFabAuthClient.cs` |
| Sign-in progress screen and offline fallback | `src/NetRumble.Game/UI/Screens/AcquireUserScreen.cs` |
| PlayFab Party as the match transport | `src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs`, `src/NetRumble.Core/Net/PartyMatchNetwork.cs` |
| Join-code discovery with PlayFab Lobby | `src/NetRumble.Platform.PlayFab/PlayFabLobbyClient.cs` |
| A second transport behind the same interface | `src/NetRumble.Platform.Lan/LanPartyService.cs`, `src/NetRumble.Platform.Lan/LanLink.cs` |
| Multiplayer and communications privilege checks | `src/NetRumble.Platform.GameCore/Services/GameCorePrivilegeService.cs` |
| Per-player mute, block and avoid enforcement | `src/NetRumble.Platform.GameCore/Services/GameCorePrivacyService.cs` |
| Text verification before player-authored content is shown | `src/NetRumble.Platform.GameCore/Services/GameCoreModerationService.cs` |
| Party voice and speech-to-text captions | `src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs`, `src/NetRumble.Game/UI/Elements/SttOverlay.cs` |
| One-shot and incremental achievement progress | `src/NetRumble.Platform.GameCore/Services/GameCoreAchievementService.cs`, `src/NetRumble.Game/Profile/AchievementTracker.cs` |
| Cloud save for settings and achievement stats | `src/NetRumble.Platform.GameCore/Services/GameCoreGameSaveService.cs`, `src/NetRumble.Platform.PlayFab/PlayFabGameSaveService.cs`, `src/NetRumble.Game/Profile/CloudSettingsSync.cs` |
| Suspend, resume, constrain and account-change handling | `src/NetRumble.Game/LifecycleCoordinator.cs`, `src/NetRumble.Platform.GameCore/Interop/PlmInterop.cs` |
| Activity publishing, presence and join-from-guide invites | `src/NetRumble.Platform.GameCore/Services/GameCoreActivityService.cs`, `src/NetRumble.Game/PresencePublisher.cs`, `src/NetRumble.Game/InviteRouter.cs` |
| Connectivity detection before online play is offered | `src/NetRumble.Platform/Runtime/IPlatformRuntime.cs`, `src/NetRumble.Game/UI/Screens/MainMenuScreen.cs` |
| Controller association and loss | `src/NetRumble.Game/ControllerWatcher.cs`, `src/NetRumble.Game/UI/ControllerDisconnectOverlay.cs` |
| The system keyboard and other platform UI | `src/NetRumble.Platform.GameCore/Services/GameCorePlatformUiService.cs`, `src/NetRumble.Game/UI/Elements/SystemKeyboard.cs` |
| Build-version refusal and the peer trust boundary | `src/NetRumble.Platform/Networking/NRProtocol.cs`, `src/NetRumble.Core/Net/Wire/MessageReader.cs` |
| Marshalling async completions onto the frame-pumped thread | `src/NetRumble.Platform.GameCore/Services/PumpDispatcher.cs` |

## Implementation status

"Wired" means the code path is complete and compiles. "Proven" means it has been observed
working. Anything that needs a signed-in account against this sample's own title has not
been proven by anyone without access to that title and its sandbox.

| Service | Offline provider | GDK provider | Proven |
|---|---|---|---|
| `IPlatformRuntime` | Complete | Complete. Lifecycle events bound directly because XBOX GDK.NET wraps none of them. Device kind and a connectivity hint are read from the runtime. | Connectivity hint observed answering. Lifecycle callbacks need XBOX Series X\|S hardware. |
| `IIdentityService` | Complete | XBOX sign-in complete. Account changes surface through `AccountChanged`, controller associations through `TracksControllerAssociations`, `HasAssociatedController` and `ControllerAssociationChanged`. PlayFab entity exchange and REST session-ticket exchange are wired. | Sign-in proven. Account-change and association events need XBOX Series X\|S hardware. |
| `IGameSaveService` | Complete, local | Connected storage (`XGameSave`) by default, PlayFab user data behind `/p:NetRumbleCloudSave=PlayFab` | Local tier proven. Neither cloud tier has been round-tripped between two devices. |
| `IPrivilegeService` | Complete | Complete for the two privileges this title gates on, multiplayer and communications | Needs a signed-in account |
| `IAchievementService` | Complete | Complete | No unlock round trip has been observed |
| `IActivityService` | Complete | Activation, multiplayer activity and presence are wired and driven from the game | Join and invite flows proven between two XBOX Series X\|S consoles. Presence has not been seen on a real profile. |
| `IPrivacyService` | Complete | Complete, using per-target permission checks rather than the batch API | Needs a signed-in account |
| `IModerationService` | Complete | Complete | Needs a signed-in account |
| `ISocialService` | Complete | Complete. It re-queries rather than tracking a live group, so it raises no change event. | Needs a signed-in account |
| `IGameInputService` | Unused | A null implementation in every provider. It reports no devices and raises no events. | Not applicable |
| `IPlatformUiService` | Complete | Complete | Interactive calls unverified end to end |
| `IPartyService` | Refuses | Party networking, Lobby join-code publishing, messaging and chat are wired | Transport proven over loopback and LAN. No run under real network conditions. |

### Why `IGameInputService` is a null implementation

The game reads pads directly through MonoGame `GamePad` and `Keyboard` in `LocalInput` and
`UiInput`, so it needs no separate input service. Controller *association* went onto
`IIdentityService` instead, because which controller belongs to which player is a fact
about an account rather than a reading from a device.

## Peer trust

Peer identity is authenticated on both transports.

- The GDK provider accepts a "welcome" control packet only from the actual host entity,
  only while a join is in flight, and only when the claimed peer id is valid. Without those
  checks any player in the session could take authority over another.
- The LAN transport carries a per-link session token issued during the connection
  handshake and required on every link-scoped datagram.

`HostAsync`, `JoinAsync` and `JoinByConnectionStringAsync` all take an explicit
`protocolVersion` rather than reading a static, so no layer can disagree about which
version it is running. An absent version counts as a mismatch and the join is refused
before a Party endpoint is created.

See [Design notes](design-notes.md#network-trust-boundary) for the full reasoning, and
[Known gaps](known-gaps.md#networking) for what the LAN transport still does not do.
