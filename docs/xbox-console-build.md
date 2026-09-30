# Building for XBOX Series X|S

> [!IMPORTANT]
> **This path is available to XBOX development partners only.**
>
> The public [Microsoft GDK on GitHub](https://aka.ms/gdk) covers Windows PC development.
> Building, deploying and running on XBOX Series X|S additionally needs the console
> portions of the GDK, a development kit, and documentation that Microsoft distributes only
> to organisations onboarded as XBOX development partners under a GDK Agreement.
>
> If you do not have that access, everything in this repository still builds and runs on
> Windows. See [Getting started](getting-started.md).
>
> To begin onboarding, visit the
> [XBOX Developer Program](https://developer.microsoft.com/en-us/games/). Partners can
> find the authoritative XBOX Series X|S setup, tooling, deployment and certification
> documentation in the
> [GDK documentation](https://learn.microsoft.com/en-us/gaming/gdk/?view=gdk-2604) and on
> the secure partner download site.

This page describes the shape of the XBOX Series X|S build at a high level and explains the
parts that transfer to any C# game. It deliberately does not reproduce partner-only setup
steps, toolchain detail, deployment mechanics or certification requirements. Those live in
the partner documentation, which is the authoritative source and stays current.

## Two targets from one set of sources

| | Windows | XBOX Series X\|S |
|---|---|---|
| Project | `src/NetRumble.Game` | `src/NetRumble.Game.GDKX` |
| Target framework | `net9.0-windows` | `net8.0` |
| MonoGame | 3.8.5 from NuGet | 3.8.5 built from source with the console backend |
| Runtime | CoreCLR with JIT | NativeAOT, linked into a native host |
| Build | `dotnet build NetRumble.slnx` | `tools\Build-Gdkx.ps1` |
| Package | `tools\Build-GdkPc.ps1 -Package` | `tools\Build-Gdkx.ps1 -Package` |
| Game config | `packaging\MicrosoftGame.PC.config` | `packaging\MicrosoftGame.GDKX.*.config` |

**The game sources are shared verbatim and contain no `#if GDKX`.** As of MonoGame 3.8.5
the desktop and XBOX Series X|S backends present the same managed API surface, so game code
does not have to know which target it is compiling for. A `GDKX` constant is defined for
the XBOX Series X|S build, but nothing uses it, and it should stay that way. A `#if` in
game code is a signal that something belongs behind the platform abstraction instead. See
[Platform abstraction](platform-abstraction.md).

## Prerequisites

Building for XBOX Series X|S needs, in addition to the Windows prerequisites in
[Getting started](getting-started.md):

- the console portions of the GDK;
- Visual Studio 2026 (18.x) with the C++ workload, or Visual Studio 2022 with
  `-PlatformToolset v143`;
- an authorized XBOX Series X|S development kit, set up according to the partner
  documentation;
- MonoGame and the console runtime components checked out beside this repository.

`tools\Setup-Gdkx.ps1` checks every prerequisite, reports what is missing, and prepares
the MonoGame console backend. Run it once. `tools\Build-Gdkx.ps1` then builds and,
with `-Package`, packages.

`dotnet build` cannot build `.vcxproj` files at all, failing with `MSB4278`, so the
XBOX Series X|S flow shells out to the Visual Studio copy of MSBuild.

## How the pieces fit

The XBOX Series X|S OS does not host CoreCLR, so there is no managed executable with a
runtime beside it. The build instead:

1. compiles the shared game sources for `net8.0` against MonoGame built from source;
2. runs the NativeAOT compiler over the result, turning the whole managed program into a
   single object file;
3. links that object file into a native executable and stages the package layout.

`src/NetRumble.Game.GDKX` targets `net8.0` rather than `net9.0` because the NativeAOT
compiler used for this target is pinned to the 8.0 feature band. That pin says nothing
about what the game code needs.

## Why the XBOX Series X|S project is not in NetRumble.slnx

`dotnet build NetRumble.slnx` is the one command that builds and tests everything else,
and it must not require a GDK install, a development kit or the console toolchain.
`src/NetRumble.Game.GDKX` needs all three plus content the desktop build has already
produced, so adding it to the solution would turn a missing optional toolchain into a hard
failure for everyone.

`tools\Build-Gdkx.ps1` builds it instead, and it fails with an explanation rather than an
MSBuild error when a prerequisite is missing.

## Content is built once by the desktop project

The XBOX Series X|S build consumes the `.xnb` files the desktop build produced and does not
run the content pipeline a second time. MonoGame's `ContentManager` validates the platform
byte in an `.xnb` header against the set of platform ids it knows rather than against the
platform it is running on, so a Windows-built `.xnb` loads unchanged.

This holds because this title's content is textures, `SpriteFont`s and PCM or MP3 audio.

**It does not hold for custom effects.** Compiled shaders are profile specific. Adding a
`.fx` to this game means building content a second time with the XBOX Series X|S shader
profile and pointing `GdkxContentDir` at that output.
`tools\Build-ConsoleShaders.ps1` does this.

## Reflection and trimming

NativeAOT removes anything it cannot see a static path to, so anything reached
reflectively has to be named in a runtime directives file. This repository roots exactly
one assembly, `NetRumble.Core`, whose data types the content pipeline deserializes through
MonoGame's `ReflectiveReader`. That rooting lives in `console\NetRumble.rd.xml`.

Everything else is statically analysable, so **the AOT step should produce no `IL3053` or
`IL3050` warnings from this repository's own assemblies**. If one appears, something
reflective has been added. Expand the aggregate warning with
`/p:GdkxIlcFlags=--nosinglewarnassembly:<AssemblyName>` and fix the call site rather than
rooting the assembly.

The pattern that makes this work is worth copying. `PlayFabRestClient.PostAsync` takes a
`JsonTypeInfo<T>` explicitly instead of looking the body's type up at runtime, so a request
type nobody registered in the source-generated `PlayFabJsonContext` fails to compile
instead of failing on XBOX Series X|S. Do the same for any new endpoint.

## Debugging

`NetRumble.GDKX.sln` is the Visual Studio solution for the XBOX Series X|S flavour. It
builds the managed game, the AOT step and the native host in the order the console layout
requires, and it also builds MonoGame's native backend.

Make `NetRumble.Bootstrap` the startup project, configure your development kit in that
project's debugging properties, and press F5.

Two things catch people out:

- The native host is an ordinary native executable, so use the **Native Only** debugger.
  The game code is already AOT compiled by that point and C# source breakpoints do not
  bind.
- If the build succeeds but nothing deploys, open **Configuration Manager** and confirm
  that **Deploy** is checked for `NetRumble.Bootstrap`. The checked-in solution enables it
  for every XBOX Series X|S configuration.

A managed `DllNotFoundException` for `mgruntime` means Visual Studio is launching a managed
project instead of the native host.

## Restore

Both `dotnet build` calls in `tools\Build-Gdkx.ps1` pass `/p:NoWarn=NU1603`. See
[Restore fails with NU1603](troubleshooting.md#restore-fails-with-nu1603).

## Toolchain patches

The external repositories this build depends on pin `PlatformToolset` to `v143`, which is
Visual Studio 2022. The scripts pass `-PlatformToolset v145` instead. Nothing is patched in
place, so those checkouts stay clean and can be pulled.
