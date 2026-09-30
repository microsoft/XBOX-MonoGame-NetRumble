using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Media;
using NetRumble.Core;
using NetRumble.Game.Content;
using NetRumble.Game.Fx;

namespace NetRumble.Game.Audio;

/// <summary>
/// Looping music plus a fixed pool of one-shot voices, ported from
/// <c>scripts/autoload/audio_manager.gd</c>. Also absorbs
/// <c>ParticleManager._play_positional_sound</c>, which is the only caller that needed
/// panning and distance falloff.
/// </summary>
/// <remarks>
/// <para>
/// <b>Buses become scalars, but keep bus semantics.</b> Godot routed everything through
/// <c>Master -&gt; Music</c> and <c>Master -&gt; SFX</c> so the options sliders could drive
/// <c>AudioServer</c> and affect sounds that were already playing. MonoGame has no mixer, so
/// the levels are plain multipliers here - but a volume change is pushed into every live
/// voice rather than only applying to the next one, which is what preserves the behaviour
/// the sliders were written against.
/// </para>
/// <para>
/// <b>Voice slots, not reusable players.</b> Godot's pool was 24 <c>AudioStreamPlayer</c>
/// nodes whose <c>stream</c> was reassigned on each play. A
/// <see cref="SoundEffectInstance"/> is permanently bound to one <see cref="SoundEffect"/>,
/// so a slot here owns whatever instance is currently in it and replaces it when reused. The
/// count and the round-robin stealing are unchanged, and stealing still exists for the same
/// reason: a burst of explosions must never silently swallow the newest sound.
/// </para>
/// </remarks>
public sealed class AudioManager : IDisposable
{
    /// <summary>Godot <c>VOICE_COUNT</c>.</summary>
    private const int VoiceCount = 24;

    /// <summary>
    /// <c>AudioStreamPlayer2D.max_distance</c>, at its Godot default. Beyond this a sound is
    /// silent.
    /// </summary>
    private const float MaxDistance = 2000f;

    /// <summary>
    /// <c>AudioStreamPlayer2D.attenuation</c>, at its Godot default of 1.0 - which makes the
    /// falloff linear in distance.
    /// </summary>
    private const float Attenuation = 1.0f;

    /// <summary>
    /// Half the design viewport width, used to normalise pan. A sound at the edge of the
    /// screen is hard left or right.
    /// </summary>
    private const float PanHalfWidth = NetRumbleGame.DesignWidth / 2f;

    private readonly AssetRegistry _assets;
    private readonly Random _random;
    private readonly Voice[] _voices = new Voice[VoiceCount];

    /// <summary>
    /// Guards the voice table (XR-001).
    /// </summary>
    /// <remarks>
    /// Almost every caller here is the game thread, and on its own that needs no lock. The
    /// exception is the one that matters: <see cref="StopAll"/> is called from the PLM
    /// suspend handler, which runs on an OS thread while the game loop is still advancing.
    /// Without this gate that handler can read a <see cref="Voice.Instance"/> that
    /// <see cref="Play"/> is in the middle of disposing and replacing, and calling
    /// <c>Stop()</c> on it throws <see cref="ObjectDisposedException"/> inside a native
    /// callback. The critical sections are a handful of field writes over 24 entries, so
    /// the cost is not measurable against a frame.
    /// </remarks>
    private readonly object _voiceGate = new();

    private int _nextVoice;
    private float _masterVolume = 1.0f;
    private float _musicVolume = 1.0f;
    private float _sfxVolume = 1.0f;
    private bool _muted;
    private bool _systemMuted;
    private bool _muteBeforeSystem;
    private Vector2 _listener;

    public AudioManager(AssetRegistry assets, Random random)
    {
        _assets = assets;
        _random = random;

        for (var i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new Voice();
        }
    }

