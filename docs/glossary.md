# Glossary

Terms used by the walkthroughs, in the context of this sample.

| Term | Meaning here |
|---|---|
| Microsoft GDK | The Game Development Kit and runtime APIs used for XBOX platform integration. PC and console tooling have different access requirements. |
| Gaming Runtime | The native runtime that must be initialized before the GDK/PlayFab native services used here. |
| XBOX GDK.NET / GDK.Net | The managed C# projection used by this sample to call GDK, PlayFab and Party APIs. It is a source project reference, not the MonoGame framework. |
| Provider | An `IPlatformProvider` implementation supplying identity, networking, saves and other services. This branch uses the GDK-backed GameCore provider. |
| Capability | A provider's indication that an implemented path and its local dependencies are available. It is not proof of a user's permission or a successful service call. |
| XBOX title ID | The XBOX product identity in the game configuration. It is distinct from a PlayFab title ID. |
| PlayFab title ID | The identifier of the PlayFab backend selected by command line, environment or the sample default. It is not a secret key. |
| Sandbox | An XBOX service environment in which a title and authorized test accounts are provisioned. |
| SCID | XBOX Services service configuration ID. It identifies service configuration such as achievements and presence for a title; it is not a PlayFab title ID. |
| Simplified user model | The GDK user model in which the system supplies one default user and owns sign-in and user-change relaunches. This title does not provide an account picker. |
| XBOX user / XUID | The signed-in platform account and its XBOX user identifier. The native user handle remains inside the provider. |
| PlayFab entity | The PlayFab identity returned by login and used by Lobby and Party. It is additional to the XBOX identity in GameCore. |
| Entity token | A credential used to access PlayFab services. Treat it as sensitive and do not put it in logs, examples or searchable lobby data. |
| Lobby | PlayFab's discovery and membership service. The GameCore provider publishes a join code and connection data here; Lobby is not the match transport. |
| Party | PlayFab's network and chat service. It connects authenticated users and endpoints and carries game messages and communications. |
| Endpoint | A Party network address associated with a local user. The sample maps endpoints to its own integer game peer IDs. |
| Join code | A five-character discovery value used to search PlayFab Lobby. It is not an authentication credential. |
| Connection string | Connection data carried by an XBOX activity/invite so a recipient can attempt to join. It can include a host token; share it only with intended participants and keep it out of public logs. |
| Pump | Per-frame processing of queued platform work and native Lobby/Party state changes. Some GDK.NET tasks can also complete on a thread-pool thread. |
| Privilege | An account-level permission for an activity such as multiplayer or communications. A failed check is not a grant. |
| Privacy | Per-player communication restrictions, including platform mute/avoid lists and permission checks. |
| Connected storage / XGameSaveFiles | XBOX account-bound storage. GDK.NET obtains a user folder and the title uses file I/O while the platform manages synchronization. |
| NativeAOT | Ahead-of-time compilation of the console game's managed code into native output. Reflection and serialization choices must account for trimming. |

Continue with [Configuration](configuration.md) or the
[GDK and PlayFab walkthroughs](walkthroughs.md).
