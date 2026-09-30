using System.Text.Json;
using NetRumble.Platform;

namespace NetRumble.Game.Profile;

/// <summary>
/// Syncs <see cref="PlayerProfile"/>'s settings to the cloud tier of
/// <see cref="IGameSaveService"/>, which on this port is PlayFab user
/// data.
/// </summary>
/// <remarks>
/// <para>
/// <b>The local tier is not this class's job.</b> <see cref="PlayerProfile"/> already
/// owns an INI file under local app data, keeping Godot's <c>ConfigFile</c> section and
/// key names so either port can read the other's settings. That file stays the local
/// tier - it is read before sign-in and must keep working for a player who never signs
/// in at all. This class adds the second tier on top and nothing else.
/// </para>
/// <para>
/// <b>Why the payload is <see cref="PlayerProfile.ToDictionary"/> and not the INI.</b>
/// The dictionary uses the C++ sample's <c>SettingKeyToString()</c> names, so a cloud
/// record written here is interchangeable with the C++ and Godot builds' - which is the
/// entire point of putting settings in the cloud for a title that ships on three
/// engines. The INI's Godot-shaped names are a local-file detail.
/// </para>
/// <para>
/// <b>Size.</b> PlayFab caps a user-data value at 1,000 characters, which is 750 bytes
/// once Base64-encoded. This payload is ten scalars and lands around 200 bytes, so it
/// fits with room to spare - and <c>PlayFabGameSaveService</c> refuses an oversized write
/// up front rather than letting the service return a bare 400. Match history is
/// deliberately *not* synced for exactly this reason; see <see cref="MatchHistoryStore"/>.
/// </para>
/// </remarks>
public sealed class CloudSettingsSync
{
    /// <summary>
    /// The user-data key. Matches the C++ sample's save name, so the three builds share
    /// one record rather than each writing its own.
    /// </summary>
    public const string SaveKey = "settings";

    private readonly IGameSaveService _saves;
    private readonly PlayerProfile _profile;

    public CloudSettingsSync(IGameSaveService saves, PlayerProfile profile)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>
    /// Pulls the cloud record over the local one and writes the result back to disk.
    /// Returns true only when a record actually existed and applied.
    /// </summary>
    /// <remarks>
    /// Cloud wins on sign-in. The alternative - merging, or preferring the newer of the
    /// two - needs a timestamp this payload does not carry and a conflict UI the source
    /// never had; taking the cloud copy is what makes signing in on a second console
    /// bring your settings with you, which is the only reason this tier exists.
    /// An absent record, an offline provider and a malformed blob are all "nothing to
    /// apply", not failures: the local settings simply stand.
    /// </remarks>
    public async Task<bool> PullAsync(CancellationToken cancellationToken = default)
    {
        var result = await _saves.LoadCloudAsync(SaveKey, cancellationToken);

        if (result.Failed || result.Value is not { Length: > 0 } bytes)
        {
            return false;
        }

        var values = TryParse(bytes);

        if (values is null)
        {
            return false;
        }

        // Applying writes the local file too, so the two tiers agree straight away
        // rather than only after the player next opens Options. The guard stops that
        // save from immediately pushing the record we have just pulled back up.
        _profile.SuppressCloudPush = true;

        try
        {
            _profile.ApplyDictionary(values);
            _profile.Save();
        }
        finally
        {
            _profile.SuppressCloudPush = false;
        }

        return true;
    }

    /// <summary>
    /// Fire-and-forget push. Called by <see cref="PlayerProfile.Save"/>, so every place
    /// that already commits settings to disk commits them to the cloud too.
    /// </summary>
    public void Push() => _ = PushAsync();

    /// <summary>Pushes the current settings. Best-effort: a failure is not surfaced.</summary>
    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _saves.SaveCloudAsync(SaveKey, Serialise(_profile.ToDictionary()), cancellationToken);
        }
        catch (Exception)
        {
            // Settings are already safe on disk. Losing the cloud copy of a volume
            // slider is not worth a dialog, let alone an unhandled exception on a
            // thread nobody is awaiting.
        }
    }

    /// <summary>
    /// Writes the payload by hand rather than through <c>JsonSerializer</c>. The GDKX
    /// build publishes Native AOT, where the reflection-based serialiser is unavailable,
    /// and a flat bag of ten scalars does not justify a source-generated context.
    /// </summary>
    private static byte[] Serialise(IReadOnlyDictionary<string, object> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var (key, value) in values)
            {
                switch (value)
                {
                    case bool flag:
                        writer.WriteBoolean(key, flag);
                        break;
                    case int number:
                        writer.WriteNumber(key, number);
                        break;
                    case float number:
                        writer.WriteNumber(key, number);
                        break;
                    default:
                        writer.WriteString(key, value.ToString());
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the payload back into the shape <see cref="PlayerProfile.ApplyDictionary"/>
    /// expects, which reads every value through <see cref="IConvertible"/> - so a bool
    /// boxed here still answers a float read there.
    /// </summary>
    private static Dictionary<string, object>? TryParse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Number when property.Value.TryGetDouble(out var number):
                        values[property.Name] = number;
                        break;
                    case JsonValueKind.True:
                        values[property.Name] = true;
                        break;
                    case JsonValueKind.False:
                        values[property.Name] = false;
                        break;
                    default:
                        // Anything else was not written by this class. Skipping the key
                        // leaves the profile's current value in place, which is what
                        // ApplyDictionary does for an absent one anyway.
                        break;
                }
            }

            return values;
        }
        catch (JsonException)
        {
            // Someone wrote this key by another route. Keep the local settings rather
            // than clobbering them with a half-read record.
            return null;
        }
    }
}
