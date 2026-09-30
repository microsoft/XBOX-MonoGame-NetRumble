using System.Globalization;
using System.Text;
using NetRumble.Core;
using NetRumble.Game.Audio;

namespace NetRumble.Game.Profile;

/// <summary>
/// Mirrors <c>PlayFab::VoiceChatTranscriptionMode</c>, including its ordering: the default
/// is <see cref="UsePlatformSetting"/>, not "off".
/// </summary>
public enum TranscriptionMode
{
    UsePlatformSetting,
    Enabled,
    Disabled,
}

/// <summary>
/// Local player identity plus persisted settings, ported from
/// <c>scripts/autoload/player_profile.gd</c> and originally <c>Game/PlayerProfile.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// Settings live in an INI-shaped file under local app data, keeping the section and key
/// names Godot's <c>ConfigFile</c> used so a file written by either port stays readable by
/// the other.
/// </para>
/// <para>
/// The cloud payload from <see cref="ToDictionary"/> deliberately still uses the
/// <c>SettingKeyToString()</c> names from the C++ sample, so PlayFab Game Saves remain
/// interchangeable across all three implementations.
/// </para>
/// </remarks>
public sealed class PlayerProfile
{
    private readonly string _root;
    private readonly bool _pathIsPinned;

    /// <summary>
    /// The settings file currently in use, or empty while no user is established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Empty until sign-in, deliberately (XR-052).</b> This used to be seeded at boot
    /// from a <c>last-profile.txt</c> pointer naming whichever account last signed in on
    /// the device, so that the player's own volume levels were applied before the first
    /// note of music played. The pre-certification audit found the cost of that: the next
    /// person to use a shared console, if they never signed in and played through
    /// Continue Offline, had their <see cref="Save"/> write straight back into
    /// <c>settings_&lt;previous xuid&gt;.cfg</c> and silently overwrite the first
    /// player's settings under the first player's account.
    /// </para>
    /// <para>
    /// So boot now starts from the built-in defaults and an unsigned session is held
    /// entirely in memory: <see cref="Load"/> and <see cref="Save"/> both no-op while
    /// this is empty. <see cref="Rehome"/> supplies a path the moment an identity
    /// exists, and loads that account's real settings then. The cost is that an offline
    /// session's setting changes do not survive a restart, which is the correct trade:
    /// they belong to nobody, and there is no file they can be written to that is not
    /// somebody else's.
    /// </para>
    /// </remarks>
    private string _path;
    private AudioManager? _audio;
    private CloudSettingsSync? _cloud;

    public PlayerProfile(string? userToken = null, string? root = null)
    {
        root ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetRumble");

        Directory.CreateDirectory(root);
        _root = root;

        // Instances launched with --pf-user=<name> (the local multi-instance Party test
        // path, debug builds only) get their own settings file; otherwise every client on
        // one PC would share and overwrite the same settings and appearance. That token
        // is an established identity as far as this class is concerned, so it is the one
        // case where a path exists before Rehome.
        _pathIsPinned = !string.IsNullOrWhiteSpace(userToken);

        _path = _pathIsPinned
            ? Path.Combine(root, $"settings_{Sanitise(userToken!)}.cfg")
            : string.Empty;
    }

    public event Action? SettingsChanged;

    public event Action? IdentityChanged;

    public string DisplayName { get; private set; } = "Player";

    public string EntityId { get; private set; } = string.Empty;

    public string XboxUserId { get; private set; } = string.Empty;

    public bool IsSignedIn { get; private set; }

    public float MasterVolume { get; set; } = 1.0f;

    public float MusicVolume { get; set; } = 0.7f;

    public float SfxVolume { get; set; } = 1.0f;

    public float VoiceChatVolume { get; set; } = 1.0f;

    public TranscriptionMode VoiceChatTranscriptionMode { get; set; } = TranscriptionMode.UsePlatformSetting;

    public int ShipStyleId { get; set; }

    public int ShipColorId { get; set; }

    public bool Fullscreen { get; set; }

    public bool ShowRosterOverlay { get; set; } = true;

    /// <summary>
    /// AI opponents to put in a practice match, 0 to
    /// <see cref="NRConst.MaxPracticeBots"/>.
    /// </summary>
    public int PracticeOpponents { get; set; } = 2;

