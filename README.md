<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/netrumble-banner-dark.png">
    <img alt="NetRumble, a MonoGame multiplayer sample for Microsoft GDK and PlayFab" src="docs/images/netrumble-banner-light.png" width="620">
  </picture>
</p>

[![License: MIT][badge-license]][link-license]
[![MonoGame 3.8.5][badge-monogame]][link-monogame]
[![XBOX Series X|S][badge-console]][link-console]
[![Microsoft GDK supported][badge-gdk]][link-gdk]
[![PlayFab supported][badge-playfab]][link-playfab]
[![MonoGame input][badge-input]][link-input]
[![MonoGame GDK documentation][badge-docs]][link-docs]
[![Visit our Blog][badge-blog]][link-blog]
[![Join us on Discord][badge-discord]][link-discord]
[![PRs welcome][badge-prs]][link-prs]
[![Join the XBOX Developer Program][badge-devprogram]][link-devprogram]

# XBOX MonoGame NetRumble

**A complete multiplayer XBOX game, built in MonoGame.** NetRumble is a 2D top-down space
shooter. Dodge the asteroids, grab lasers, rockets and mines from a twenty-weapon arsenal,
and shoot your friends for points. Party voice chat, invites from the guide, achievements
and a saved match history are all included.

Microsoft **GDK** and **PlayFab** services are wired into the game loop, so you can trace
sign-in, privileges, privacy, Party networking and cloud saves through the sample, then
exercise the available paths with the required accounts and service access.

