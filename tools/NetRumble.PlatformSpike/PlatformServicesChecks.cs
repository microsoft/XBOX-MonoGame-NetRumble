using NetRumble.Platform;

/// <summary>
/// Exercises the Phase 5 GDK-backed services (privileges, achievements, social,
/// moderation, privacy, platform UI) through the public <see cref="IPlatformProvider"/>
/// seam, rather than through raw P/Invoke.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="XblInteropChecks"/> proves the native ABI signatures are correct against
/// the real <c>Microsoft.Xbox.Services.C.Thunks.dll</c>. This class proves the other
/// hard requirement: that every one of these services reports its documented outcome
/// without throwing when there is no signed-in Xbox account. The game remains at the
/// sign-in screen in that state.
/// </para>
/// <para>
/// Social queries may return empty results, while moderation and privacy must fail
/// closed without a platform context. Each check names the contract it expects.
/// </para>
/// <para>
/// None of these checks can reach a PASS that proves a successful unlock, a real
/// friends list, or a real permission check actually round-tripped platform data - that
/// needs a signed-in account this environment does not have.
/// </para>
/// </remarks>
internal static class PlatformServicesChecks
{
    public static async Task Run(IPlatformProvider platform)
    {
        Console.WriteLine("[12] Platform services degrade cleanly without a signed-in account");

        await CheckPrivileges(platform.Privileges).ConfigureAwait(false);
        await CheckAchievements(platform).ConfigureAwait(false);
        await CheckSocial(platform.Social).ConfigureAwait(false);
        await CheckModeration(platform).ConfigureAwait(false);
        await CheckPrivacy(platform).ConfigureAwait(false);
        await CheckPlatformUi(platform).ConfigureAwait(false);

        Console.WriteLine();
    }

    private static async Task CheckPrivileges(IPrivilegeService privileges)
    {
        // Without an XUserHandle, privilege checks must deny rather than guess.
        var multiplayer = await privileges.CheckAsync(GamePrivilege.Multiplayer).ConfigureAwait(false);
        Check("Multiplayer privilege check completes without throwing", true);
        Console.WriteLine($"    ---- Multiplayer: allowed={multiplayer.Allowed} reason={multiplayer.Reason} \"{multiplayer.Message}\"");

        var comms = await privileges.CheckAsync(GamePrivilege.Communications).ConfigureAwait(false);
        Console.WriteLine($"    ---- Communications: allowed={comms.Allowed} reason={comms.Reason} \"{comms.Message}\"");
    }

    /// <summary>
    /// Unlock and progress are write operations against a live title's achievement
    /// service. A provider that backs achievements must refuse them without a signed-in
    /// user rather than pretend; a provider that does not back them at all must report
    /// that honestly. Both land on a failed <see cref="PlatformResult"/>, so this one
    /// reads the same either way - what differs is the reason, which is printed.
    /// </summary>
    private static async Task CheckAchievements(IPlatformProvider platform)
    {
        var achievements = platform.Achievements;

        var unlock = await achievements.UnlockAsync("1").ConfigureAwait(false);
        Check("Achievement unlock does not silently succeed without a signed-in user", unlock.Failed);
        Console.WriteLine($"    ---- {unlock}");

        var progress = await achievements.SetProgressAsync("2", 50).ConfigureAwait(false);
        Check("Achievement progress does not silently succeed without a signed-in user", progress.Failed);
    }

    private static async Task CheckSocial(ISocialService social)
    {
        // Fail-soft, not fail: GameCoreSocialService's own remarks document this - a
        // desktop dev machine with no Xbox account has nobody to be friends with, so an
        // empty *successful* list is returned rather than an error, matching the Godot
        // source's contract exactly.
        var friends = await social.GetFriendsAsync().ConfigureAwait(false);
        Check("Friends query succeeds with an empty list without a signed-in user",
            friends.Succeeded && friends.Value is { Count: 0 });
        Console.WriteLine($"    ---- {friends}");

        // Clear() must be safe to call with nothing cached - it is invoked on every
        // sign-out, including one that follows a sign-in that never completed.
        social.Clear();
        Check("Clear() does not throw with no cached state", true);
    }

    private static async Task CheckModeration(IPlatformProvider platform)
    {
        var moderation = platform.Moderation;

        // XR-018 fails closed on the GDK provider: no Xbox Services context there means a
        // moderation service that is not answering, not an absent audience, and
        // player-authored text must not be published unchecked.
        var verify = await moderation.VerifyTextAsync("hello there").ConfigureAwait(false);
        Check("Text verification holds text back without an Xbox Services context",
            verify.Failed && verify.Status == PlatformStatus.Unavailable);
        Console.WriteLine($"    ---- {verify}");

        // Reporting and profile cards are the opposite direction from verification:
        // both reach out to a service, so neither may report success it did not get,
        // when the provider lacks a context.
        var report = await moderation.ReportPlayerAsync("0", PlayerReportType.Cheating).ConfigureAwait(false);
        Check("Player report does not silently succeed without a signed-in user", report.Failed);

        var card = await moderation.ShowProfileCardAsync("0").ConfigureAwait(false);
        Check("Profile card does not silently succeed without a signed-in user", card.Failed);
    }

    private static async Task CheckPrivacy(IPlatformProvider platform)
    {
        var privacy = platform.Privacy;

        // A missing user cannot authorize access to mute and avoid lists.
        var refresh = await privacy.RefreshListsAsync().ConfigureAwait(false);

        Check("Mute/avoid list refresh fails cleanly without a signed-in user", refresh.Failed);
        Console.WriteLine($"    ---- {refresh}");

        // Fails closed (XR-045/XR-046). The GDK provider is only reached on a live Xbox
        // session, where an unreachable privacy service means the title cannot show that
        // peer-to-peer communication is permitted - so it must not happen.
        var evaluate = await privacy.EvaluateAsync(["0"]).ConfigureAwait(false);
        Check("Permission evaluation returns a deny-all verdict without a signed-in user",
            evaluate.Succeeded && evaluate.Value is { Count: 1 } v && v["0"] == PrivacyVerdict.DenyAll);

        // Never null in this provider, but the default is now restrictive: "nothing
        // cached yet" means nothing has been proven, which is not the same as permitted.
        Check("GetCached returns the restrictive default with nothing cached",
            privacy.GetCached("0") == PrivacyVerdict.DenyAll);

        privacy.ClearCache();
        Check("ClearCache() does not throw with nothing cached", true);
    }

    private static Task CheckPlatformUi(IPlatformProvider platform)
    {
        var ui = platform.PlatformUi;

        Console.WriteLine($"    ---- RequiresVirtualKeyboard={ui.RequiresVirtualKeyboard}");
        Check("RequiresVirtualKeyboard reads without throwing", true);

        // ShowTextEntryAsync surfaces real, modal GDK UI that only completes when
        // a person interacts with it or a console shell dismisses it - and completion
        // is only delivered through Pump(), which nothing is driving outside the main
        // Program loop's PumpUntil helper. Calling it here, unattended, could hang
        // the harness rather than fail it, which is worse than not testing it at all.
        // Marked SKIP rather than exercised - see docs/design-notes.md for what a real
        // verification of this would need (an attended run with a signed-in profile).
        Console.WriteLine(
            "    SKIP ShowTextEntryAsync needs an attended session " +
            "to drive the modal GDK UI to completion; not exercised here to avoid hanging " +
            "an unattended run. See docs/design-notes.md.");

        return Task.CompletedTask;
    }

    private static bool Check(string label, bool condition)
    {
        Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
        return condition;
    }
}
