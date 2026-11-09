using System.Text.RegularExpressions;
using System.Xml.Linq;
using NetRumble.Platform;
using NetRumble.Platform.Composition;
using NetRumble.Platform.GameCore;

namespace NetRumble.PlatformSpike;

/// <summary>Checks GDK-only composition and the sign-in requirement without opening system UI.</summary>
internal static class IdentityChecks
{
    private static int _failures;

    public static async Task<int> Run()
    {
        _failures = 0;
        Console.WriteLine("[17] Required GDK identity");

        foreach (var mode in Enum.GetValues<PlatformProviderMode>())
        {
            await using var platform = PlatformProviderFactory.Create(mode);
            Check($"{mode} constructs the GDK provider", platform is GameCorePlatformProvider);
            Check($"{mode} starts without a fabricated user", platform.Identity.CurrentUser is null);
            foreach (var options in new[] { SignInOptions.Silent, SignInOptions.Interactive })
            {
                var result = await platform.Identity.SignInAsync(options).ConfigureAwait(false);
                Check($"{mode} refuses sign-in before GDK initialization",
                    result.Status == PlatformStatus.Unavailable && result.Value is null
                    && platform.Identity.CurrentUser is null && !platform.Identity.IsSignInInProgress);
            }
        }

        var rejected = false;
        try
        {
            await using var invalid = PlatformProviderFactory.Create((PlatformProviderMode)1);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        Check("an unsupported provider cannot construct a fallback", rejected);

        CheckGameEntryPoints();
        SimplifiedUserModel();
        Console.WriteLine();
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckGameEntryPoints()
    {
        var root = FindRepoRoot();
        Check("found game sources for required-sign-in checks", root is not null);
        if (root is null)
        {
            return;
        }

        var gameRoot = Path.Combine(root, "src", "NetRumble.Game");
        var acquireUser = File.ReadAllText(Path.Combine(gameRoot, "UI", "Screens", "AcquireUserScreen.cs"));
        var game = File.ReadAllText(Path.Combine(gameRoot, "NetRumbleGame.cs"));
        var autopilot = File.ReadAllText(Path.Combine(gameRoot, "Autopilot.cs"));
        var menu = File.ReadAllText(Path.Combine(gameRoot, "UI", "Screens", "MainMenuScreen.cs"));

        Check("sign-in uses the platform's interactive options",
            acquireUser.Contains("SignInAsync(SignInOptions.Interactive)", StringComparison.Ordinal));
        Check("the game gates screen updates on a current platform user",
            game.Contains("_platform.Identity.CurrentUser is null && _screens.Find<AcquireUserScreen>() is null",
                StringComparison.Ordinal));
        Check("autopilot requests normal sign-in rather than replacing the menu",
            autopilot.Contains("acquireUser.BeginSignIn(context)", StringComparison.Ordinal)
            && !autopilot.Contains("new MainMenuScreen()", StringComparison.Ordinal));
        Check("Practice remains available to the signed-in player",
            menu.Contains("new LobbyScreen(LobbyIntent.Practice)", StringComparison.Ordinal));
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

    private static void SimplifiedUserModel()
    {
        Console.WriteLine("    ---- Simplified user model");
        var root = FindRepoRoot();
        Check("found repository for user-model contract checks", root is not null);
        if (root is null)
        {
            return;
        }

        string[] manifests =
        [
            "MicrosoftGame.config",
            "MicrosoftGame.PC.config",
            "MicrosoftGame.GDKX.Scarlett.config",
            "MicrosoftGame.GDKX.XboxOne.config",
        ];
        foreach (var manifest in manifests)
        {
            var game = XDocument.Load(Path.Combine(root, "packaging", manifest)).Root!;
            Check($"{manifest} selects the simplified user model",
                (string?)game.Element("AdvancedUserModel") == "false");
            Check($"{manifest} retains package version 1.0.3.0",
                (string?)game.Element("Identity")?.Attribute("Version") == "1.0.3.0");
        }

        Check("platform UI exposes no account-switch picker",
            typeof(IPlatformUiService).GetMethod("ShowAccountPickerAsync") is null);

        var services = Path.Combine(root, "src", "NetRumble.Platform.GameCore", "Services");
        var identity = File.ReadAllText(Path.Combine(services, "GameCoreIdentityService.cs"));
        var options = Regex.Matches(identity, @"UserAddOptions\.(\w+)");
        Check("GDK identity only requests the default user silently, with no UI fallback",
            options.Count == 1 && options[0].Groups[1].Value == "AddDefaultUserSilently");

        var activity = File.ReadAllText(Path.Combine(services, "GameCoreActivityService.cs"));
        Check("activity state does not subscribe to account replacement",
            !activity.Contains("_identity.UserChanged +=", StringComparison.Ordinal));
    }

    private static void Check(string label, bool condition)
    {
        Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
        if (!condition)
        {
            _failures++;
        }
    }
}
