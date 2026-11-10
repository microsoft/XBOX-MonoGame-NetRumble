# Getting started

Build NetRumble on Windows and reach a playable match. Start here before any other page
in this folder.

## What you need

| Requirement | Why |
|---|---|
| Windows 10 or 11 | The Microsoft GDK, PlayFab and PlayFab Party binaries are Windows only. |
| .NET 9 SDK or later | The desktop game targets `net9.0-windows`. |
| [XBOX GDK.NET](https://github.com/gaming-microsoft/gdk-dotnet), cloned beside this repository | A managed projection of the GDK, PlayFab and Party used by the GameCore provider. |
| A Microsoft GDK edition | Required to run the game, including Practice. The source can still be built without the installed native runtime. |
| An XBOX account with access to this sample's title and sandbox | GDK sign-in is required before entering the main menu. |

XBOX GDK.NET is not published on NuGet and the solution consumes it as a `ProjectReference`,
so it is a prerequisite for the solution and GameCore builds, not only the XBOX Series X|S
build. The standalone game core and platform interface projects do not depend on it.
Set the `GdkNetRoot` MSBuild property if your checkout lives somewhere other than beside
this repository. See [Configuration](configuration.md#source-checkout-and-build) for
the expected layout and an override example.

All PlayFab calls use GDK.Net's native wrappers and the Gaming Runtime, with the
native Core, Services and Multiplayer libraries.
The desktop build stages available native libraries from `GameDKCoreLatest`; console
packaging stages the XBOX copies instead.

The `gdk-dotnet` repository that holds XBOX GDK.NET is not public at the time of writing.
Request access through the
[XBOX Developer Program](https://developer.microsoft.com/en-us/games/) or your Microsoft
representative.

Targeting XBOX Series X|S needs more than the list above, and all of it is restricted to
XBOX development partners. See [Building for XBOX Series X|S](xbox-console-build.md).

## Build and run

```powershell
git clone https://github.com/gaming-microsoft/gdk-dotnet.git
git clone --branch develop https://github.com/microsoft/XBOX-MonoGame-NetRumble.git
cd XBOX-MonoGame-NetRumble
dotnet build NetRumble.slnx /p:NoWarn=NU1603
dotnet run --project src\NetRumble.Game --no-build
```

`/p:NoWarn=NU1603` is required, and it has to be passed as a global property rather than
set inside a project file. See
[Restore fails with NU1603](troubleshooting.md#restore-fails-with-nu1603) for the reason.

## What you should see

With a GDK installed and an authorized XBOX account available, the game opens the
acquire-user screen. Press a key to begin sign-in; the system supplies its default user and
the main menu shows the gamertag. The title uses the simplified user model rather than a
title-driven account picker.

If sign-in fails, the screen reports the stage it reached and the reason, and lets you
retry. The main menu and Practice remain unavailable until GDK sign-in succeeds.
If the native runtime is missing, install the Microsoft GDK before trying to play.
After signing in, choose **Practice** for a single-player match with configurable bots.
XBOX sign-in can succeed even if the separate PlayFab exchange fails, so a gamertag alone
does not prove that Lobby and Party are available.

## Online features need access to this title

Sign-in, lobby discovery, Party networking, privileges, achievements and presence all run
against this sample's own sandbox and title. Reaching them requires an XBOX publishing
relationship and a test account provisioned in that sandbox.

Without that access you can still clone, build and inspect the source. Playing requires
GDK sign-in; Practice uses the same account requirement as the rest of the game.

Join the [XBOX Developer Program](https://developer.microsoft.com/en-us/games/) to begin
the onboarding process.

## Keep the sample identity unchanged

The files under `packaging\` carry the StoreId, TitleId and MSAAppId that identify this
sample. Changing them without configuring the corresponding title can break sign-in,
invites and achievements. Leave them as they are unless you are deliberately retargeting
the sample at a title of your own.

## Where to go next

| Page | Read it when |
|---|---|
| [Configuration](configuration.md) | You need to configure the source dependency, account, XBOX or PlayFab title. |
| [Walkthroughs](walkthroughs.md) | You want to follow a player action into the actual service calls. |
| [Manual test plan](manual-test-plan.md) | You want to record observations and untested cases. |
| [Run modes](run-modes.md) | You want multiplayer, Practice, or an unattended run. |
| [Feature support](feature-support.md) | You want to know which features work in which environment. |
| [Platform abstraction](platform-abstraction.md) | You want to reuse the provider boundary in your own game. |
| [Windows packaging](windows-packaging.md) | You want an installable package on the same PC. |
| [Building for XBOX Series X\|S](xbox-console-build.md) | You are an XBOX development partner with a devkit. |
| [Troubleshooting](troubleshooting.md) | A build, install or sign-in step failed. |
