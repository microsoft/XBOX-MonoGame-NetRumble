using NetRumble.Platform;
using NetRumble.Platform.Composition;
using NetRumble.Platform.GameCore;

namespace NetRumble.PlatformSpike;

/// <summary>Checks GDK-only composition and the sign-in requirement without opening system UI.</summary>
internal static class IdentityChecks
{
    public static async Task Run()
    {
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
        Console.WriteLine();
    }

    private static void CheckGameEntryPoints()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetRumble.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            Console.WriteLine("    SKIP could not locate game sources from the test binary");
            return;
        }

        var gameRoot = Path.Combine(directory.FullName, "src", "NetRumble.Game");
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

    private static void Check(string label, bool condition)
    {
        Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }
    }
}
