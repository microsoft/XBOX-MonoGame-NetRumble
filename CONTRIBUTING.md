# Contributing

Pull requests are welcome. This page covers how to get a change accepted.

## The acceptance gate

This repository has no automated gameplay or netcode test suite. The gate for any change to
the platform boundary or the multiplayer core is `tools\NetRumble.PlatformSpike`, which
has to pass in both its default and `gamecore` modes.

```powershell
dotnet build NetRumble.slnx /p:NoWarn=NU1603
dotnet run --project tools\NetRumble.PlatformSpike
dotnet run --project tools\NetRumble.PlatformSpike -- --platform=gamecore
```

`--platform=offline` reports three failures on a machine with the GDK installed, and that
is expected. See
[the offline spike](docs/troubleshooting.md#the-offline-spike-reports-three-failures-on-a-gdk-machine).

## Write the reasoning down

If you close something in [Known gaps](docs/known-gaps.md), add the reasoning to
[Design notes](docs/design-notes.md). A fix without its reasoning tends to come back.

The same applies to a decision to *not* do something. The
[Deliberate non-goals](docs/known-gaps.md#deliberate-non-goals) section exists so that the
next person does not spend a day re-solving a question that was already settled.

## Respect the platform boundary

`NetRumble.Core` and `NetRumble.Game` reference `NetRumble.Platform` only. If a change
needs a native type, an `HRESULT` or an SDK structure in game code, it belongs behind the
provider boundary instead. [Platform abstraction](docs/platform-abstraction.md) describes
the four invariants a provider has to honour.

A `#if GDKX` in game code is a signal that something belongs behind that boundary. The
constant is defined for the XBOX Series X|S build and nothing uses it.

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

Read [Known gaps](docs/known-gaps.md) and [Troubleshooting](docs/troubleshooting.md) first.
Between them they cover most of what is currently missing or environment specific.

When you open an issue, include the provider mode you ran in, the exact command line, and
the output of the platform spike if the problem touches sign-in, the transport or saving.
