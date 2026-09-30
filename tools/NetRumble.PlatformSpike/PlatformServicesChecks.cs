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
/// hard requirement: that every one of these services degrades cleanly - never an
/// exception, and exactly the documented outcome, whether that is a failed
/// <see cref="PlatformResult"/> or a deliberate fail-open success - when there is no
/// signed-in Xbox account, which is exactly the state of this dev box and a supported
/// configuration on a real one too (see <c>docs/design-notes.md</c>).
/// </para>
/// <para>
/// Not every check below expects failure, and not every check expects the same thing
/// from every provider. Several of these services are documented, in their own remarks,
/// to fail <i>open</i> rather than closed when no platform context is available at all -
/// friends queries, text verification and privacy evaluation all follow the Godot
/// source's "nobody to check against, so let it through" contract. Asserting the wrong
/// direction here would be worse than not asserting anything, so each check below names
/// which contract it expects and why.
/// </para>
/// <para>
/// <b>Expectations are derived from the provider, not hard-coded.</b> This class used to
/// assert GDK behaviour unconditionally, which made <c>--platform=offline</c> report
/// false failures: <c>OfflinePrivacyService.RefreshListsAsync</c> correctly succeeds,
/// because there are no mute or avoid lists to fetch offline and therefore nothing that
/// could fail, whereas <c>GameCorePrivacyService</c> correctly fails, because it cannot
/// reach lists it is supposed to have. Both are right for their provider. Each check
/// that differs now asks <see cref="IPlatformProvider.Capabilities"/> whether this
/// provider claims to back the service at all, and asserts the matching contract - so
/// the harness tests the provider it was given rather than the one it assumed.
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
        await CheckLocalSaveScoping().ConfigureAwait(false);

        Console.WriteLine();
    }

    /// <summary>
    /// The local tier's per-account scoping, and the read-only fallback that keeps a
    /// build which changes its scoping key from looking like a factory reset (XR-052).
    /// </summary>
    private static async Task CheckLocalSaveScoping()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "netrumble-spike-save-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var payload = "settings-blob"u8.ToArray();

            var legacy = new NetRumble.Platform.Offline.LocalFileGameSaveService(
                root, () => "playfab-entity");
            await legacy.SaveLocalAsync("settings", payload).ConfigureAwait(false);

            // Same account, new scoping key, no fallback: a different folder, so nothing
            // to read. This is the regression the fallback exists to prevent.
            var bare = new NetRumble.Platform.Offline.LocalFileGameSaveService(
                root, () => "xuid-1234");
            Check(
                "a new scoping key alone sees no saved data",
                (await bare.LoadLocalAsync("settings").ConfigureAwait(false)).Value?.Length == 0);

            var migrating = new NetRumble.Platform.Offline.LocalFileGameSaveService(
                root, () => "xuid-1234", () => "playfab-entity");
            Check(
                "the legacy key is read when the current one has nothing",
                (await migrating.LoadLocalAsync("settings").ConfigureAwait(false))
                    .Value?.AsSpan().SequenceEqual(payload) == true);

            // One-way: the next save lands under the current key, and from then on the
            // legacy folder is superseded rather than kept in step.
            var updated = "new-settings"u8.ToArray();
            await migrating.SaveLocalAsync("settings", updated).ConfigureAwait(false);

            Check(
                "a save after the fallback writes under the current key",
                (await bare.LoadLocalAsync("settings").ConfigureAwait(false))
                    .Value?.AsSpan().SequenceEqual(updated) == true);

            Check(
                "the legacy copy is left untouched",
                (await legacy.LoadLocalAsync("settings").ConfigureAwait(false))
                    .Value?.AsSpan().SequenceEqual(payload) == true);

            // Still no on-disk bucket before anyone is signed in.
            var anonymous = new NetRumble.Platform.Offline.LocalFileGameSaveService(
                root, () => null, () => "playfab-entity");
            Check(
                "an unsigned session reads nothing from either key",
                (await anonymous.LoadLocalAsync("settings").ConfigureAwait(false)).Value?.Length == 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CheckPrivileges(IPrivilegeService privileges)
    {
        // Multiplayer/Communications: no signed-in user means XUserCheckPrivilege has
        // no XUserHandle to check, so the service must fail rather than guess. This is
        // the fail-open-vs-fail-closed boundary called out in privilege_service.gd: the
        // *call* fails closed (deny), but the *feature gate* built on top of it is what
        // decides whether that is treated as "assume allowed" (offline single-player
        // still works) or "assume denied" (do not let comms silently open).
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
        // player-authored text must not be published unchecked. The offline provider has
        // no XBOX network to publish to and keeps its passthrough contract.
        var expectPassthrough = platform.Moderation.GetType().Name != "GameCoreModerationService";
        var verify = await moderation.VerifyTextAsync("hello there").ConfigureAwait(false);
        Check("Text verification holds text back without an Xbox Services context",
            expectPassthrough
                ? verify.Succeeded && verify.Value == "hello there"
                : verify.Failed && verify.Status == PlatformStatus.Unavailable);
        Console.WriteLine($"    ---- {verify}");

        // Reporting and profile cards are the opposite direction from verification:
        // both reach out to a service, so neither may report success it did not get,
        // whether the provider lacks a context (GDK) or lacks the service (offline).
        var report = await moderation.ReportPlayerAsync("0", PlayerReportType.Cheating).ConfigureAwait(false);
        Check("Player report does not silently succeed without a signed-in user", report.Failed);

        var card = await moderation.ShowProfileCardAsync("0").ConfigureAwait(false);
        Check("Profile card does not silently succeed without a signed-in user", card.Failed);
    }

    private static async Task CheckPrivacy(IPlatformProvider platform)
    {
        var privacy = platform.Privacy;

        // The one genuinely provider-dependent contract in this file, and the reason
        // --platform=offline used to report a false failure. A provider that backs
        // privacy has real mute and avoid lists to fetch and must fail when it cannot
        // reach them; a provider that does not has no lists at all, so there is nothing
        // to fail at and succeeding is the honest answer. Assert whichever one this
        // provider signed up for.
        var backsPrivacy = platform.Capabilities.HasFlag(PlatformCapabilities.Privacy);
        var refresh = await privacy.RefreshListsAsync().ConfigureAwait(false);

        Check(
            backsPrivacy
                ? "Mute/avoid list refresh fails cleanly without a signed-in user"
                : "Mute/avoid list refresh succeeds trivially on a provider with no lists",
            backsPrivacy ? refresh.Failed : refresh.Succeeded);
        Console.WriteLine($"    ---- {refresh}");

        // Fails closed (XR-045/XR-046). The GDK provider is only reached on a live Xbox
        // session, where an unreachable privacy service means the title cannot show that
        // peer-to-peer communication is permitted - so it must not happen. Offline and
        // LAN play never come through here; they reach OfflinePrivacyService, which still
        // allows all.
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

        // ShowTextEntryAsync and ShowAccountPickerAsync both surface real, modal GDK
        // UI (XGameUiShowTextEntryAsync / XUserAddAsync) that only ever completes when
        // a person interacts with it or a console shell dismisses it - and completion
        // is only delivered through Pump(), which nothing is driving outside the main
        // Program loop's PumpUntil helper. Calling either here, unattended, could hang
        // the harness rather than fail it, which is worse than not testing it at all.
        // Marked SKIP rather than exercised - see docs/design-notes.md for what a real
        // verification of this would need (an attended run with a signed-in profile).
        Console.WriteLine(
            "    SKIP ShowTextEntryAsync/ShowAccountPickerAsync need an attended session " +
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
