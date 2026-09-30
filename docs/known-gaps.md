# Known gaps

What this sample does not do yet, what nobody has observed working, and what is missing on
purpose. Read this before filing a bug.

Items under [Deliberate non-goals](#deliberate-non-goals) are decisions rather than debt.
Please do not "fix" them without raising the decision first.

## Certification and platform behaviour

The four requirements that block certification on XBOX Series X|S are implemented and are
covered by automated checks, but two of them cannot be proven without XBOX Series X|S
hardware.

| Area | State |
|---|---|
| Process lifecycle: suspend, resume, constrain, unconstrain | Implemented and bound to real platform notifications. Never observed firing on hardware, and a real suspend has never been survived. |
| Online play gated on the platform connectivity hint | Implemented and verified on driven runs |
| Controller loss surfaced to the player | Implemented and verified on driven runs |
| State committed when the signed-in user is removed | Implemented. Needs hardware to observe, because no platform user-change notification has fired here. |

Haptics are absent and are not required.

## Player-visible features

- **No focus ring.** Focused rows change colour, but there is no explicit focus indicator
  and no scroll-into-view for a focused control.
- **No power-up frequency slider.** The simulation half is complete. The drop interval and
  the active cap are both driven by `World.PowerUpFrequency`. The options screen has no
  widget for it yet.
- **Remote players do not show service-verified gamertags, and the title never publishes a
  PlayFab display name.** Nothing calls `UpdateUserTitleDisplayName`, and PlayFab resolves
  no name on your behalf, so PlayFab surfaces show raw entity ids.
- **Settings and match history are not separated per user on shared local storage.**
- **Online play is not re-checked against live privilege state at the point of use.** The
  check happens earlier in the flow.
- **A lobby-code search does not stop after a single lookup**, and lobby focus is not
  restored after a roster modal closes.
- **The OS cursor is not restored while a gameplay popup covers the reticle.**
- **The main menu shows no build identifier**, which is what makes a build-mismatch refusal
  diagnosable by the person who hit it.
- **Menus are not on the XBOX green theme.**

## Networking

- **The LAN transport is not internet safe.** Protocol version 2 added a per-link session
  token, which raises the bar from "know two public constants" to "read this link's
  traffic". It is not encryption and it stops no on-path observer. There is also no
  congestion control and no RTT estimation. Either address those properly or keep the
  transport inside its stated scope, which is development and same-network testing.
- **`LoginWithCustomID` must never reach a shipping build.** It authenticates nobody. It is
  gated behind `SignInOptions.DeveloperCustomId` today, and that gate needs enforcing
  rather than documenting.
- **Privacy permissions are checked one player at a time.** The batch API returns nested
  variable-stride unmanaged arrays and was judged the riskier binding. See
  [Design notes](design-notes.md#prefer-the-shape-you-can-marshal-correctly). Revisit only
  if per-roster-refresh latency turns out to matter.

## Configuration and assets

These concern this sample's own title configuration and assets rather than its code, so
they are fixed in Partner Center or in this repository. Skip this section unless you are
working on the sample itself.

- **Confirm the service configuration id.** The value the title initializes with is
  derived rather than read from configuration, and the initialization call validates only
  that the string is well formed. Join codes, invites and presence all work, which is
  strong indirect evidence that it is correct, but nothing has confirmed it. Check this
  first if achievements misbehave, because achievements are the one surface behind it that
  has never been observed working.
- **Confirm the rich presence string ids.** They were inferred from the naming convention
  used by the Godot version of this sample. An id the service does not define is rejected
  service-side rather than caught locally. They are three constants, so correcting them is
  trivial. This depends on the same unconfirmed configuration id.
- **Set the PlayFab statistic's aggregation method in Game Manager.** PlayFab creates a
  statistic on first write with last-value-wins aggregation, so an unconfigured high-score
  table silently ranks the most recent score instead of the best one. This cannot be done
  from code.
- **Replace the store logos.** The files in `storelogos\` are generated stand-ins.
- **Replace the controller glyphs.** The glyphs in this repository are obvious
  placeholders. Certification-compliant controller art comes from the official XBOX design
  guidance and is not redistributable, so it is not in this repository.

## Never observed end to end

Nothing in this list should be read as broken. It has simply never been watched working,
usually because it needs a signed-in account, XBOX Series X|S hardware or a second device.

- **Audio.** It has never been verified working at any point during this port.
- **A dynamic-light pass on space objects.** The Godot original lights asteroids and
  power-ups from nearby bolts and explosions using `Light2D`, which MonoGame has no
  equivalent for. Only the base pass is ported. With twenty weapons distinguished largely
  by colour and thirty-two pickups sharing three silhouettes, that lighting is part of how
  the arsenal reads, so this is a gap rather than an omission.
- **A mine drawn lit.** The automated run never picks up the power-up that grants them.
- **The end-of-match results screen.** The automated run never reaches it, because the
  match timer is ten minutes.
- **An achievement unlocking on XBOX.** The ten achievements are wired to gameplay and
  verified against a headless harness, but no unlock round trip has been seen. It needs a
  signed-in account on XBOX Series X|S hardware and it sits behind the unconfirmed service
  configuration id.
- **Rich presence showing on a real XBOX profile.** The publisher is verified to emit the
  right id at the right moment, but no id has reached the service.
- **Cloud save roaming between two machines.** Settings and achievement stats go to
  connected storage on the GDK provider, but no part of that path has run against a real
  save provider. An absent-blob first run has only ever been reasoned about, and roaming
  needs a second device. The PlayFab tier has been proven against a fake cloud tier only.
- **Behaviour under real network conditions.** Join and invite flows are confirmed between
  two XBOX Series X|S consoles, which exercises the REST and lobby layers rather than the
  gameplay transport. The three reliability tiers have only ever run on one machine over
  loopback and a local adapter, which exercises no latency, no jitter and no genuine packet
  loss.
- **XBOX Series X|S output at runtime.** That flavour compiles and links. Whether the title
  boots, initialises the graphics device and renders has not been established here.

## Deliberate non-goals

- The game is a single four-player free-for-all deathmatch. There are no game modes, no
  leaderboards, no host migration and no join in progress.
- `velocity_min_threshold` is unused in the Godot original as well. The parity target is
  the Godot version, not the older C++ sample, so reintroducing the floor would be a
  gameplay change.
- The starfield seeds .NET `Random` rather than the Godot original's PCG32 generator, so
  individual stars land in different places. Density and brightness are identical and
  nothing depends on star positions.
- `GameplayEventType.ShipSpawned` and `RocketTrail` are never raised in either build.
- Positional sound panning is an approximation. It diverges only under a zoom or a
  letterboxed viewport, and this game has neither.
- Tuning is compile-time. If a data overlay is ever added it has to *populate* the existing
  instances rather than deserialize fresh ones.
- `MonoGamePlatform=WindowsDX12` is preview quality. Nothing depends on DX12 specifically,
  so the platform can be changed if a blocker appears.