    /// <summary>Master bus level, in <c>[0, 1]</c>.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolumes();
        }
    }

    /// <summary>Music bus level, in <c>[0, 1]</c>.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolumes();
        }
    }

    /// <summary>SFX bus level, in <c>[0, 1]</c>.</summary>
    public float SfxVolume
    {
        get => _sfxVolume;
        set
        {
            _sfxVolume = Math.Clamp(value, 0f, 1f);
            ApplyVolumes();
        }
    }

    /// <summary>Master mute, matching <c>AudioServer.set_bus_mute</c> on the master bus.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolumes();
        }
    }

    /// <summary>
    /// Mutes for a platform constrain (XR-001), remembering the player's own mute setting
    /// so it survives the round trip.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Muted"/> on purpose. Opening the Guide is not a settings
    /// change, so the platform mute must not be written to <c>PlayerProfile</c> and must
    /// not clobber a player who had already muted the game themselves - they stay muted
    /// when the Guide closes.
    /// </remarks>
    public void SetSystemMuted(bool value)
    {
        if (value == _systemMuted)
        {
            return;
        }

        if (value)
        {
            _muteBeforeSystem = Muted;
            _systemMuted = true;
            Muted = true;
        }
        else
        {
            _systemMuted = false;
            Muted = _muteBeforeSystem;
        }
    }

    /// <summary>
    /// Where positional sounds are heard from. The camera centre, set once a frame.
    /// </summary>
    /// <remarks>
    /// Godot derived this from the current <c>Camera2D</c> automatically; nothing in the port
    /// ever moved the listener independently of the view.
    /// </remarks>
    public void SetListener(Vector2 position) => _listener = position;

    /// <summary>
    /// Plays the sound(s) an event maps to, at the event's world position.
    /// </summary>
    /// <remarks>
    /// Sounds and particles are looked up from separate tables because the two do not line
    /// up - see <see cref="EffectLibrary"/>.
    /// </remarks>
    public void PlayEvent(GameplayEventType eventType, Vector2 position)
    {
        if (!EffectLibrary.Sounds.TryGetValue(eventType, out var keys))
        {
            return;
        }

        foreach (var key in keys)
        {
            PlayAt(key, position);
        }
    }

    /// <summary>Plays a one-shot with distance attenuation and panning.</summary>
    public void PlayAt(string key, Vector2 position)
    {
        var distance = Vector2.Distance(position, _listener);
        var falloff = MathF.Pow(Math.Clamp(1f - (distance / MaxDistance), 0f, 1f), Attenuation);

        if (falloff <= 0f)
        {
            return;
        }

        var pan = Math.Clamp((position.X - _listener.X) / PanHalfWidth, -1f, 1f);
        Play(key, falloff, pan);
    }

    /// <summary>
    /// Plays a one-shot by <see cref="AssetRegistry"/> key, resolving randomised pools.
    /// </summary>
    /// <param name="volumeScale">Level relative to the SFX bus.</param>
    /// <param name="pan">-1 hard left to 1 hard right.</param>
    /// <param name="pitchScale">
    /// A Godot-style playback <em>rate</em> multiplier, converted to MonoGame's octave-based
    /// pitch by <see cref="AssetRegistry.PitchScaleToMonoGamePitch"/>.
    /// </param>
    public void Play(string key, float volumeScale = 1.0f, float pan = 0.0f, float pitchScale = 1.0f)
    {
        SoundEffect clip;
        float pitch;

        if (AssetRegistry.SoundVariants.TryGetValue(key, out var set))
        {
            clip = _assets.Sound(set.Paths[_random.Next(set.Paths.Length)]);

            // Godot draws the pitch scale from [1/random_pitch, random_pitch]. Sampling the
            // exponent uniformly rather than the scale keeps the distribution symmetric about
            // unity, which is what a multiplicative range implies.
            var maxOctaves = MathF.Log2(set.RandomPitch);
            pitch = ((float)_random.NextDouble() * 2f - 1f) * maxOctaves;
        }
        else
        {
            clip = _assets.Sound(key);
            pitch = AssetRegistry.PitchScaleToMonoGamePitch(pitchScale);
        }

        // Selection and mutation share one critical section. Monitor is reentrant, so
        // AcquireVoice taking the same gate is harmless, and holding it across both is
        // what keeps StopAll from stopping an instance that is mid-replacement.
        lock (_voiceGate)
        {
            var voice = AcquireVoice();

            voice.Instance?.Dispose();
            voice.Instance = clip.CreateInstance();
            voice.BaseVolume = Math.Max(volumeScale, 0f);
            voice.Instance.Pan = Math.Clamp(pan, -1f, 1f);
            voice.Instance.Pitch = Math.Clamp(pitch, -1f, 1f);
            voice.Instance.Volume = Math.Clamp(voice.BaseVolume * EffectiveSfx, 0f, 1f);
            voice.Instance.Play();
        }
    }

    /// <summary>Starts the looping soundtrack.</summary>
    /// <remarks>
    /// The level is pushed both before and after <see cref="MediaPlayer.Play(Song)"/>.
    /// Setting it first is what stops a single frame of full-volume music escaping on a
    /// backend that starts playback immediately; setting it again afterwards covers a
    /// backend that resets the volume as part of starting a new song. Either way the
    /// first note the player hears already respects their saved level.
    /// </remarks>
    public void PlayMusic(bool loop = true)
    {
        var volume = Math.Clamp(EffectiveMusic, 0f, 1f);
        MediaPlayer.IsRepeating = loop;
        MediaPlayer.Volume = volume;
        MediaPlayer.Play(_assets.Music());
        MediaPlayer.Volume = volume;
    }

    public void StopMusic() => MediaPlayer.Stop();

    /// <summary>Silences music and every voice, matching <c>AudioManager.stop_all</c>.</summary>
    /// <remarks>
    /// Called from the PLM suspend handler on an OS thread as well as from the game
    /// thread, which is why it takes <see cref="_voiceGate"/>.
    /// </remarks>
    public void StopAll()
    {
        StopMusic();

        lock (_voiceGate)
        {
            foreach (var voice in _voices)
            {
                voice.Instance?.Stop();
            }
        }
    }

    public void Dispose()
    {
        lock (_voiceGate)
        {
            foreach (var voice in _voices)
            {
                voice.Instance?.Dispose();
                voice.Instance = null;
            }
        }
    }

    private float EffectiveSfx => _muted ? 0f : _masterVolume * _sfxVolume;

    private float EffectiveMusic => _muted ? 0f : _masterVolume * _musicVolume;

    /// <summary>
    /// Takes the first idle voice, and steals round-robin when all 24 are busy.
    /// </summary>
    private Voice AcquireVoice()
    {
        lock (_voiceGate)
        {
            foreach (var voice in _voices)
            {
                if (voice.Instance is null || voice.Instance.State == SoundState.Stopped)
                {
                    return voice;
                }
            }

            var stolen = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            return stolen;
        }
    }

    private void ApplyVolumes()
    {
        var sfx = EffectiveSfx;

        lock (_voiceGate)
        {
            foreach (var voice in _voices)
            {
                if (voice.Instance is { } instance && instance.State != SoundState.Stopped)
                {
                    instance.Volume = Math.Clamp(voice.BaseVolume * sfx, 0f, 1f);
                }
            }
        }

        MediaPlayer.Volume = Math.Clamp(EffectiveMusic, 0f, 1f);
    }

    private sealed class Voice
    {
        public SoundEffectInstance? Instance;
        public float BaseVolume = 1.0f;
    }
}
