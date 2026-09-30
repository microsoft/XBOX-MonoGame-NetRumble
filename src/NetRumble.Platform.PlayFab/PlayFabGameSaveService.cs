using System.Text.Json;
using NetRumble.Platform.Offline;

namespace NetRumble.Platform.PlayFab;

/// <summary>
/// Adds a cloud tier to <see cref="LocalFileGameSaveService"/> using PlayFab user data.
/// </summary>
/// <remarks>
/// <para>
/// Inherits rather than reimplements, which is what the base class asks for: the local
/// tier backs settings and match history, both of which are read before sign-in
/// completes, so it must behave identically whether or not a cloud tier exists.
/// Everything here is additive.
/// </para>
/// <para>
/// <b>Size limit.</b> <c>/Client/UpdateUserData</c> caps each value at 1,000 characters
/// by default. Blobs are Base64-encoded, which costs a third on top, so the usable
/// payload is around 750 bytes - see <see cref="MaxCloudBytes"/>. That is plenty for
/// settings and a short match history and nowhere near enough for a general save
/// system, so oversized writes are refused up front with a diagnostic that says why.
/// Failing at the door beats a 400 from PlayFab that reads as a generic bad request.
/// </para>
/// <para>
/// <b>Not the same thing as GDK Game Save.</b> The contract's cloud tier is satisfied
/// here by PlayFab, which works on any platform and needs no console. A real Xbox title
/// would use the GDK's connected storage for certified save data; that path needs the
/// XGameSave flat API and is not what this implements. The distinction matters if this
/// ever ships on console, so it is stated rather than left to be discovered.
/// </para>
/// </remarks>
internal sealed class PlayFabGameSaveService : LocalFileGameSaveService
{
    /// <summary>
    /// Largest blob that fits PlayFab's 1,000-character value limit once Base64-encoded.
    /// </summary>
    /// <remarks>
    /// Base64 turns n bytes into ceil(n/3)*4 characters. 750 bytes encodes to exactly
    /// 1,000, so this is the true ceiling rather than a round number chosen for looks.
    /// </remarks>
    public const int MaxCloudBytes = 750;

    private readonly PlayFabRestClient _client;
    private readonly Func<PlayFabSession?> _session;

    public PlayFabGameSaveService(
        PlayFabRestClient client,
        Func<PlayFabSession?> session,
        string? root = null,
        Func<string?>? currentUserId = null)
        : base(root, currentUserId)
    {
        _client = client;
        _session = session;
    }

    public override async Task<PlatformResult> SaveCloudAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "Could not save your data.",
                "SaveCloudAsync called with an empty key.");
        }

        if (data.Length > MaxCloudBytes)
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "That save is too large to store online.",
                $"SaveCloudAsync got {data.Length} bytes for '{key}'; PlayFab user data " +
                $"allows {MaxCloudBytes} bytes once Base64-encoded.");
        }

        var result = await _client.PostAsync(
            "/Client/UpdateUserData",
            new UpdateUserDataRequest
            {
                // PlayFab user data values are strings, so arbitrary bytes have to be
                // encoded. Base64 rather than UTF-8: save blobs are not text, and any
                // byte sequence that is not valid UTF-8 would be silently mangled.
                Data = new Dictionary<string, string> { [key] = Convert.ToBase64String(data.Span) },
            },
            PlayFabJsonContext.Default.UpdateUserDataRequest,
            PlayFabAuth.SessionTicket,
            _session(),
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? PlatformResult.Ok()
            : PlatformResult.Fail(
                result.Status,
                "Could not save your data online.",
                result.Result.Diagnostics);
    }

    public override async Task<PlatformResult<byte[]>> LoadCloudAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return PlatformResult<byte[]>.Fail(
                PlatformStatus.Failed,
                "Could not read your saved data.",
                "LoadCloudAsync called with an empty key.");
        }

        var result = await _client.PostAsync(
            "/Client/GetUserData",
            new GetUserDataRequest { Keys = [key] },
            PlayFabJsonContext.Default.GetUserDataRequest,
            PlayFabAuth.SessionTicket,
            _session(),
            cancellationToken).ConfigureAwait(false);

        if (result.Failed)
        {
            return PlatformResult<byte[]>.Fail(
                result.Status,
                "Could not read your saved data.",
                result.Result.Diagnostics);
        }

        return ReadUserDataValue(result.Value, key);
    }

    /// <summary>
    /// Pulls one key's bytes out of a <c>GetUserData</c> response.
    /// </summary>
    /// <remarks>
    /// Internal so the spike can prove the nesting (<c>Data.key.Value</c>, not
    /// <c>Data.key</c>), the absent-key case, and - importantly - that a corrupt
    /// Base64 value is reported rather than thrown, since <c>Convert.FromBase64String</c>
    /// throws on malformed input and this runs while loading a player's settings.
    /// </remarks>
    internal static PlatformResult<byte[]> ReadUserDataValue(JsonElement data, string key)
    {
        if (!data.TryGetProperty("Data", out var bag)
            || bag.ValueKind != JsonValueKind.Object
            || !bag.TryGetProperty(key, out var record)
            || record.ValueKind != JsonValueKind.Object)
        {
            // Absent is not an error, exactly as in the local tier: a player who has
            // never saved has no record, and first run must not look like a failure.
            return PlatformResult<byte[]>.Ok(Array.Empty<byte>());
        }

        var encoded = PlayFabRestClient.ReadString(record, "Value");
        if (encoded.Length == 0)
        {
            return PlatformResult<byte[]>.Ok(Array.Empty<byte>());
        }

        try
        {
            return PlatformResult<byte[]>.Ok(Convert.FromBase64String(encoded));
        }
        catch (FormatException ex)
        {
            // Someone wrote this key by another route - Game Manager, a CloudScript
            // handler, an older build - and it is not our encoding. Say so plainly;
            // treating it as empty would silently discard the player's real data.
            return PlatformResult<byte[]>.Fail(
                PlatformStatus.Failed,
                "Your saved data could not be read.",
                $"User data key '{key}' is not Base64: {ex.Message}");
        }
    }

    internal sealed class UpdateUserDataRequest
    {
        public required IReadOnlyDictionary<string, string> Data { get; init; }
    }

    internal sealed class GetUserDataRequest
    {
        public required IReadOnlyList<string> Keys { get; init; }
    }
}
