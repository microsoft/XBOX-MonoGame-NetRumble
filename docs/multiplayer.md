# Multiplayer: Lobby, Party and the game

NetRumble uses **PlayFab Lobby for discovery** and **PlayFab Party for transport and
chat** through the GDK provider. This page follows the MonoGame integration; it does not
describe the separate Godot addon.

For a player-facing exercise, start with
[Discover and join a Party match](walkthroughs.md#discover-and-join-a-party-match).
The [feature matrix](feature-support.md) describes intended availability;
[Known gaps](known-gaps.md#never-observed-end-to-end) records what has not been proven.

## Layers and ownership

```text
MonoGame screens: connectivity, sign-in, permissions, host/join UI
        |
IPartyService: host, join, leave, messages and chat
        |
GameCorePartyService
  PlayFab Lobby: discover by code
  PlayFab Party: network and chat
        |
PartyMatchNetwork: roster, wire messages and host-authoritative match state
```

The common interface is [`IPartyService`](../src/NetRumble.Platform/Networking/IPartyService.cs).
[`LobbyScreen`](../src/NetRumble.Game/UI/Screens/LobbyScreen.cs) uses it without importing
GDK types. The provider owns native handles; the game consumes `PlatformResult` and
branches on its status rather than on a native error code.

## Before hosting or joining

1. [`NetRumbleGame`](../src/NetRumble.Game/NetRumbleGame.cs) initializes the GDK
   provider and continues running its frame loop.
2. [`GameCoreIdentityService`](../src/NetRumble.Platform.GameCore/Services/GameCoreIdentityService.cs)
   acquires an XBOX user and attempts the PlayFab exchange. Party needs the resulting
   PlayFab entity; an XBOX gamertag alone is not enough.
3. `MainMenuScreen` checks connectivity and multiplayer privilege.
   `LobbyScreen.ConnectAsync` checks again at the point of use and resolves the
   communications privilege before chat can transmit.
4. `GameCorePartyService` checks its runtime and identity, and can retry a transient
   PlayFab sign-in failure.

In the game loop, the relevant ordering is:

```csharp
_mainThread.Pump();
_platform.Runtime.Pump();
```

The first drain resumes queued game-thread continuations. The second drains dispatched
platform work and Lobby/Party state changes. Do not block the frame loop while waiting for
an operation whose completion needs the pump. The headless
[`platform spike`](../tools/NetRumble.PlatformSpike/Program.cs) demonstrates pumping while
waiting without MonoGame. Suspend is a separate OS-thread callback; see
[Platform abstraction](platform-abstraction.md#3-keep-game-state-on-the-game-thread).

## Host a match

[`GameCorePartyService.HostAsync`](../src/NetRumble.Platform.GameCore/Services/GameCorePartyService.cs)
performs the following work:

1. Initialize the Party manager for the selected PlayFab title, create a local Party user
   from the signed-in entity, and find available Party regions.
2. Create the Party network, connect and authenticate the host, then create its endpoint
   and chat control. Party operations return operation IDs; their completions are matched
   to waiting tasks as state changes are pumped.
3. Serialize the network descriptor and create a PlayFab Lobby containing the connection
   data, an opaque host token and a protocol version. The
   [`PlayFabLobbyClient`](../src/NetRumble.Platform.PlayFab/PlayFabLobbyClient.cs)
   publishes a five-character code in indexed search data.
4. Return the code to `LobbyScreen`, which binds `PartyMatchNetwork` and publishes XBOX
   activity when invites are available.

The lobby is public because the sample resolves codes with `FindLobbies`, which does not
return private lobbies. A short join code is a discovery convenience, not authentication.
Do not put credentials or private player information in searchable lobby properties.
Party authenticates its users and endpoints separately.

## Join by code or invite

For a code, `GameCorePartyService.JoinAsync` asks `PlayFabLobbyClient` to validate and
search the code. It rejects a missing or mismatched protocol version before joining the
lobby, then reads the Party connection data, connects and authenticates a local user, and
creates an endpoint. A small hello/welcome handshake assigns the client's game peer ID.
The client verifies the host against the published token before accepting host authority.

This is the actual call from `LobbyScreen.JoinAsync`, with the surrounding timeout and
failure handling omitted:

```csharp
result = await party.JoinAsync(code, NRProtocol.VersionString(), timeout.Token);
```

An XBOX invite follows a different discovery path: `InviteRouter` waits for sign-in and
the front end, then `LobbyScreen` calls `JoinByConnectionStringAsync`. The invite carries
the Party connection data and protocol version; it need not resolve a Lobby code first.

After either path succeeds,
[`PartyMatchNetwork`](../src/NetRumble.Core/Net/PartyMatchNetwork.cs) maps endpoints to
game peers and dispatches encoded messages. The host owns match state; clients must not
accept authoritative snapshots or score changes from another client. See
[Design notes](design-notes.md#network-trust-boundary) for the trust boundary.

## Leave and fail safely

Host and join failures unwind partial lobby and Party state before returning to the menu.
On a normal host leave, `PlayFabLobbyClient.CloseAndLeaveLobbyAsync` locks the lobby
before the owner leaves; clients leave their own membership. Provider shutdown also
releases Party and PlayFab resources before the Gaming Runtime.

Party teardown waits share a six-second budget, while Lobby requests have their own
timeouts. Session revisions prevent an older attempt or leave from tearing down a newer
session. A late host response for a retired session is closed by its own lobby ID rather
than adopted into the replacement session. See
[Party teardown and pump ownership](platform-services.md#party-teardown-and-pump-ownership).

A valid-looking code can still name a stale session, and a successful local capability
probe does not guarantee account access or service availability. Handle the result of
each asynchronous operation. The sample has no host migration, join in progress or
PlayFab Matchmaking queue.
