using NetRumble.Platform.Networking;
using NetRumble.Platform;
using NetRumble.Platform.Composition;
using NetRumble.Platform.GameCore;
using NetRumble.Platform.GameCore.Services;
using NetRumble.Platform.PlayFab;

/// <summary>
/// Exercises the Phase 6 <c>GameCorePartyService</c> and <c>GameCoreActivityService</c>
/// degrade paths, plus the pure connection-string parsing <c>InviteRouter</c> and the
/// native activation callback both depend on.
/// </summary>
/// <remarks>
/// <para>
/// Neither service can be driven to a successful outcome here - see their own remarks
/// for exactly why (no PlayFab entity id anywhere in this codebase, and no XSAPI
/// Multiplayer Activity/Presence REST surface). What this class proves instead is the
/// half of the "honesty requirement" that <i>is</i> checkable without credentials: every
/// path degrades to a descriptive failed <see cref="PlatformResult"/> rather than a
/// throw, and the local chat-policy bookkeeping (mute/restrict/indicator) that does not
/// need a network at all is correct.
/// </para>
/// </remarks>
internal static class GameCorePartyAndActivityChecks
{
    public static async Task Run(PlatformProviderMode mode)
    {
        const string Heading = "[13] GameCore Party/Activity degrade paths and URI parsing";

        if (!GdkCheckGate.ShouldRun(mode, Heading))
        {
            return;
        }

        Console.WriteLine(Heading);

        ConnectionStringParsing();
        InviteProtocolVersion();
        await PartyDegradePaths().ConfigureAwait(false);
        ChatPolicy();
        await ActivityDegradePaths().ConfigureAwait(false);
        await OnlineServiceDegradePaths().ConfigureAwait(false);

        Console.WriteLine();
    }

    private static void InviteProtocolVersion()
    {
        var encoded = GameCorePartyService.EncodeInvite(
            "descriptor",
            "invitation",
            "host-entity",
            NRProtocol.VersionString());

        var decoded = GameCorePartyService.TryDecodeInvite(
            encoded,
            out var descriptor,
            out var invitation,
            out var hostEntityId,
            out var protocolVersion);

        Check("GameCore invites carry the protocol version",
            decoded
            && descriptor == "descriptor"
            && invitation == "invitation"
            && hostEntityId == "host-entity"
            && protocolVersion == NRProtocol.VersionString());

        var oldDescriptor = GameCorePartyService.TryDecodeInvite(
            "serialized-party-descriptor",
            out _,
            out _,
            out _,
            out var absentVersion);

        Check("raw or pre-version Party descriptors have no protocol permission",
            oldDescriptor && absentVersion.Length == 0 && !NRProtocol.IsCompatible(absentVersion));
    }

