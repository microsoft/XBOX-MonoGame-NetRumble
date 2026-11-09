# Feature support

What every platform feature in the sample is built to do, and the environments it runs
in. This table describes intended behaviour rather than a record of live test results.
Service availability, title access and account policy still apply.

[Known gaps](known-gaps.md) records which of these have been observed working and which
have only ever compiled.

## Platform feature matrix

| Feature | XBOX on PC | XBOX Series X\|S |
|---|---|---|
| XBOX identity exchanged for a PlayFab identity | XBOX linked | XBOX linked |
| PlayFab Lobby discovery and Party transport | Yes | Yes |
| PlayFab Party voice and speech-to-text captions | Subject to XBOX policy | Subject to XBOX policy |
| XBOX privileges, privacy, string verification, reporting | Yes | Yes |
| XBOX friends, activity, invites, recent players | Yes | Yes |
| XBOX achievement reporting | Yes | Yes |
| Rich presence | Yes | Yes |
| Roaming cloud save for settings and achievement stats | Connected storage (`XGameSaveFiles`) | Connected storage (`XGameSaveFiles`) |
| Match history and counters | Local, per user | Local, per user |
| Connectivity gating before online play is offered | Platform hint when known | Platform hint when known |
| Lifecycle and controller detection | Focus and device detection | Suspend, resume, constrain, association detection |

Both platforms require GDK sign-in, including for single-player Practice.

## Scope of the game itself

The sample is a single four-player free-for-all deathmatch. It deliberately has:

- no game modes;
- no leaderboards;
- no host migration and no join in progress.

Discovery uses **PlayFab Lobby**, not PlayFab Matchmaking queues or tickets, because the
sample favours a low-latency demonstration over a queue that has to fill.

## Cloud save

The GDK provider saves to GDK connected storage (`XGameSaveFiles`), so settings, achievement
stats and match history roam together and the platform resolves conflicts.
GDK.Net's `GameSaveFiles.GetFolderWithUiAsync` acquires the per-user folder before the
first file operation. Each save is a file named by its key directly in that directory.
Writes use temporary files and atomic replacement; account changes invalidate queued work.
The GDK handles save synchronization outside the title process. The title only acquires
the correct user's directory and reads or writes files; it has no synchronization
callbacks, reconciliation barriers or connectivity retry queue. Account changes,
suspension and resume invalidate the cached directory. The next file operation
reacquires it before accessing files. Public cloud-operation completions are delivered
through the runtime pump.

Cloud save has not been round-tripped between two devices. See
[Known gaps](known-gaps.md#never-observed-end-to-end).

## Known rough edges

The controller glyphs in this repository are placeholders rather than the certification
compliant art from the XBOX design guidance, and the store logos referenced by the
packaging configs are generated stand-ins. Replace both before taking anything from this
sample into a submission.
