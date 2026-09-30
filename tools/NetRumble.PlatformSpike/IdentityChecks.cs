using System.Text.RegularExpressions;
using NetRumble.Platform;
using NetRumble.Platform.Composition;

namespace NetRumble.PlatformSpike;

/// <summary>
/// Asserts that the developer identity override reaches sign-in.
/// </summary>
/// <remarks>
/// <c>--pf-user=&lt;name&gt;</c> (and the <c>PF_CUSTOM_ID</c> environment variable, which
/// was documented on <see cref="SignInOptions.DeveloperCustomId"/> but read nowhere) does
/// two jobs. It gives each instance its own settings file, and it becomes the identity the
/// instance signs in with. Only the first was wired: both call sites passed a bare
/// <c>SignInOptions.Interactive</c>, so <see cref="SignInOptions.DeveloperCustomId"/> was
/// always null.
///
/// Two consequences, neither visible to any check that existed. Every instance signed in
/// as "Player", so a LAN match showed two identical names in the roster, the scoreboard
/// and the standings and no one could tell the players apart. And the PlayFab custom-id
/// sign-in path - the one documented as a complete working online identity on any dev box
/// - returns early when the id is empty, so it could never run: dead code reachable only
/// through an argument nothing forwarded.
///
/// The providers were always correct. The caller was not, which is why this checks the
/// call sites as text as well as the behaviour.
/// </remarks>
internal static class IdentityChecks
{
    /// <summary>
    /// Finds a sign-in that passes the shared options struct directly instead of the
    /// context helper that carries the override.
    /// </summary>
    private static readonly Regex BareSignIn = new(
        @"SignInAsync\(\s*SignInOptions\.",
        RegexOptions.Compiled);

    public static async Task Run()
    {
        Console.WriteLine("[17] Developer identity override");

        await OverrideReachesTheProvider().ConfigureAwait(false);
        CallSitesForwardTheOverride();

        Console.WriteLine();
    }

    /// <summary>
    /// The offline provider must adopt the supplied id as the display name, and must still
    /// fall back to "Player" without one.
    /// </summary>
    private static async Task OverrideReachesTheProvider()
    {
        await using (var platform = PlatformProviderFactory.Create(PlatformProviderMode.Offline))
        {
            var result = await platform.Identity
                .SignInAsync(SignInOptions.Interactive with { DeveloperCustomId = "\u00c5ke" })
                .ConfigureAwait(false);

            var name = result.Value?.DisplayName ?? string.Empty;

            Check($"the override becomes the display name (got \"{name}\")", name == "\u00c5ke");
            Check("the user is flagged as a developer override", result.Value?.IsDeveloperOverride == true);
        }

        await using (var platform = PlatformProviderFactory.Create(PlatformProviderMode.Offline))
        {
            var result = await platform.Identity
                .SignInAsync(SignInOptions.Interactive)
                .ConfigureAwait(false);

            var name = result.Value?.DisplayName ?? string.Empty;

            Check($"no override still falls back to Player (got \"{name}\")", name == "Player");
            Check("an anonymous user is not flagged as an override", result.Value?.IsDeveloperOverride == false);
        }

        // Two instances with different overrides must be distinguishable, which is the
        // whole point: a roster showing "Player" twice is what the defect looked like.
        await using var host = PlatformProviderFactory.Create(PlatformProviderMode.Offline);
        await using var join = PlatformProviderFactory.Create(PlatformProviderMode.Offline);

        var hostUser = await host.Identity
            .SignInAsync(SignInOptions.Interactive with { DeveloperCustomId = "hostbot" })
            .ConfigureAwait(false);
        var joinUser = await join.Identity
            .SignInAsync(SignInOptions.Interactive with { DeveloperCustomId = "clientbot" })
            .ConfigureAwait(false);

        Check(
            "two instances with different overrides get different names",
            hostUser.Value?.DisplayName != joinUser.Value?.DisplayName);
    }

    /// <summary>
    /// Guards the half that behaviour cannot see: whether the game actually forwards the
    /// override. A provider that honours the id is useless if nobody supplies it.
    /// </summary>
    private static void CallSitesForwardTheOverride()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            Console.WriteLine("    SKIP could not locate NetRumble.slnx from the test binary");
            return;
        }

        var sourceDir = Path.Combine(root, "src", "NetRumble.Game");
        if (!Directory.Exists(sourceDir))
        {
            Check("found the game sources", false);
            return;
        }

        var offenders = new List<string>();
        var callSites = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;

                if (!line.Contains("SignInAsync(", StringComparison.Ordinal))
                {
                    continue;
                }

                callSites++;

                if (BareSignIn.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{lineNumber}");
                }
            }
        }

        // If this hits zero the scan has broken rather than the game: the front end has to
        // sign in somewhere.
        Check($"found the sign-in call sites (got {callSites})", callSites > 0);

        Check(
            offenders.Count == 0
                ? "every sign-in call site forwards the developer override"
                : $"these sign-in call sites drop the developer override: {string.Join(", ", offenders)}",
            offenders.Count == 0);
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetRumble.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
