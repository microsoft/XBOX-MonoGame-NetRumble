using System.Globalization;
using System.Text.Json;
using NetRumble.Core.Net;
using NetRumble.Platform;

namespace NetRumble.Game.Profile;

/// <summary>One finished match, as the Extras screen lists it.</summary>
/// <param name="CompletedAtUtc">When the match ended.</param>
/// <param name="GameMode">Display name of the mode that was played.</param>
/// <param name="Placement">One-based finishing position for the local player.</param>
/// <param name="PlayerCount">How many players finished, for "3rd of 6".</param>
/// <param name="Score">The local player's final score.</param>
public readonly record struct MatchHistoryEntry(
    DateTimeOffset CompletedAtUtc,
    string GameMode,
    int Placement,
    int PlayerCount,
    int Score)
{
    /// <summary>Local completion time shown on the first line of a history row.</summary>
    public string ToTimestamp() => CompletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Placement and score shown on the second line of a history row.</summary>
    public string ToDetail() => $"{GameMode} \u2014 {Ordinal(Placement)} of {PlayerCount}, Score: {Score}";

    /// <summary>The row text the Extras list shows.</summary>
    public string ToRow() => $"{ToTimestamp()}  {ToDetail()}";

    private static string Ordinal(int placement)
    {
        // Only ever 1..4 here, the maximum roster, so the teens special case the general
        // algorithm needs cannot arise - but it costs nothing to be correct anyway.
        var suffix = (placement % 100) is >= 11 and <= 13
            ? "th"
            : (placement % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };

        return placement.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}

/// <summary>
/// The local match history, ported from <c>Services.MATCH_HISTORY_PATH</c> - a
/// <c>user://</c> JSON file the GDScript appended to after every match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local only, deliberately.</b> This is the one save this port does not put in the
/// cloud. PlayFab caps a user-data value at 750 bytes once Base64-encoded, and a history
/// long enough to be worth reading does not fit; truncating it to what does would make
/// the cloud copy strictly worse than the local one it replaced. Settings and achievement
/// stats are small and are synced; see <see cref="CloudSettingsSync"/> and
/// <see cref="AchievementStats"/>.
/// </para>
/// <para>
/// The cap exists for the same reason the source's did: this is a list nobody scrolls to
/// the end of, and an unbounded append would grow a save file forever.
/// </para>
/// </remarks>
public sealed class MatchHistoryStore
{
    /// <summary>The save key.</summary>
    public const string SaveKey = "match_history";

    /// <summary>How many matches are kept, newest first.</summary>
    public const int MaxEntries = 25;

    private readonly IGameSaveService _saves;

    public MatchHistoryStore(IGameSaveService saves)
        => _saves = saves ?? throw new ArgumentNullException(nameof(saves));

    /// <summary>Reads the history, newest first. A missing or unreadable file is empty.</summary>
    public async Task<IReadOnlyList<MatchHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _saves.LoadLocalAsync(SaveKey, cancellationToken);

        return result.Failed || result.Value is not { Length: > 0 } bytes
            ? []
            : TryParse(bytes) ?? [];
    }

    /// <summary>
    /// Records a finished match for the local player, if they were in the standings.
    /// Best-effort: a failed write is not surfaced.
    /// </summary>
    public async Task RecordAsync(
        MatchResult result,
        int localPeerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.StandingFor(localPeerId) is not { } standing)
        {
            return;
        }

        var entry = new MatchHistoryEntry(
            DateTimeOffset.UtcNow,
            result.GameMode,
            standing.Placement,
            result.Standings.Count,
            standing.Score);

        var entries = new List<MatchHistoryEntry>(await LoadAsync(cancellationToken));
        entries.Insert(0, entry);

        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        await _saves.SaveLocalAsync(SaveKey, Serialise(entries), cancellationToken);
    }

    /// <summary>
    /// Hand-written rather than <c>JsonSerializer</c>, because the GDKX build publishes
    /// Native AOT and the reflection-based serialiser is unavailable there.
    /// </summary>
    private static byte[] Serialise(IReadOnlyList<MatchHistoryEntry> entries)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();

            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("completedAt", entry.CompletedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("gameMode", entry.GameMode);
                writer.WriteNumber("placement", entry.Placement);
                writer.WriteNumber("playerCount", entry.PlayerCount);
                writer.WriteNumber("score", entry.Score);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return buffer.ToArray();
    }

    private static List<MatchHistoryEntry>? TryParse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var entries = new List<MatchHistoryEntry>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                entries.Add(new MatchHistoryEntry(
                    ReadDate(element, "completedAt"),
                    ReadString(element, "gameMode"),
                    ReadInt(element, "placement"),
                    ReadInt(element, "playerCount"),
                    ReadInt(element, "score")));
            }

            return entries;
        }
        catch (JsonException)
        {
            // Truncated, or written by another route. A history is not worth failing a
            // menu over; the next recorded match starts a clean one.
            return null;
        }
    }

    private static DateTimeOffset ReadDate(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                    ? parsed
                    : DateTimeOffset.UnixEpoch;

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : 0;
}
