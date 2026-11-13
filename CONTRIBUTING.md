# Contributing

Pull requests are welcome. This page covers how to get a change accepted.

## The acceptance gate

The platform spike exercises the provider boundary and multiplayer core. The default and
`gamecore` modes select the same GDK provider. Its broad run prints individual PASS, FAIL
and SKIP results without aggregating every failure into its exit code, so review the
output and record unavailable checks as untested.

```powershell
dotnet build NetRumble.slnx /p:NoWarn=NU1603
dotnet run --project tools\NetRumble.PlatformSpike
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=gamecore
```

For focused native-independent checks and a game-layer save check, see the
[manual test plan](docs/manual-test-plan.md#automated-aids). Live sign-in, networking,
roaming and hardware behavior still need the appropriate accounts and devices.

## Write the reasoning down

If you close something in [Known gaps](docs/known-gaps.md), add the reasoning to
[Design notes](docs/design-notes.md). A fix without its reasoning tends to come back.

The same applies to a decision to *not* do something. The
[Deliberate non-goals](docs/known-gaps.md#deliberate-non-goals) section exists so that the
next person does not spend a day re-solving a question that was already settled.

## Respect the platform boundary

`NetRumble.Core` references the platform contract without a concrete provider. The game
also references GameCore for the composition root, but gameplay and UI consume the
provider interfaces. If a change needs a native type, an `HRESULT` or an SDK structure in
game code, it belongs behind that boundary instead.
[Platform abstraction](docs/platform-abstraction.md) describes the four invariants.

`#if GDKX` is used for target-specific startup and UI. Keep service APIs and native
handles behind the provider boundary rather than adding platform calls to game code.

## Keep the documentation public-safe

This sample is written for developers outside Microsoft. When you add or change
documentation:

- Do not reproduce partner-only material. XBOX Series X|S setup, development kit operation,
  certification requirements and the console portions of the GDK are covered by NDA. Refer
  to them at a high level, then point readers at the
  [XBOX Developer Program](https://developer.microsoft.com/en-us/games/) and the partner
  documentation, which is authoritative and stays current.
- Do not paste internal paths, machine names, internal repository names or internal dates.
- Do not claim a feature works if nobody has watched it work. Say what was observed and
  what was not, and record the gap in [Known gaps](docs/known-gaps.md).

## Style

- Write headings as short direct labels.
- Do not use em dashes, and do not use a hyphen as a parenthetical dash. A comma, a colon,
  parentheses or a second sentence all read better.
- Write the brand as XBOX in prose. Leave file names, identifiers and URLs alone.
- Prefer a plain statement of fact over a rhetorical contrast.

## Reporting a problem

Check [Known gaps](docs/known-gaps.md) for platform limitations and
[Troubleshooting](docs/troubleshooting.md) for symptoms and fixes.

When you open an issue, include the provider mode you ran in, the exact command line, and
the output of the platform spike if the problem touches sign-in, the transport or saving.