    /// <summary>True when settings have changed since the last save.</summary>
    public bool HasPendingChanges { get; private set; }

    /// <summary>Connects the audio mixer so volume changes are audible while adjusting.</summary>
    public void AttachAudio(AudioManager audio)
    {
        _audio = audio;
        ApplyToAudio();
    }

    /// <summary>
    /// Connects the cloud tier, so every existing call to <see cref="Save"/> commits to
    /// both tiers.
    /// </summary>
    /// <remarks>
    /// Deliberately shaped like <see cref="AttachAudio"/>, and for the same reason: the
    /// screens that change settings should not each have to remember a second call.
    /// Options and the in-game menu already call <see cref="Save"/> when they close, and
    /// so does shutdown - hooking the sink here is what makes those three the cloud
    /// write points too, rather than three more places to keep in step.
    /// </remarks>
    public void AttachCloudSave(CloudSettingsSync cloud) => _cloud = cloud;

    /// <summary>
    /// Set while <see cref="CloudSettingsSync.PullAsync"/> is applying a downloaded
    /// record, so writing it to disk does not immediately push it straight back up.
    /// </summary>
    internal bool SuppressCloudPush { get; set; }

    public void SetIdentity(string name, string entityId, string xuid = "")
    {
        DisplayName = string.IsNullOrEmpty(name) ? "Player" : name;
        EntityId = entityId;
        XboxUserId = xuid;
        IsSignedIn = !string.IsNullOrEmpty(entityId) || !string.IsNullOrEmpty(xuid);
        IdentityChanged?.Invoke();
    }

    /// <summary>
    /// Moves the settings file onto a path scoped to the given stable user id, so a
    /// second Xbox account signing in on the same console or PC gets its own settings
    /// instead of reading and overwriting whatever the previous account left (XR-052).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once real sign-in completes, after <see cref="SetIdentity"/> and before
    /// <see cref="CloudSettingsSync.PullAsync"/>. Until it runs there is no settings file
    /// at all - see <see cref="_path"/> for why boot no longer adopts one.
    /// </para>
    /// <para>
    /// A null or empty id means the identity went away (a sign-out), and puts the profile
    /// back into the in-memory state it booted in. The in-memory values are reset to
    /// defaults at the same time, so the departing player's appearance and levels do not
    /// linger for whoever signs in next.
    /// </para>
    /// <para>
    /// Distinct from the <c>--pf-user</c> constructor override: that scopes an entire
    /// debug-only process to one file for the life of the run so several local
    /// instances used for Party testing do not collide, and never changes afterwards.
    /// This method re-homes the live profile mid-session, for a single instance whose
    /// signed-in user only becomes known after boot.
    /// </para>
    /// </remarks>
    public void Rehome(string? userId)
    {
        if (_pathIsPinned)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(userId)
            ? string.Empty
            : Path.Combine(_root, $"settings_{Sanitise(userId)}.cfg");

        if (path == _path)
        {
            return;
        }

        _path = path;

        if (_path.Length == 0)
        {
            // Back to an unowned session. Whatever is in memory belonged to the user who
            // just left, so it goes with them.
            ResetToDefaults();
            return;
        }

        if (File.Exists(_path))
        {
            // This account has signed in on this device before - use its own file rather
            // than whatever happens to be in memory.
            Load();
        }
        else
        {
            // First time this account has signed in here. Seed its file from whatever is
            // in memory now - the built-in defaults, or settings the player changed on the
            // way in - rather than leaving it absent until they next change something.
            Save();
        }
    }

