using System.Text.Json;

namespace NetRumble.Platform;

/// <summary>
/// The Xbox Services service configuration id (SCID) that XSAPI is initialized with
/// (XR-055).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not derived.</b> The SCID used to be computed as
/// <c>00000000-0000-0000-0000-0000{titleId:x8}</c> from the title id. That shape is the
/// convention Microsoft's own samples use, but it is a convention, not a rule: a title
/// whose service configuration was created separately has an unrelated GUID, and a
/// derived value that is wrong fails at the first achievement write rather than at
/// initialization - which is the worst place for it, because every XR that depends on
/// Xbox Services (achievements, presence, activities, recent players, reputation) then
/// fails together and for a reason none of them report. The SCID is deployment
/// configuration and is now treated as such.
/// </para>
/// <para>
/// <b>Where it comes from,</b> in order: the <c>--xbl-scid=</c> command line switch, the
/// <see cref="EnvironmentVariable"/> environment variable, then <c>xboxservices.config</c>
/// staged beside the executable - which is the file the GDK itself defines for this
/// purpose and the one a packaged build ships. If none of those supplies a value, the
/// title-id derivation is used as a last resort and <see cref="IsDerived"/> is set, so
/// callers can say so rather than silently pretending to be configured.
/// </para>
/// </remarks>
public sealed record XboxServicesConfiguration
{
    /// <summary>Environment variable checked when no SCID is passed on the command line.</summary>
    public const string EnvironmentVariable = "NETRUMBLE_XBL_SCID";

    /// <summary>The GDK's own name for the file that carries the SCID.</summary>
    public const string FileName = "xboxservices.config";

    private const string CommandLineSwitch = "--xbl-scid=";

    /// <summary>The service configuration id to initialize XSAPI with.</summary>
    public required string Scid { get; init; }

    /// <summary>
    /// True when <see cref="Scid"/> was derived from the title id rather than configured.
    /// </summary>
    /// <remarks>
    /// Not fatal - the derivation is correct for a title using the default service
    /// configuration, which this one may well be - but it is unproven, so it is reported
    /// once at startup and named in any Xbox Services failure that follows.
    /// </remarks>
    public bool IsDerived { get; init; }

    /// <summary>Where the value came from, for logs.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Resolves the SCID for a title id: command line, environment, config file, then
    /// the title-id derivation.
    /// </summary>
    /// <param name="titleId">The Xbox title id, used only for the last-resort derivation.</param>
    /// <param name="commandLineArgs">The process arguments, or null to skip that source.</param>
    /// <param name="baseDirectory">
    /// Where to look for <see cref="FileName"/>. Defaults to the directory the executable
    /// was loaded from, which is where the packaging script stages it.
    /// </param>
    public static XboxServicesConfiguration Resolve(
        uint titleId,
        IReadOnlyList<string>? commandLineArgs = null,
        string? baseDirectory = null)
    {
        var fromCommandLine = ReadCommandLine(commandLineArgs);
        if (fromCommandLine.Length > 0)
        {
            return new XboxServicesConfiguration
            {
                Scid = fromCommandLine,
                Source = CommandLineSwitch,
            };
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return new XboxServicesConfiguration
            {
                Scid = fromEnvironment,
                Source = EnvironmentVariable,
            };
        }

        var fromFile = ReadConfigFile(baseDirectory ?? AppContext.BaseDirectory);
        if (fromFile.Length > 0)
        {
            return new XboxServicesConfiguration
            {
                Scid = fromFile,
                Source = FileName,
            };
        }

        return new XboxServicesConfiguration
        {
            Scid = Derive(titleId),
            IsDerived = true,
            Source = "the title id",
        };
    }

    /// <summary>
    /// The sample convention: a GUID whose last group is the eight-hex-digit title id,
    /// zero-padded on the left to fill the twelve hex digits that group holds.
    /// </summary>
    public static string Derive(uint titleId) => $"00000000-0000-0000-0000-0000{titleId:x8}";

    private static string ReadCommandLine(IReadOnlyList<string>? commandLineArgs)
    {
        if (commandLineArgs is null)
        {
            return string.Empty;
        }

        foreach (var arg in commandLineArgs)
        {
            if (arg.StartsWith(CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return arg[CommandLineSwitch.Length..].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Reads <c>PrimaryServiceConfigId</c> out of the GDK's <c>xboxservices.config</c>.
    /// </summary>
    /// <remarks>
    /// Parsed with <see cref="JsonDocument"/> rather than a deserialiser so that no
    /// reflection or source-generated context is needed - this assembly is compiled into
    /// a NativeAOT console build. A missing, unreadable or malformed file is simply "no
    /// value here"; it is not worth failing startup over, because the caller still has
    /// the derivation and reports having used it.
    /// </remarks>
    private static string ReadConfigFile(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("PrimaryServiceConfigId", out var scid)
                && scid.ValueKind == JsonValueKind.String
                    ? scid.GetString()?.Trim() ?? string.Empty
                    : string.Empty;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or ArgumentException)
        {
            return string.Empty;
        }
    }
}