The game is written in C# against **MonoGame 3.8.5** and reaches the platform through
[XBOX GDK.NET](https://github.com/gaming-microsoft/gdk-dotnet), a managed projection of
the GDK, PlayFab and Party, behind a single `IPlatformProvider` boundary. The game sources
build for CoreCLR on the desktop and for XBOX Series X|S; limited conditional compilation
handles platform-specific startup and UI. That boundary is the most reusable part of the
sample. See [Platform abstraction](docs/platform-abstraction.md).

> [!IMPORTANT]
> **This is a source-only sample rather than a shipping game.** NetRumble is MIT licensed
> at the game layer. The Microsoft GDK, PlayFab, Party and
> [XBOX GDK.NET](https://github.com/gaming-microsoft/gdk-dotnet) dependencies require
> their own installs and license acceptance, consistent with other XBOX samples.
>
> There is no fixed update cadence for support or maintenance. We watch the repository,
> monitor issues and iterate where it makes sense. We would like to hear your feedback and
> see your pull requests as this evolves.

<img width="1920" height="1080" alt="XBOX MonoGame NetRumble sample banner, showing the game's NET.RUMBLE start screen signing in to XBOX" src="docs/images/monogame_netrumble_hero.png" />

## Quickstart

You need Windows, the .NET 9 SDK or later, and access to the private
[XBOX GDK.NET](https://github.com/gaming-microsoft/gdk-dotnet) source repository.
Running the game also needs the Microsoft GDK and an authorized XBOX account for the
configured title and sandbox. This includes Practice; there is no guest mode.

```powershell
git clone https://github.com/gaming-microsoft/gdk-dotnet.git
git clone --branch develop https://github.com/microsoft/XBOX-MonoGame-NetRumble.git
cd XBOX-MonoGame-NetRumble
dotnet build NetRumble.slnx /p:NoWarn=NU1603
dotnet run --project src\NetRumble.Game --no-build
```

`/p:NoWarn=NU1603` is required and has to be passed as a global property. See
[Restore fails with NU1603](docs/troubleshooting.md#restore-fails-with-nu1603).

Press a key at the sign-in screen. After the system supplies its default XBOX user,
choose **Practice** or an online action. An XBOX gamertag alone does not prove that the
separate PlayFab login succeeded.

Full prerequisites and title setup are in [Getting started](docs/getting-started.md) and
[Configuration](docs/configuration.md). Follow an action into the source in the
[walkthroughs](docs/walkthroughs.md).

## What the sample covers

XBOX identity exchanged for a PlayFab identity, PlayFab Lobby discovery, PlayFab Party as
the match transport, Party voice with speech-to-text captions, privileges, privacy, text
moderation, achievements, rich presence, invites from the guide, roaming cloud save,
process lifecycle and controller detection.

[Feature support](docs/feature-support.md) lists every feature against every environment,
on PC and XBOX Series X|S. It describes intended paths, not a live-service test report.
Both environments require the GDK and XBOX sign-in.

| Learn about | Start in the source | Guide |
|---|---|---|
| XBOX and PlayFab identity | [`GameCoreIdentityService`](src/NetRumble.Platform.GameCore/Services/GameCoreIdentityService.cs), [`PlayFabOnlineServices`](src/NetRumble.Platform.PlayFab/PlayFabOnlineServices.cs) | [Sign in](docs/walkthroughs.md#sign-in-to-xbox-and-playfab) |
| Lobby discovery and Party transport | [`GameCorePartyService`](src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs), [`PlayFabLobbyClient`](src/NetRumble.Platform.PlayFab/PlayFabLobbyClient.cs) | [Multiplayer](docs/multiplayer.md) |
| Activity and invites | [`GameCoreActivityService`](src/NetRumble.Platform.GameCore/Services/GameCoreActivityService.cs), [`InviteRouter`](src/NetRumble.Game/InviteRouter.cs) | [Invite a friend](docs/walkthroughs.md#invite-a-friend) |
| Privileges, privacy and chat | [`GameCorePrivilegeService`](src/NetRumble.Platform.GameCore/Services/GameCorePrivilegeService.cs), [`GameCorePrivacyService`](src/NetRumble.Platform.GameCore/Services/GameCorePrivacyService.cs) | [Communications](docs/walkthroughs.md#voice-text-privileges-and-privacy) |
| Saves and achievements | [`GameCoreGameSaveService`](src/NetRumble.Platform.GameCore/Services/GameCoreGameSaveService.cs), [`AchievementTracker`](src/NetRumble.Game/Profile/AchievementTracker.cs) | [Saves and achievements](docs/walkthroughs.md#saves-and-achievements) |
| Lifecycle and controllers | [`LifecycleCoordinator`](src/NetRumble.Game/LifecycleCoordinator.cs), [`ControllerWatcher`](src/NetRumble.Game/ControllerWatcher.cs) | [Lifecycle](docs/walkthroughs.md#lifecycle-connectivity-and-controllers) |

> [!NOTE]
> This sample is still being built and some things are missing or broken. The controller
> glyphs are obvious placeholders rather than certification-compliant art, and the store
> logos are generated stand-ins. Read [Known gaps](docs/known-gaps.md) before you file a
> bug.

## Architecture at a glance

```
Program.cs                      Resolves the provider mode, then constructs the game
NetRumbleGame                   MonoGame Game: pumps the platform once per frame, owns the screen stack
IPlatformProvider               The only platform surface the game can see
  \- ...GameCore                XBOX GDK.NET: identity, Party, privileges, privacy, moderation, achievements, cloud save
       \- ...PlayFab            Lobby discovery and the authentication exchange
MatchSession -> IMatchNetwork   Session, authoritative simulation and the wire codec
LifecycleCoordinator            Suspend, resume, constrain, account change, durable commit
InviteRouter                    Buffers platform activations until identity and the front end are ready
```

This is an ownership sketch rather than a startup order.

`NetRumble.Core` references **`NetRumble.Platform`**, the interface assembly, without a
concrete provider. `NetRumble.Game` also references the GameCore assembly to reach
`PlatformProviderFactory`, the composition root; gameplay and UI consume the provider
interfaces. No `IntPtr`, `HRESULT`, `XUserHandle` or SDK structure appears in a signature
that crosses that boundary. Native errors arrive as a `PlatformResult` the game branches
on.

That boundary lets the provider use a managed GDK projection without exposing native
types throughout gameplay code.

## Documentation

| Page | Read it when |
|---|---|
| [Getting started](docs/getting-started.md) | You are building this for the first time |
| [Configuration](docs/configuration.md) | You need to understand build dependencies, title IDs, accounts and saves |
| [Walkthroughs](docs/walkthroughs.md) | You want player actions connected to source, outcomes and failure cases |
| [Multiplayer](docs/multiplayer.md) | You want the Lobby and Party host/join sequences |
| [Manual test plan](docs/manual-test-plan.md) | You want to record real results without confusing them with intended behavior |
| [Run modes](docs/run-modes.md) | You want Practice, multiplayer or an unattended run |
| [Feature support](docs/feature-support.md) | You want to know what works where |
| [Platform abstraction](docs/platform-abstraction.md) | You want to reuse the provider boundary |
| [Platform services](docs/platform-services.md) | You are looking for the file that implements a feature |
| [Windows packaging](docs/windows-packaging.md) | You want an installable package |
| [Building for XBOX Series X\|S](docs/xbox-console-build.md) | You are an XBOX development partner |
| [Design notes](docs/design-notes.md) | You want the reasoning behind a decision |
| [Known gaps](docs/known-gaps.md) | You want to check platform limitations and unverified paths |
| [Troubleshooting](docs/troubleshooting.md) | A build, install or sign-in step failed |
| [Glossary](docs/glossary.md) | You are new to GDK, XBOX or PlayFab terminology |

New to the GDK or PlayFab? Start with the
[MonoGame GDK documentation](https://aka.ms/XBOXMonoGameDocs), then use
[docs/](docs/) for everything specific to this port. For MonoGame itself, see the
[MonoGame documentation](https://docs.monogame.net/).

> [!IMPORTANT]
> **The online flows need access to this sample's title.** Sign-in, lobby discovery, Party,
> privileges, achievements and presence all run against this sample's own sandbox and
> title, which requires an XBOX publishing relationship and a test account from that
> sandbox.
>
> You can clone and build the source without title access, but entering the game,
> including Practice, requires GDK sign-in. Join the
> [XBOX Developer Program](https://developer.microsoft.com/en-us/games/) to get started.

## Contributing

Pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) explains the acceptance gate
and how to record a decision so it does not get undone later.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for the full text.

The PlayFab Party redistributable binaries vendored under `third_party/party/` are not
covered by that license. They ship under their own Microsoft terms. See
[third_party/party/README.md](third_party/party/README.md).

## Trademarks

This project may contain trademarks or logos for projects, products, or services.
Authorized use of Microsoft trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause
confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos is
subject to those third-party's policies.

[badge-license]: https://img.shields.io/badge/license-MIT-107C10
[link-license]: LICENSE
[badge-monogame]: https://img.shields.io/badge/MonoGame-3.8.5-E73C00
[link-monogame]: https://monogame.net/
[badge-console]: https://img.shields.io/badge/XBOX%20Series%20X%7CS-NativeAOT-107C10?logo=xbox&logoColor=white
[link-console]: docs/xbox-console-build.md
[badge-gdk]: https://img.shields.io/badge/Microsoft%20GDK-%E2%9C%93-107C10
[link-gdk]: https://github.com/microsoft/GDK/releases
[badge-playfab]: https://img.shields.io/badge/PlayFab-%E2%9C%93-107C10
[link-playfab]: docs/platform-abstraction.md
[badge-input]: https://img.shields.io/badge/input-MonoGame-107C10
[link-input]: docs/platform-services.md#why-igameinputservice-is-a-null-implementation
[badge-docs]: https://img.shields.io/badge/docs-Microsoft%20Learn-0078D4
[link-docs]: https://aka.ms/XBOXMonoGameDocs
[badge-blog]: https://img.shields.io/badge/Visit%20our-Blog-FFA500?logo=rss&logoColor=white
[link-blog]: https://developer.microsoft.com/en-us/games/articles/
[badge-discord]: https://img.shields.io/badge/Join%20us%20on-Discord-7289DA?logo=discord&logoColor=white
[link-discord]: https://aka.ms/msftgamedevdiscord
[badge-prs]: https://img.shields.io/badge/PRs-welcome-d6336c
[link-prs]: CONTRIBUTING.md
[badge-devprogram]: https://img.shields.io/badge/XBOX%20Developer%20Program-0B5D0B?logo=xbox&logoColor=white
[link-devprogram]: https://developer.microsoft.com/en-us/games/
