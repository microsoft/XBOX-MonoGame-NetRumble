# Configuration

This page separates the things needed to **build** the sample from the accounts and
services needed to **run online**. Start with [Getting started](getting-started.md), then
choose the environment for the [walkthroughs](walkthroughs.md).

## What each environment needs

| Environment | What you need | What it lets you learn |
|---|---|---|
| Build the desktop solution | Windows, the .NET SDK, and the XBOX GDK.NET source checkout | The managed platform boundary and a local build. The source reference is required even without the installed native runtime. |
| Run on PC | Microsoft GDK and native redistributables, an XBOX title and sandbox, and an authorized test account | XBOX sign-in and Practice. Online play also needs a correctly linked PlayFab title, Lobby and Party. |
| XBOX Series X\|S | The runtime and account requirements above, plus partner-only console tooling and a development kit | Console builds, platform activation and lifecycle behavior. See [Building for XBOX Series X\|S](xbox-console-build.md). |

These are different resources: installing an SDK does not create an XBOX title, provision
a sandbox account, or configure a PlayFab title. The sample title requires access granted
through an XBOX publishing relationship. Without an authorized account, you can build and
study the source but cannot enter the game, including Practice.

## Source checkout and build

The projects expect a sibling checkout named `gdk-dotnet`:

```text
C:\work\
  gdk-dotnet\
  XBOX-MonoGame-NetRumble\
```

The default `GdkNetRoot` points to that sibling. If your checkout has a different name or
location, set the MSBuild property to its **absolute directory with a trailing
backslash**. For example, in the PowerShell session used to build:

```powershell
$env:GdkNetRoot = 'C:\src\gdk-dotnet\'
dotnet build NetRumble.slnx /p:NoWarn=NU1603
```

The source checkout is not distributed on a public NuGet feed. Access to it and acceptance
of its terms are separate from access to the public GDK. See
[Getting started](getting-started.md#what-you-need) and
[Troubleshooting](troubleshooting.md#restore-fails-with-nu1603).

## XBOX title and service configuration

The game configuration identifies an XBOX title; it is not the same thing as a PlayFab
title ID. The files under [`packaging/`](../packaging/) contain this sample's identity.
The unpackaged desktop build and spike stage `MicrosoftGame.config` beside the
executable; the PC package uses `MicrosoftGame.PC.config`. Leave the sample values in
place unless you are intentionally configuring a title you own.

XBOX Services also needs the title's **service configuration ID** (SCID).
[`XboxServicesConfiguration`](../src/NetRumble.Platform/XboxServicesConfiguration.cs)
resolves it in this order:

1. `--xbl-scid=YOUR_SCID` on the command line.
2. The `NETRUMBLE_XBL_SCID` environment variable.
3. `xboxservices.config` beside the executable, staged from
   [`packaging/xboxservices.config`](../packaging/xboxservices.config).
4. A logged, title-ID-derived fallback when none of the above is present.

The fallback is a convention, not proof that the service configuration belongs to your
title. Use the SCID supplied for your own title. Successful initialization alone does not
prove that achievements, presence or activity are configured.

### User model

The package configurations use the GDK **simplified user model**
(`AdvancedUserModel=false`). [`GameCoreIdentityService`](../src/NetRumble.Platform.GameCore/Services/GameCoreIdentityService.cs)
requests `UserAddOptions.AddDefaultUserSilently`; the title does not open an account picker
or switch accounts in session. The system owns sign-in and user-change relaunches. On a
loose-deployed PC build with no user, the runtime may exit and launch its sign-in
bootstrapper. See [Platform services](platform-services.md#implementation-status).

## Selecting a PlayFab title

[`PlayFabConfiguration.Resolve`](../src/NetRumble.Platform/PlayFabConfiguration.cs)
chooses the first non-empty value from:

1. `--playfab-title=YOUR_TITLE_ID`.
2. `NETRUMBLE_PLAYFAB_TITLE_ID`.
3. The built-in sample PlayFab title ID.

A title ID identifies the backend; it is not a developer secret or proof of account
access. Supplying another ID does not create or configure a PlayFab title, authorize an
XBOX account, or retarget the XBOX title and SCID. Configure the XBOX and PlayFab sides
for the same intended product using the applicable Microsoft and PlayFab documentation.
Never put a developer secret key or an entity token in source, a command line, or a
shared log.

The GDK provider owns one XBOX-backed PlayFab session. An XBOX login can succeed while the
PlayFab exchange fails. In that case the player can reach the front end and Practice, but
Party and Lobby cannot use an absent PlayFab entity. Setting the title to `PLACEHOLDER`
disables the PlayFab path; it does **not** disable the GDK sign-in requirement.

## Save storage

The GDK provider uses `XGameSaveFiles`, independently of the PlayFab title. On the first
cloud operation, [`GameCoreGameSaveService`](../src/NetRumble.Platform.GameCore/Services/GameCoreGameSaveService.cs)
obtains the signed-in user's folder through GDK.NET's
`GameSaveFiles.GetFolderWithUiAsync`. The title reads and writes files in that folder;
the platform manages synchronization. Local files are scoped to the signed-in user, and
unsigned local service data is held only in memory.

Settings and achievement statistics use local and cloud paths. Match history uses only
the local path and does not roam. A first-run settings file is seeded without pushing
defaults ahead of the cloud pull. Roaming between devices has not been verified. See the
[save walkthrough](walkthroughs.md#saves-and-achievements) and
[feature support](feature-support.md#cloud-save).