    /// <summary>
    /// <see cref="GameCoreActivityService.ConnectionStringFromUri"/> is the one piece of
    /// the invite-routing chain reachable with no native callback and no signed-in
    /// account at all - it is plain string parsing, ported from
    /// <c>activity_service.gd</c>'s <c>_connection_string_from_uri</c>.
    /// </summary>
    private static void ConnectionStringParsing()
    {
        Check(
            "a bare 'connectionString' key is found",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?connectionString=abc123") == "abc123");

        Check(
            "the snake_case key is matched too",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?connection_string=abc123") == "abc123");

        Check(
            "the lower-case key is matched too",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?connectionstring=abc123") == "abc123");

        Check(
            "key matching is case-insensitive",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?CONNECTIONSTRING=abc123") == "abc123");

        Check(
            "the first matching key wins among several query parameters",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?ref=abc&connectionString=xyz&other=1") == "xyz");

        Check(
            "a URL-encoded value is decoded",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?connectionString=abc%3D123%26456") == "abc=123&456");

        Check(
            "a URI with no query string returns empty",
            GameCoreActivityService.ConnectionStringFromUri("ms-netrumble://join") == string.Empty);

        Check(
            "a query string with no matching key returns empty",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?ref=abc&other=1") == string.Empty);

        Check(
            "a malformed percent-escape falls back to the raw value rather than throwing",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join?connectionString=abc%zz") == "abc%zz");

        // The shapes added for #22, where an accepted invite whose URI did not match the
        // one recognised query form was dropped without trace.
        Check(
            "a fragment-carried payload is found",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join#connectionString=abc123") == "abc123");

        Check(
            "a payload carried as the path is found",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join/abc123") == "abc123");

        Check(
            "a path-carried payload is decoded",
            GameCoreActivityService.ConnectionStringFromUri(
                "ms-netrumble://join/abc%3D123") == "abc=123");

        Check(
            "a bare connection string is taken as-is",
            GameCoreActivityService.ConnectionStringFromUri("abc123") == "abc123");

        Check(
            "an empty or whitespace URI still returns empty",
            GameCoreActivityService.ConnectionStringFromUri("   ") == string.Empty);
    }

    /// <summary>
    /// A <see cref="GameCorePartyService"/> built from an identity with no signed-in
    /// user, which is the state this environment - and any real one without a
    /// completed PlayFab login - is always in. See the class's own remarks for why it
    /// never touches <c>Party.dll</c> at all.
    /// </summary>
    private static async Task PartyDegradePaths()
    {
        var runtime = new GameCoreRuntime();
        var identity = new GameCoreIdentityService(runtime);
        var party = new GameCorePartyService(identity);

        Check("HasNetwork is false with no session", !party.HasNetwork);
        Check("IsHost is false with no session", !party.IsHost);
        Check("JoinCode is empty with no session", party.JoinCode.Length == 0);

        var host = await party.HostAsync(8, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);
        Check("HostAsync fails cleanly without a PlayFab entity", host.Failed);
        Console.WriteLine($"    ---- {host}");

        var join = await party.JoinAsync("ABCDE", NRProtocol.VersionString()).ConfigureAwait(false);
        Check("JoinAsync fails cleanly without a PlayFab entity", join.Failed);

        var joinByString = await party.JoinByConnectionStringAsync("connection-string", NRProtocol.VersionString()).ConfigureAwait(false);
        Check("JoinByConnectionStringAsync fails cleanly without a PlayFab entity", joinByString.Failed);

        var chat = party.SendChatText("hello");
        Check("SendChatText fails cleanly without a session", chat.Failed);

        // Never throws even though there is no bridge and no queue to leave - matches
        // OfflinePartyService's own no-op contract.
        await party.LeaveAsync().ConfigureAwait(false);
        Check("LeaveAsync completes without a session", true);
    }

    /// <summary>
    /// The local mute/restriction/indicator bookkeeping works independently of the
    /// network gate above - it is exercised here on its own, matching
    /// <c>party_service.gd</c>'s own dictionaries.
    /// </summary>
    private static void ChatPolicy()
    {
        var runtime = new GameCoreRuntime();
        var identity = new GameCoreIdentityService(runtime);
        var party = new GameCorePartyService(identity);

        var changeCount = 0;
        party.ChatChanged += () => changeCount++;

        Check("a peer starts unmuted", !party.IsPeerMuted(1));

        party.SetPeerMuted(1, true);
        Check("SetPeerMuted mutes the peer", party.IsPeerMuted(1));
        Check("ChatChanged fired for the mute", changeCount == 1);

        // Indicator is always None while HasNetwork is false, regardless of mute state -
        // matching the class's own documented "no network to carry voice at all" rule.
        Check("GetChatIndicator is None while there is no network", party.GetChatIndicator(1) == ChatIndicator.None);

        party.SetPeerMuted(1, false);
        Check("SetPeerMuted un-mutes the peer", !party.IsPeerMuted(1));

        party.SetPeerRestrictions(2, allowVoice: false, allowText: true);
        Check("ChatChanged fired for the restriction change", changeCount == 3);

        party.ClearChatRestrictions();
        Check("ClearChatRestrictions clears mutes too", !party.IsPeerMuted(1) && !party.IsPeerMuted(2));
        Check("ChatChanged fired for the clear", changeCount == 4);
    }

    /// <summary>
    /// A <see cref="GameCoreActivityService"/> built against an un-initialized
    /// <see cref="GameCoreRuntime"/>, which is what this environment - with no GDK
    /// available at test time or no signed-in user even when it is - always presents.
    /// </summary>
    private static async Task ActivityDegradePaths()
    {
        var runtime = new GameCoreRuntime();
        var identity = new GameCoreIdentityService(runtime);
        var xbl = new GameCoreXblContext(runtime, identity);
        var activity = new GameCoreActivityService(runtime, identity, xbl);

        var invite = await activity.ShowInviteUiAsync().ConfigureAwait(false);
        Check("ShowInviteUiAsync fails cleanly without an initialized runtime", invite.Failed);
        Console.WriteLine($"    ---- {invite}");

        var setActivity = await activity.SetActivityAsync("cs", 8, 1).ConfigureAwait(false);
        Check("SetActivityAsync fails cleanly without an Xbox Live context", setActivity.Failed);

        // Checked before the context is, so this holds even on a machine that has one:
        // an activity with no connection string advertises a join that cannot happen.
        var emptyActivity = await activity.SetActivityAsync("  ", 8, 1).ConfigureAwait(false);
        Check("SetActivityAsync rejects an empty connection string", emptyActivity.Failed);

        var joinable = await activity.GetJoinableActivitiesAsync(["1"]).ConfigureAwait(false);
        Check("GetJoinableActivitiesAsync fails cleanly without an Xbox Live context", joinable.Failed);

        // Short-circuited before the context is reached - an empty query has an empty
        // answer whether or not the platform is there to give it.
        var noneJoinable = await activity.GetJoinableActivitiesAsync([]).ConfigureAwait(false);
        Check(
            "GetJoinableActivitiesAsync returns an empty map for an empty request",
            noneJoinable.Succeeded && noneJoinable.Value is { Count: 0 });

        var reportRecent = await activity.ReportRecentPlayersAsync(["1"]).ConfigureAwait(false);
        Check("ReportRecentPlayersAsync fails cleanly without an Xbox Live context", reportRecent.Failed);

        var reportNone = await activity.ReportRecentPlayersAsync([]).ConfigureAwait(false);
        Check("ReportRecentPlayersAsync succeeds with nothing to report", reportNone.Succeeded);

        var presence = await activity.SetPresenceAsync("Playing").ConfigureAwait(false);
        Check("SetPresenceAsync fails cleanly without an Xbox Live context", presence.Failed);

        // Both are documented as "nothing to do" no-ops rather than failures.
        var deleteActivity = await activity.DeleteActivityAsync().ConfigureAwait(false);
        Check("DeleteActivityAsync succeeds with nothing published", deleteActivity.Succeeded);

        var flush = await activity.FlushRecentPlayersAsync().ConfigureAwait(false);
        Check("FlushRecentPlayersAsync succeeds with nothing queued", flush.Succeeded);

        // Never registered (no bridge exists), so this must be a harmless no-op.
        activity.Shutdown();
        Check("Shutdown() does not throw when never registered", true);
    }

    /// <summary>
    /// A configured GDK provider should expose its own cloud-save service directly, not
    /// delegate to the offline fallbacks. Without a signed-in Xbox user it cannot
    /// succeed; what it must not do is quietly report success or behave like the offline
    /// stub.
    /// </summary>
    /// <remarks>
    /// The expected failure differs per backend, and that difference is the point:
    /// connected storage has no user handle and no Gaming Runtime here, so it is
    /// unavailable, whereas the PlayFab tier gets as far as looking for credentials it
    /// does not have. See <c>NetRumbleCloudSave</c> in <c>Directory.Build.props</c>.
    /// </remarks>
    private static async Task OnlineServiceDegradePaths()
    {
        await using var provider = new GameCorePlatformProvider(
            playFab: new PlayFabConfiguration { TitleId = "A1B2C" });

        var save = await provider.GameSaves
            .SaveCloudAsync("settings", "blob"u8.ToArray())
            .ConfigureAwait(false);

#if NETRUMBLE_PLAYFAB_GAMESAVE
        Check("GameCore cloud save uses the PlayFab session", save.Status == PlatformStatus.NotSignedIn);
#else
        Check(
            "GameCore cloud save is unavailable without connected storage",
            save.Status == PlatformStatus.Unavailable);
#endif
    }

    private static bool Check(string label, bool condition)
    {
        Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
        return condition;
    }
}
