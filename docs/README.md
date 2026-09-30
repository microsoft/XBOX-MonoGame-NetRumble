# XBOX MonoGame NetRumble documentation

Everything specific to this port. For general GDK, PlayFab and GameInput material, start
with the [MonoGame GDK documentation](https://aka.ms/XBOXMonoGameDocs) and the
[GDK documentation on Microsoft Learn](https://learn.microsoft.com/en-us/gaming/gdk/?view=gdk-2604).
For MonoGame itself, see the [MonoGame documentation](https://docs.monogame.net/).

## Start here

| Page | Contents |
|---|---|
| [Getting started](getting-started.md) | Prerequisites, first build, first run, and what online access requires |
| [Run modes](run-modes.md) | Desktop, offline, LAN, a separate PlayFab title, unattended runs, and the platform spike |
| [Feature support](feature-support.md) | Which features work in which environment, and the scope of the game itself |

## Reference

| Page | Contents |
|---|---|
| [Platform abstraction](platform-abstraction.md) | The `IPlatformProvider` boundary and the four invariants a provider must honour |
| [Platform services](platform-services.md) | Every service, the file that implements it, and how far it has been proven |
| [Windows packaging](windows-packaging.md) | Building, packaging and installing on a PC |
| [Building for XBOX Series X\|S](xbox-console-build.md) | High-level overview of the XBOX Series X\|S build. Available to XBOX development partners only. |

## Background

| Page | Contents |
|---|---|
| [Design notes](design-notes.md) | Why the sample is shaped the way it is, plus the findings that cost real time |
| [Known gaps](known-gaps.md) | What is missing, what has never been observed working, and what is missing on purpose |
| [Troubleshooting](troubleshooting.md) | Symptoms, causes and fixes |
| [Vendored Party binaries](../third_party/party/README.md) | What is vendored under `third_party/party/`, and which target it is correct for |

## Reading the source

The document set here is deliberately small, because the detail lives in XML comments on
the source. [Platform services](platform-services.md) is the index that maps each platform
feature to the file that implements it.
