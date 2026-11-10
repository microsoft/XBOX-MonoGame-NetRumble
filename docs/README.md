# XBOX MonoGame NetRumble documentation

Everything specific to this port. For general GDK, PlayFab and GameInput material, start
with the [MonoGame GDK documentation](https://aka.ms/XBOXMonoGameDocs) and the
[GDK documentation on Microsoft Learn](https://learn.microsoft.com/en-us/gaming/gdk/?view=gdk-2604).
For MonoGame itself, see the [MonoGame documentation](https://docs.monogame.net/).

## Start here

| Page | Contents |
|---|---|
| [Getting started](getting-started.md) | Prerequisites, first build, first run, and what title access requires |
| [Configuration](configuration.md) | GDK.NET, XBOX and PlayFab title settings, account model and saves |
| [Walkthroughs](walkthroughs.md) | Follow an action through source and APIs to its expected outcome |
| [Glossary](glossary.md) | Terms used throughout the GDK and PlayFab guides |

## Platform integration

| Page | Contents |
|---|---|
| [Multiplayer](multiplayer.md) | Lobby discovery, Party transport, host/join and cleanup |
| [Platform abstraction](platform-abstraction.md) | The `IPlatformProvider` boundary and the four invariants a provider must honour |
| [Platform services](platform-services.md) | Every service, its implementation and its verification status |
| [Feature support](feature-support.md) | Intended feature availability and the scope of the game |

## Building and testing

| Page | Contents |
|---|---|
| [Run modes](run-modes.md) | GDK startup, Practice, an unattended run and the platform spike |
| [Manual test plan](manual-test-plan.md) | Acceptance cases and how to record pass, fail or untested |
| [Windows packaging](windows-packaging.md) | Building, packaging and installing on a PC |
| [Building for XBOX Series X\|S](xbox-console-build.md) | High-level overview of the XBOX Series X\|S build. Available to XBOX development partners only. |

## Troubleshooting and reference

| Page | Contents |
|---|---|
| [Design notes](design-notes.md) | Why the sample is shaped the way it is, plus the findings that cost real time |
| [Known gaps](known-gaps.md) | What is missing, what has never been observed working, and what is missing on purpose |
| [Troubleshooting](troubleshooting.md) | Symptoms, causes and fixes |
| [Vendored Party binaries](../third_party/party/README.md) | What is vendored under `third_party/party/`, and which target it is correct for |

## Reading the source

The [walkthroughs](walkthroughs.md) connect player actions to source and native APIs.
[Platform services](platform-services.md) is the broader source index; XML comments in
the implementation explain ownership and ordering. The test plan describes expected
behavior, while [Known gaps](known-gaps.md) records outstanding evidence.
