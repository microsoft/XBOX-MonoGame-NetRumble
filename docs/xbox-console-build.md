# Building for XBOX Series X|S

> [!IMPORTANT]
> **This path is available to XBOX development partners only.**
>
> The public [Microsoft GDK on GitHub](https://aka.ms/gdk) covers Windows PC development.
> Building, deploying and running on XBOX Series X|S additionally needs the console
> portions of the GDK, a development kit, and documentation that Microsoft distributes only
> to organisations onboarded as XBOX development partners under a GDK Agreement.
>
> Without console-partner access, you can still build for Windows. Running the game also
> requires the GDK and an authorized XBOX account. See [Getting started](getting-started.md).
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
| Build | `dotnet build NetRumble.slnx /p:NoWarn=NU1603` | `tools\Build-Gdkx.ps1` |
| Package | `tools\Build-GdkPc.ps1 -Package` | `tools\Build-Gdkx.ps1 -Package` |
| Game config | `packaging\MicrosoftGame.PC.config` | `packaging\MicrosoftGame.GDKX.*.config` |

The projects share the game sources. Most gameplay and platform-service calls use the same
managed surface on both targets; the existing `GDKX` conditionals cover a few
target-specific entry-point, storage, diagnostics and UI details. Keep new platform
service differences behind the provider boundary where practical. See
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

### Match the managed and native MonoGame ABI

The managed framework and console backend must agree on native entry points and
signatures. The bootstrap build runs `tools\NetRumble.NativeImportChecks` against the
managed framework used by AOT and the staged `mgruntime.dll`. Missing exact named
exports fail the build before packaging; successful linking or package validation
alone does not catch this mismatch. The check reads PE metadata without executing
console code and does not prove parameter or structure-layout compatibility.

Use matching managed and console sources, not export aliases or no-op compatibility
entry points, and repeat console play testing after a toolchain change.

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

`dotnet build NetRumble.slnx` builds the desktop solution without requiring an installed
GDK, a development kit or the console toolchain. Running the checks is a separate step.
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

PlayFab calls use GDK.Net's typed native wrappers rather than reflection-based JSON
serialization. Keep new service calls on those wrappers; request marshalling and native
result ownership belong to GDK.Net. The console layout includes `PlayFabServices.dll`
and `PlayFabMultiplayer.dll` alongside Core and Party.

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
Visual Studio 2022. The scripts pass `-PlatformToolset v145` instead of editing those
project files.

The bootstrap build applies `tools\patches\monogame-xbox-input.patch` to the MonoGame
console backend before compiling it. Use an isolated dependency checkout: this patch
changes its working tree. An already-applied patch is recognized; incompatible sources
fail the build rather than silently skipping the fix.

The patch treats a controller read as connected only when it succeeds, avoiding phantom
controllers on other error results. Bounded native and managed diagnostics trace input
initialization, slot discovery, the first button input and the acquire-user handoff.
These diagnostics are console-only and do not replace testing with a real controller.

`tools\Build-ConsoleShaders.ps1` also applies
`tools\patches\monogame-shader-rootsig.patch` to the MonoGame checkout before building
MGCB. The patch preserves the space between DXC's root-signature arguments used to build
the working console shaders. The script recognizes an applied patch, rejects incompatible
sources, and rebuilds MGCB when the patch is newly applied or its compiler output is stale,
even with `-SkipToolBuild`. Use an isolated MonoGame checkout for this change too.
