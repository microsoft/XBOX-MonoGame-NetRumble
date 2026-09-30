# Getting started

Build NetRumble on Windows and reach a playable match. Start here before any other page
in this folder.

## What you need

| Requirement | Why |
|---|---|
| Windows 10 or 11 | The Microsoft GDK, PlayFab and PlayFab Party binaries are Windows only. |
| .NET 9 SDK or later | The desktop game targets `net9.0-windows`. |
| [XBOX GDK.NET](https://github.com/gaming-microsoft/gdk-dotnet), cloned beside this repository | A managed projection of the GDK, PlayFab and Party. Every project in the solution reaches the platform through it. |
| A Microsoft GDK edition | Needed for any GDK-backed feature. The game still builds and runs without one. |

XBOX GDK.NET is not published on NuGet and the solution consumes it as a `ProjectReference`,
so it is a hard prerequisite for **every** build rather than only the XBOX Series X|S one.
Set the `GdkNetRoot` MSBuild property if your checkout lives somewhere other than beside
this repository.

The `gdk-dotnet` repository that holds XBOX GDK.NET is not public at the time of writing.
Request access through the
[XBOX Developer Program](https://developer.microsoft.com/en-us/games/) or your Microsoft
representative.

Targeting XBOX Series X|S needs more than the list above, and all of it is restricted to
XBOX development partners. See [Building for XBOX Series X|S](xbox-console-build.md).

## Build and run

```powershell
git clone https://github.com/gaming-microsoft/gdk-dotnet.git
git clone https://github.com/gaming-microsoft/monogame-rumble.git
cd monogame-rumble
dotnet build NetRumble.slnx /p:NoWarn=NU1603
dotnet run --project src\NetRumble.Game
```

`/p:NoWarn=NU1603` is required, and it has to be passed as a global property rather than
set inside a project file. See
[Restore fails with NU1603](troubleshooting.md#restore-fails-with-nu1603) for the reason.

## What you should see

With a GDK installed and an XBOX account available, the game opens the acquire-user
screen, signs in, and lands on the main menu showing your gamertag.

If sign-in fails, the screen reports the stage it reached and the reason. Choose
**Continue Offline** to reach Practice, which needs no account and no network.

With no GDK installed, the game starts in the offline provider, reports the runtime as
unavailable, and offers Practice. This is a supported configuration and is how the front
end and the single-player match are developed.

## Online features need access to this title

Sign-in, lobby discovery, Party networking, privileges, achievements and presence all run
against this sample's own sandbox and title. Reaching them requires an XBOX publishing
relationship and a test account provisioned in that sandbox.

Without that access you can still:

- clone, build and play **Practice**;
- run real netcode between two processes with the **LAN** transport, which needs no GDK,
  no PlayFab title and no XBOX account.

Join the [XBOX Developer Program](https://developer.microsoft.com/en-us/games/) to begin
the onboarding process.

## Keep the sample identity unchanged

The files under `packaging\` carry the StoreId, TitleId and MSAAppId that identify this
sample. Changing them points the build at a title that does not exist, and sign-in,
invites and achievements stop resolving. Leave them as they are unless you are
deliberately retargeting the sample at a title of your own.

## Where to go next

| Page | Read it when |
|---|---|
| [Run modes](run-modes.md) | You want two instances playing each other, an unattended run, or the offline provider. |
| [Feature support](feature-support.md) | You want to know which features work in which environment. |
| [Platform abstraction](platform-abstraction.md) | You want to reuse the provider boundary in your own game. |
| [Windows packaging](windows-packaging.md) | You want an installable package on the same PC. |
| [Building for XBOX Series X\|S](xbox-console-build.md) | You are an XBOX development partner with a devkit. |
| [Troubleshooting](troubleshooting.md) | A build, install or sign-in step failed. |
