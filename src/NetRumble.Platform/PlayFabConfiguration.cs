namespace NetRumble.Platform;

/// <summary>
/// The PlayFab title the game talks to.
/// </summary>
/// <remarks>
/// <para>
/// A title id is not a secret - it appears in every client build and in the URL of every
/// request - but it is deployment configuration, so command-line and environment
/// overrides still win over the built-in sample title.
/// </para>
/// <para>
/// Nothing here contacts PlayFab. This type only decides <i>which</i> title to name, and
/// tells callers whether that name is real yet, so online entry points can refuse with an
/// honest message instead of failing somewhere deep in the native SDK.
/// </para>
/// </remarks>
public sealed record PlayFabConfiguration
{
    /// <summary>
    /// The PlayFab title used by this build.
    /// </summary>
    /// <remarks>
    /// This title is not a secret. Developers can still select a different title with
    /// the command-line or environment overrides below.
    /// </remarks>
    public const string DefaultTitleId = "11C951";

    /// <summary>
    /// Stands in until a real title id is issued.
    /// </summary>
    /// <remarks>
    /// Deliberately not a plausible-looking id. Title ids are short hex-ish strings, so a
    /// realistic placeholder would be easy to mistake for a working one in a log; this
    /// cannot be. It is still a legal length, so it exercises the same code paths.
    /// </remarks>
    public const string PlaceholderTitleId = "PLACEHOLDER";

    /// <summary>Environment variable checked when no id is passed on the command line.</summary>
    public const string EnvironmentVariable = "NETRUMBLE_PLAYFAB_TITLE_ID";

    private const string CommandLineSwitch = "--playfab-title=";

    /// <summary>The PlayFab title id to use.</summary>
    public required string TitleId { get; init; }

    /// <summary>
    /// True when <see cref="TitleId"/> is still the placeholder.
    /// </summary>
    /// <remarks>
    /// Online features must check this and decline up front. Handing the placeholder to
    /// PlayFab would produce an authentication failure several async hops later, which is
    /// a far worse thing to have to diagnose than a refusal at the door.
    /// </remarks>
    public bool IsPlaceholder => TitleId == PlaceholderTitleId;

    /// <summary>
    /// Resolves the title id: command line first, then environment, then the sample title.
    /// </summary>
    /// <remarks>
    /// Command line beats environment so a single machine can run two builds against two
    /// titles at once, which is exactly what testing a host and a client side by side
    /// needs.
    /// </remarks>
    public static PlayFabConfiguration Resolve(IReadOnlyList<string>? commandLineArgs = null)
    {
        return new PlayFabConfiguration { TitleId = ResolveTitleId(commandLineArgs) };
    }

    private static string ResolveTitleId(IReadOnlyList<string>? commandLineArgs)
    {
        if (commandLineArgs is not null)
        {
            foreach (var arg in commandLineArgs)
            {
                if (arg.StartsWith(CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg[CommandLineSwitch.Length..].Trim();
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();

        return string.IsNullOrEmpty(fromEnvironment) ? DefaultTitleId : fromEnvironment;
    }

    /// <summary>
    /// Why online play is unavailable, or empty when the configuration is usable.
    /// </summary>
    public string UnavailableReason => IsPlaceholder
        ? "No PlayFab title id is configured. Set the " + EnvironmentVariable +
          " environment variable, or pass " + CommandLineSwitch + "<id> on the command line."
        : string.Empty;
}
