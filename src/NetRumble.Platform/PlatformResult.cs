namespace NetRumble.Platform;

/// <summary>
/// Outcome of a platform call.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <i>not</i> an HRESULT. Native error codes are provider-specific, so
/// exposing them would leak the GDK ABI into game code and make a future
/// standalone-C#-GDK provider awkward to write. Game code branches on
/// <see cref="Status"/>; <see cref="Diagnostics"/> carries the raw detail for logs
/// only.
/// </para>
/// <para>
/// This mirrors the Godot port's policy (<c>scripts/autoload/services.gd</c>): only
/// multiplayer failures are fatal to the operation the player asked for. Everything
/// else degrades to an empty result and a warning, so the front end and practice
/// match still run on an unconfigured dev machine.
/// </para>
/// </remarks>
public readonly record struct PlatformResult
{
    private PlatformResult(PlatformStatus status, string? message, string? diagnostics)
    {
        Status = status;
        Message = message;
        Diagnostics = diagnostics;
    }

    public PlatformStatus Status { get; }

    /// <summary>Player-facing reason for a failure. Empty on success.</summary>
    public string? Message { get; }

    /// <summary>
    /// Provider-specific detail (an HRESULT string, an SDK error name). For logging
    /// and the debug overlay only — never branch on this and never show it to players.
    /// </summary>
    public string? Diagnostics { get; }

    public bool Succeeded => Status == PlatformStatus.Ok;
    public bool Failed => Status != PlatformStatus.Ok;

    /// <summary>True when the user cancelled, which the UI must not treat as an error.</summary>
    public bool WasCanceled => Status == PlatformStatus.Canceled;

    public static PlatformResult Ok() => new(PlatformStatus.Ok, null, null);

    public static PlatformResult Canceled(string? message = null)
        => new(PlatformStatus.Canceled, message, null);

    public static PlatformResult Unavailable(string? message = null)
        => new(PlatformStatus.Unavailable, message ?? "This feature is not available in this build.", null);

    public static PlatformResult Fail(
        PlatformStatus status,
        string message,
        string? diagnostics = null)
        => new(status == PlatformStatus.Ok ? PlatformStatus.Failed : status, message, diagnostics);

    public override string ToString()
        => Succeeded ? "Ok" : $"{Status}: {Message}{(Diagnostics is null ? "" : $" [{Diagnostics}]")}";
}

/// <summary>Outcome of a platform call that returns a value.</summary>
public readonly record struct PlatformResult<T>
{
    private PlatformResult(PlatformResult result, T? value)
    {
        Result = result;
        Value = value;
    }

    public PlatformResult Result { get; }
    public T? Value { get; }

    public PlatformStatus Status => Result.Status;
    public bool Succeeded => Result.Succeeded;
    public bool Failed => Result.Failed;
    public bool WasCanceled => Result.WasCanceled;
    public string? Message => Result.Message;

    public static PlatformResult<T> Ok(T value) => new(PlatformResult.Ok(), value);

    public static PlatformResult<T> Unavailable(string? message = null)
        => new(PlatformResult.Unavailable(message), default);

    public static PlatformResult<T> Canceled(string? message = null)
        => new(PlatformResult.Canceled(message), default);

    public static PlatformResult<T> Fail(
        PlatformStatus status,
        string message,
        string? diagnostics = null)
        => new(PlatformResult.Fail(status, message, diagnostics), default);

    /// <summary>Value on success, <paramref name="fallback"/> otherwise.</summary>
    public T? ValueOr(T? fallback) => Succeeded ? Value : fallback;

    public override string ToString() => Result.ToString();
}

public enum PlatformStatus
{
    Ok = 0,

    /// <summary>The user dismissed a platform UI. Not an error; do not show a dialog.</summary>
    Canceled,

    /// <summary>The provider does not implement this capability at all.</summary>
    Unavailable,

    /// <summary>The call needs a signed-in user and there isn't one.</summary>
    NotSignedIn,

    /// <summary>The account lacks the required privilege (XR-045).</summary>
    NoPrivilege,

    /// <summary>Network unreachable or the service is down.</summary>
    NetworkFailure,

    /// <summary>The operation ran out of time.</summary>
    TimedOut,

    /// <summary>Anything else.</summary>
    Failed,
}