    /// <summary>
    /// Returns every setting to its built-in default, without touching disk. Used when an
    /// identity goes away, so nothing of the departing player's survives in memory to be
    /// shown to, or saved under, the next one (XR-052).
    /// </summary>
    private void ResetToDefaults()
    {
        MasterVolume = 1.0f;
        MusicVolume = 0.7f;
        SfxVolume = 1.0f;
        VoiceChatVolume = 1.0f;
        VoiceChatTranscriptionMode = TranscriptionMode.UsePlatformSetting;
        ShipStyleId = 0;
        ShipColorId = 0;
        Fullscreen = false;
        ShowRosterOverlay = true;
        PracticeOpponents = NRConst.MaxPracticeBots;

        ApplyToAudio();
        HasPendingChanges = false;
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// Resolves the stored mode into the on/off answer Party actually needs, mirroring
    /// <c>PlayFabManager::IsVoiceChatTranscriptionEnabled</c>. There is no platform
    /// speech-to-text accessibility setting to consult on this SDK, so
    /// <see cref="TranscriptionMode.UsePlatformSetting"/> resolves to enabled.
    /// </summary>
    public bool IsVoiceChatTranscriptionEnabled
        => VoiceChatTranscriptionMode != TranscriptionMode.Disabled;

    /// <summary>
    /// Marks the profile dirty and pushes audio values through immediately, so option
    /// sliders are audible while being adjusted.
    /// </summary>
    public void MarkDirty()
    {
        HasPendingChanges = true;
        ApplyToAudio();
        SettingsChanged?.Invoke();
    }

    public void Load()
    {
        if (_path.Length == 0 || !File.Exists(_path))
        {
            return;
        }

        var values = ReadIni();

        MasterVolume = Clamp01(GetFloat(values, "audio", "master_volume", MasterVolume));
        MusicVolume = Clamp01(GetFloat(values, "audio", "music_volume", MusicVolume));
        SfxVolume = Clamp01(GetFloat(values, "audio", "sfx_volume", SfxVolume));
        VoiceChatVolume = Clamp01(GetFloat(values, "audio", "voice_chat_volume", VoiceChatVolume));

        VoiceChatTranscriptionMode = (TranscriptionMode)Math.Clamp(
            GetInt(values, "accessibility", "voice_chat_transcription_mode", (int)VoiceChatTranscriptionMode), 0, 2);

        ShipStyleId = Math.Clamp(GetInt(values, "appearance", "ship_style_id", ShipStyleId), 0, 3);
        ShipColorId = Math.Clamp(
            GetInt(values, "appearance", "ship_color_id", ShipColorId),
            0,
            Core.Tuning.TuningLibrary.Palette.Size - 1);

        Fullscreen = GetBool(values, "video", "fullscreen", Fullscreen);
        ShowRosterOverlay = GetBool(values, "hud", "show_roster_overlay", ShowRosterOverlay);

        PracticeOpponents = Math.Clamp(
            GetInt(values, "gameplay", "practice_opponents", PracticeOpponents),
            0,
            NRConst.MaxPracticeBots);

        ApplyToAudio();
        HasPendingChanges = false;
        SettingsChanged?.Invoke();
    }

    public void Save()
    {
        if (_path.Length == 0)
        {
            // No user is established, so there is no file these values may be written to
            // (XR-052). The session keeps them in memory and they go no further. See
            // _path.
            HasPendingChanges = false;
            return;
        }

        var text = new StringBuilder()
            .AppendLine("[audio]")
            .AppendLine(Line("master_volume", MasterVolume))
            .AppendLine(Line("music_volume", MusicVolume))
            .AppendLine(Line("sfx_volume", SfxVolume))
            .AppendLine(Line("voice_chat_volume", VoiceChatVolume))
            .AppendLine()
            .AppendLine("[accessibility]")
            .AppendLine(Line("voice_chat_transcription_mode", (int)VoiceChatTranscriptionMode))
            .AppendLine()
            .AppendLine("[appearance]")
            .AppendLine(Line("ship_style_id", ShipStyleId))
            .AppendLine(Line("ship_color_id", ShipColorId))
            .AppendLine()
            .AppendLine("[video]")
            .AppendLine(Line("fullscreen", Fullscreen))
            .AppendLine()
            .AppendLine("[hud]")
            .AppendLine(Line("show_roster_overlay", ShowRosterOverlay))
            .AppendLine()
            .AppendLine("[gameplay]")
            .AppendLine(Line("practice_opponents", PracticeOpponents))
            .ToString();

        try
        {
            File.WriteAllText(_path, text);
            HasPendingChanges = false;
        }
        catch (IOException)
        {
            // Losing settings is not worth taking the game down for; the GDScript
            // push_warning'd here for the same reason.
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!SuppressCloudPush)
        {
            // Fire-and-forget, and after the local write: the disk copy is the one that
            // must not be lost, and a player who is not signed in simply has no cloud
            // tier to write to.
            _cloud?.Push();
        }
    }

    /// <summary>Cloud-save payload, keyed to match the C++ sample's setting names.</summary>
    public Dictionary<string, object> ToDictionary() => new()
    {
        ["masterVolume"] = MasterVolume,
        ["musicVolume"] = MusicVolume,
        ["sfxVolume"] = SfxVolume,
        ["voiceChatVolume"] = VoiceChatVolume,
        ["voiceChatTranscriptionMode"] = (int)VoiceChatTranscriptionMode,
        ["selectedShip"] = ShipStyleId,
        ["selectedColor"] = ShipColorId,
        ["fullscreen"] = Fullscreen,
        ["showRosterOverlay"] = ShowRosterOverlay,
        ["practiceOpponents"] = PracticeOpponents,
    };

    public void ApplyDictionary(IReadOnlyDictionary<string, object> data)
    {
        MasterVolume = Clamp01(Read(data, "masterVolume", MasterVolume));
        MusicVolume = Clamp01(Read(data, "musicVolume", MusicVolume));
        SfxVolume = Clamp01(Read(data, "sfxVolume", SfxVolume));
        VoiceChatVolume = Clamp01(Read(data, "voiceChatVolume", VoiceChatVolume));

        VoiceChatTranscriptionMode = (TranscriptionMode)Math.Clamp(
            (int)Read(data, "voiceChatTranscriptionMode", (int)VoiceChatTranscriptionMode), 0, 2);

        ShipStyleId = Math.Clamp((int)Read(data, "selectedShip", ShipStyleId), 0, 3);
        ShipColorId = Math.Clamp(
            (int)Read(data, "selectedColor", ShipColorId), 0, Core.Tuning.TuningLibrary.Palette.Size - 1);

        Fullscreen = Read(data, "fullscreen", Fullscreen ? 1f : 0f) >= 0.5f;
        ShowRosterOverlay = Read(data, "showRosterOverlay", ShowRosterOverlay ? 1f : 0f) >= 0.5f;

        PracticeOpponents = Math.Clamp(
            (int)Read(data, "practiceOpponents", PracticeOpponents), 0, NRConst.MaxPracticeBots);

        ApplyToAudio();
        SettingsChanged?.Invoke();
    }

    private void ApplyToAudio()
    {
        if (_audio is null)
        {
            return;
        }

        _audio.MasterVolume = MasterVolume;
        _audio.MusicVolume = MusicVolume;
        _audio.SfxVolume = SfxVolume;
    }

    private static float Read(IReadOnlyDictionary<string, object> data, string key, float fallback)
        => data.TryGetValue(key, out var value) && value is IConvertible convertible
            ? convertible.ToSingle(CultureInfo.InvariantCulture)
            : fallback;

    private Dictionary<string, string> ReadIni()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var raw in File.ReadAllLines(_path))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            var split = line.IndexOf('=');

            if (split <= 0)
            {
                continue;
            }

            values[$"{section}/{line[..split].Trim()}"] = line[(split + 1)..].Trim();
        }

        return values;
    }

    private static string Line(string key, float value)
        => $"{key}={value.ToString("R", CultureInfo.InvariantCulture)}";

    private static string Line(string key, int value)
        => $"{key}={value.ToString(CultureInfo.InvariantCulture)}";

    private static string Line(string key, bool value) => $"{key}={(value ? "true" : "false")}";

    private static float GetFloat(Dictionary<string, string> values, string section, string key, float fallback)
        => values.TryGetValue($"{section}/{key}", out var text)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

    private static int GetInt(Dictionary<string, string> values, string section, string key, int fallback)
        => values.TryGetValue($"{section}/{key}", out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

    private static bool GetBool(Dictionary<string, string> values, string section, string key, bool fallback)
        => values.TryGetValue($"{section}/{key}", out var text)
            ? text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"
            : fallback;

    private static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

    private static string Sanitise(string token)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(token.Length);

        foreach (var c in token)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.ToString();
    }
}
