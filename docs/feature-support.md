# Feature support

What every platform feature in the sample is built to do, and the environments it runs
in. This table describes intended behaviour rather than a record of live test results.
Service availability, title access and account policy still apply.

[Known gaps](known-gaps.md) records which of these have been observed working and which
have only ever compiled.

## Platform feature matrix

| Feature | XBOX on PC | XBOX Series X\|S | LAN (no GDK, no account) | Offline |
|---|---|---|---|---|
| XBOX identity exchanged for a PlayFab identity | XBOX linked | XBOX linked | Local player only | No online identity |
| PlayFab Lobby discovery and Party transport | Yes | Yes | Real UDP transport with a five-character join code | Practice only |
| PlayFab Party voice and speech-to-text captions | Subject to XBOX policy | Subject to XBOX policy | Text only, no audio | Unavailable |
| XBOX privileges, privacy, string verification, reporting | Yes | Yes | Bypassed | Unavailable |
| XBOX friends, activity, invites, recent players | Yes | Yes | Unavailable, because there is no platform surface to carry a join string | Unavailable |
| XBOX achievement reporting | Yes | Yes | Local counters only, no XBOX award | Local counters only |
| Rich presence | Yes | Yes | Unavailable | Unavailable |
| Roaming cloud save for settings and achievement stats | Connected storage (`XGameSave`) | Connected storage (`XGameSave`) | PlayFab user data with `--playfab-title=<id>` | Local file only |
| Match history and counters | Local, per user | Local, per user | Local | Local |
| Connectivity gating before online play is offered | Platform hint when known | Platform hint when known | Not gated | Not offered |
| Lifecycle and controller detection | Focus and device detection | Suspend, resume, constrain, association detection | Desktop behaviour | Depends on platform |

## Scope of the game itself

The sample is a single four-player free-for-all deathmatch. It deliberately has:

- no game modes;
- no leaderboards;
- no host migration and no join in progress.

Discovery uses **PlayFab Lobby**, not PlayFab Matchmaking queues or tickets, because the
sample favours a low-latency demonstration over a queue that has to fill.

## Cloud save tiers

The GDK provider saves to GDK connected storage (`XGameSave`), so settings, achievement
stats and match history roam together and the platform resolves conflicts.

The PlayFab user-data tier it replaced is still selectable with
`/p:NetRumbleCloudSave=PlayFab`, and it is what the desktop provider uses when no GDK is
present, because connected storage needs a GDK user handle.

Neither tier has been round-tripped between two devices. See
[Known gaps](known-gaps.md#never-observed-end-to-end).

## Known rough edges

The controller glyphs in this repository are placeholders rather than the certification
compliant art from the XBOX design guidance, and the store logos referenced by the
packaging configs are generated stand-ins. Replace both before taking anything from this
sample into a submission.
