# Known gaps

These are sample limitations and unverified GDK and PlayFab flows that affect the
learning journey. They are not a certification assessment. The
[feature matrix](feature-support.md) describes intended behavior, and the
[manual test plan](manual-test-plan.md) explains how to record observed results.

## Current limitations

Remote players do not show service-verified gamertags. The title does not publish a
PlayFab display name, so PlayFab surfaces show entity IDs.

## Deliberate non-goals

The sample is a single four-player free-for-all deathmatch. It has no additional game
modes, leaderboards, host migration or join in progress.

## Never observed end to end

These paths are unverified, not necessarily broken:

- **Console lifecycle and user removal.** Suspend, resume, constrain and unconstrain
  notifications and the state commit when the signed-in user is removed have not been
  observed on XBOX Series X|S hardware.
- **Achievements and rich presence.** Achievement reporting is checked in a headless
  harness, but an unlock has not been observed on XBOX. Presence IDs have been emitted
  locally but have not been observed on a real profile. The sample's service configuration
  ID and rich presence string IDs still need confirmation against the live title;
  initialization alone does not validate them. See [Configuration](configuration.md#xbox-title-and-service-configuration).
- **Cloud saves.** File operations and account isolation are checked in temporary folders.
  Authenticated folder acquisition, system conflict UI, resume synchronization and
  roaming between two devices still need a live test.
- **Multiplayer.** Console join and invite tests predate the native PlayFab migration.
  Native authentication, connected Lobby creation and join, token renewal and host cleanup
  need an authenticated two-device test. Reliability tests so far use loopback and a local
  adapter rather than real latency, jitter or packet loss.
- **Console runtime.** The XBOX Series X|S build compiles and links, but booting,
  initializing the graphics device and rendering have not been observed on hardware.
