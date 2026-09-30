using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Media;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game.Content;

/// <summary>
/// Central asset registry, ported from <c>scripts/autoload/assets.gd</c> - itself the
/// Godot equivalent of <c>Game/Assets/Textures.cpp</c> + <c>Audio.cpp</c> combined with
/// the C++ ContentManager's caching role.
/// </summary>
/// <remarks>
/// <para>
/// Keys mirror the C++ <c>TextureType</c> / <c>AudioType</c> enumerators so all three
/// code bases stay greppable against each other.
/// </para>
/// <para>
/// MonoGame's <see cref="ContentManager"/> already caches by asset name, so this type
/// is a name registry and a typed front door rather than a second cache. Its real job
/// is keeping content paths out of gameplay code, exactly as the Godot original did.
/// </para>
/// </remarks>
public sealed class AssetRegistry
{
    private readonly ContentManager _content;
    private readonly Dictionary<string, Texture2D> _textures = [];
    private readonly Dictionary<string, SoundEffect> _sounds = [];
    private readonly Dictionary<string, Effect> _effects = [];
    private Song? _music;

    public AssetRegistry(ContentManager content) => _content = content;

    /// <summary>
    /// Texture key to content path. Ported verbatim from
    /// <c>Assets.TEXTURE_PATHS</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TexturePaths =
        new Dictionary<string, string>
        {
            ["Asteroid0"] = "Textures/Gameplay/Asteroids/Asteroid0",
            ["Asteroid1"] = "Textures/Gameplay/Asteroids/Asteroid1",
            ["Asteroid2"] = "Textures/Gameplay/Asteroids/Asteroid2",
            ["Barrier_End"] = "Textures/Gameplay/Barriers/Barrier_End",
            ["Barrier_Horizontal"] = "Textures/Gameplay/Barriers/Barrier_Horizontal",
            ["Barrier_Vertical"] = "Textures/Gameplay/Barriers/Barrier_Vertical",
            ["Particle_Default"] = "Textures/Gameplay/Particles/Particle_Default",
            ["Particle_Spark"] = "Textures/Gameplay/Particles/Particle_Spark",
            ["Particle_Smoke"] = "Textures/Gameplay/Particles/Particle_Smoke",
            ["Projectile_Laser"] = "Textures/Gameplay/Projectiles/Projectile_Laser",
            ["Projectile_Mine"] = "Textures/Gameplay/Projectiles/Projectile_Mine",
            ["Projectile_Rocket"] = "Textures/Gameplay/Projectiles/Projectile_Rocket",
            ["PowerUp_DoubleLaser"] = "Textures/Gameplay/PowerUps/PowerUp_DoubleLaser",
            ["PowerUp_Rocket"] = "Textures/Gameplay/PowerUps/PowerUp_Rocket",
            ["PowerUp_TripleLaser"] = "Textures/Gameplay/PowerUps/PowerUp_TripleLaser",
            ["ShipShield_Base"] = "Textures/Gameplay/Ships/ShipShield/ShipShield_Base",
            ["ShipShield_Move"] = "Textures/Gameplay/Ships/ShipShield/ShipShield_Move",
            ["Ship0_Base"] = "Textures/Gameplay/Ships/Ship0/Ship0_Base",
            ["Ship0_Overlay"] = "Textures/Gameplay/Ships/Ship0/Ship0_Overlay",
            ["Ship0_Silhouette"] = "Textures/Gameplay/Ships/Ship0/Ship0_Silhouette",
            ["Ship1_Base"] = "Textures/Gameplay/Ships/Ship1/Ship1_Base",
            ["Ship1_Overlay"] = "Textures/Gameplay/Ships/Ship1/Ship1_Overlay",
            ["Ship1_Silhouette"] = "Textures/Gameplay/Ships/Ship1/Ship1_Silhouette",
            ["Ship2_Base"] = "Textures/Gameplay/Ships/Ship2/Ship2_Base",
            ["Ship2_Overlay"] = "Textures/Gameplay/Ships/Ship2/Ship2_Overlay",
            ["Ship2_Silhouette"] = "Textures/Gameplay/Ships/Ship2/Ship2_Silhouette",
            ["Ship3_Base"] = "Textures/Gameplay/Ships/Ship3/Ship3_Base",
            ["Ship3_Overlay"] = "Textures/Gameplay/Ships/Ship3/Ship3_Overlay",
            ["Ship3_Silhouette"] = "Textures/Gameplay/Ships/Ship3/Ship3_Silhouette",
            ["Controller_A"] = "Textures/UI/Controller/Controller_A",
            ["Controller_B"] = "Textures/UI/Controller/Controller_B",
            ["Controller_X"] = "Textures/UI/Controller/Controller_X",
            ["Controller_Y"] = "Textures/UI/Controller/Controller_Y",
            ["Controller_Menu"] = "Textures/UI/Controller/Controller_Menu",
            ["Controller_View"] = "Textures/UI/Controller/Controller_View",
            ["Controller_BumperLeft"] = "Textures/UI/Controller/Controller_BumperLeft",
            ["Controller_BumperRight"] = "Textures/UI/Controller/Controller_BumperRight",
            ["Controller_Xbox"] = "Textures/UI/Controller/Controller_Xbox",
            ["Loading_Ring"] = "Textures/UI/Loading/Loading_Ring",
            ["Lobby_BackgroundColor"] = "Textures/UI/LobbyBackground/LobbyBackground_Color",
            ["Lobby_BackgroundColorSlot"] = "Textures/UI/LobbyBackground/LobbyBackground_ColorSlot",
            ["Lobby_BackgroundGameType"] = "Textures/UI/LobbyBackground/LobbyBackground_GameType",
            ["Lobby_BackgroundRoster"] = "Textures/UI/LobbyBackground/LobbyBackground_Roster",
            ["Lobby_BackgroundRules"] = "Textures/UI/LobbyBackground/LobbyBackground_Rules",
            ["Lobby_BackgroundSpaceship"] = "Textures/UI/LobbyBackground/LobbyBackground_Spaceship",
            ["Logo_NetRumble"] = "Textures/UI/Logo/Logo_NetRumble",
            ["Logo_Xbox"] = "Textures/UI/Logo/Logo_Xbox",
            ["Microphone_Available"] = "Textures/UI/Microphone/Microphone_Available",
            ["Microphone_Muted"] = "Textures/UI/Microphone/Microphone_Muted",
            ["Microphone_Talking"] = "Textures/UI/Microphone/Microphone_Talking",
            ["ReadyUp_Checkmark"] = "Textures/UI/ReadyUp/ReadyUp_Checkmark",
            ["ReadyUp_RingBackground"] = "Textures/UI/ReadyUp/ReadyUp_RingBackground",
            ["ReadyUp_RingOutline"] = "Textures/UI/ReadyUp/ReadyUp_RingOutline",
            ["Shape_Square"] = "Textures/UI/Shape/Shape_Square",
            ["Shape_LeftArrow"] = "Textures/UI/Shape/Shape_LeftArrow",
            ["Shape_RightArrow"] = "Textures/UI/Shape/Shape_RightArrow",
        };

    /// <summary>
    /// Audio key to content path, mirroring the C++ <c>AudioType</c> enum.
    /// </summary>
    /// <remarks>
    /// Two Godot keys pointed at <c>.tres</c> random-stream resources rather than a
    /// single file: <c>LaserFire</c> (<c>laser_fire.tres</c>) and <c>RocketFire</c>
    /// (<c>rocket_fire.tres</c>). Those are randomised pools, so they are represented
    /// here as variant lists in <see cref="SoundVariants"/> and picked from at play
    /// time - see <c>AudioManager</c>.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> AudioPaths =
        new Dictionary<string, string>
        {
            ["AsteroidTouch"] = "Audio/Asteroid/Asteroid_Touch",
            ["ExplosionLarge"] = "Audio/Explosion/Explosion_Large",
            ["ExplosionMedium"] = "Audio/Explosion/Explosion_Medium",
            ["ExplosionShockwave"] = "Audio/Explosion/Explosion_Shockwave",
            ["MenuScroll"] = "Audio/Menu/Menu_Scroll",
            ["MenuSelect"] = "Audio/Menu/Menu_Select",
            ["PlayerSpawn"] = "Audio/Player/Player_Spawn",
            ["PowerUpSpawn"] = "Audio/PowerUp/PowerUp_Spawn",
            ["PowerUpTouch"] = "Audio/PowerUp/PowerUp_Touch",
            ["Rocket"] = "Audio/Rocket/Rocket",
        };

    /// <summary>
    /// A randomised sound pool, replacing a Godot <c>AudioStreamRandomizer</c> resource.
    /// </summary>
    /// <param name="Paths">
    /// The candidate clips. All source resources used a uniform weight of 1.0, so a
    /// plain uniform pick reproduces them.
    /// </param>
    /// <param name="RandomPitch">
    /// Godot's <c>random_pitch</c>: the pitch <em>scale</em> is drawn from
    /// <c>[1 / RandomPitch, RandomPitch]</c>. A value of 1.0 means no variation.
    /// </param>
    public readonly record struct SoundVariantSet(string[] Paths, float RandomPitch);

    /// <summary>
    /// Randomised sound pools, replacing the Godot
    /// <c>AudioStreamRandomizer</c> <c>.tres</c> resources.
    /// </summary>
    /// <remarks>
    /// Both source resources set <c>playback_mode = 1</c>, which is Godot's
    /// <c>PLAYBACK_RANDOM</c> - a pure uniform pick where consecutive repeats are
    /// allowed. That is deliberately <em>not</em> the Godot default
    /// (<c>PLAYBACK_RANDOM_NO_REPEATS</c>), so do not "improve" this into a
    /// no-repeat shuffle.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, SoundVariantSet> SoundVariants =
        new Dictionary<string, SoundVariantSet>
        {
            ["LaserFire"] = new(
                [
                    "Audio/Laser/Laser_Fire0",
                    "Audio/Laser/Laser_Fire1",
                    "Audio/Laser/Laser_Fire2",
                ],
                1.05f),
            ["RocketFire"] = new(
                [
                    "Audio/Rocket/Rocket_Fire0",
                    "Audio/Rocket/Rocket_Fire1",
                ],
                1.05f),
        };

    /// <summary>
    /// Converts a Godot pitch <em>scale</em> factor into the
    /// <see cref="SoundEffectInstance.Pitch"/> value that produces the same playback rate.
    /// </summary>
    /// <remarks>
    /// The two APIs are not the same units. Godot multiplies the playback rate directly,
    /// whereas MonoGame's <c>Pitch</c> is measured in octaves over <c>[-1, 1]</c>. The
    /// conversion is therefore logarithmic, not linear: a scale of 1.05 is
    /// <c>log2(1.05) = 0.0704</c> octaves, where a naive linear reading would have given
    /// 1.05 and been clamped to a full octave.
    /// </remarks>
    public static float PitchScaleToMonoGamePitch(float scale)
        => Math.Clamp(MathF.Log2(scale), -1f, 1f);

    /// <summary>
    /// The music track, relative to the content root and <b>including</b> its extension:
    /// this one asset is copied rather than built, so it is a real file on disk and not
    /// an .xnb.
    /// </summary>
    public const string MusicPath = "Audio/Music/OneStepBeyond.mp3";

    /// <summary>Loads and caches a texture by registry key.</summary>
    public Texture2D Texture(string key)
    {
        if (_textures.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!TexturePaths.TryGetValue(key, out var path))
        {
            throw new KeyNotFoundException($"Unknown texture key '{key}'.");
        }

        var texture = _content.Load<Texture2D>(path);
        _textures[key] = texture;
        return texture;
    }

    /// <summary>
    /// Ship art for a style id, clamped to the four available ships as
    /// <c>Assets.ship_texture</c> did.
    /// </summary>
    public Texture2D ShipTexture(int styleId, string suffix = "Base")
        => Texture($"Ship{Math.Clamp(styleId, 0, 3)}_{suffix}");

    /// <summary>Loads and caches a compiled effect by content path.</summary>
    public Effect Effect(string path)
    {
        if (_effects.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var effect = _content.Load<Effect>(path);
        _effects[path] = effect;
        return effect;
    }

    /// <summary>
    /// Loads a compiled effect, or returns <see langword="null"/> when it cannot be used
    /// on this platform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compiled shaders are the one kind of content that is not portable between the
    /// platforms this game ships on. Everything else in <c>Content</c> - textures, fonts,
    /// PCM and MP3 - is built once by the desktop project and loaded unchanged on Xbox,
    /// but an <c>.mgfxo</c> carries a profile byte that the runtime checks against its own
    /// backend, and DirectX 11 bytecode is meaningless to the console's D3D12.X driver.
    /// Loading one there throws "This MGFX effect was built for a different platform!".
    /// </para>
    /// <para>
    /// That is a content-build problem and is fixed in the content build - see
    /// <c>docs/xbox-console-build.md</c> - but it must not be a crash. A decorative lighting pass is not
    /// worth a title exit, and "the console has to survive missing or unusable content" is
    /// a certification position as much as an engineering one. Callers take the null and
    /// draw the unlit pass instead.
    /// </para>
    /// </remarks>
    public Effect? TryEffect(string path)
    {
        if (_effects.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var effect = _content.Load<Effect>(path);
            _effects[path] = effect;
            return effect;
        }
        catch (Exception ex)
        {
            CrashLog.Mark($"content: effect '{path}' unusable ({ex.GetType().Name}: {ex.Message})");

            return null;
        }
    }

    /// <summary>Loads and caches a sound effect by registry key or by content path.</summary>
    public SoundEffect Sound(string keyOrPath)
    {
        if (_sounds.TryGetValue(keyOrPath, out var cached))
        {
            return cached;
        }

        var path = AudioPaths.TryGetValue(keyOrPath, out var mapped) ? mapped : keyOrPath;
        var sound = _content.Load<SoundEffect>(path);
        _sounds[keyOrPath] = sound;
        return sound;
    }

    /// <summary>The looping menu/match music.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>not</b> <c>ContentManager.Load&lt;Song&gt;</c>. The content pipeline's
    /// <c>SongProcessor</c> transcodes to a container chosen for the target platform - .m4a
    /// (AAC) for Windows - and emits a 131-byte .xnb that holds nothing but the name of
    /// that file, which <c>SongReader</c> then opens. This game runs on
    /// <c>MonoGame.Framework.Native</c> on both desktop and Xbox, and its decoder
    /// (<c>MGM_AudioDecoder_Create</c>) sniffs the file signature and accepts only Ogg and
    /// MP3. An .m4a fails that check, <c>Song.PlatformInitialize</c> stores a null decoder
    /// and returns, and every later <c>MediaPlayer.Play</c> is a silent no-op - no
    /// exception, no log, just no music.
    /// </para>
    /// <para>
    /// So the .mp3 is copied verbatim by the pipeline (<c>/copy:</c> in Content.mgcb) and
    /// opened here by path. MPEG frame sync is a signature the native decoder does accept,
    /// so this is the one route that actually produces sound.
    /// </para>
    /// </remarks>
    public Song Music()
        => _music ??= Song.FromUri(
            "OneStepBeyond",
            new Uri(
                Path.Combine(AppContext.BaseDirectory, _content.RootDirectory, MusicPath),
                UriKind.Absolute));

    /// <summary>
    /// Forces every registered texture and sound to load.
    /// </summary>
    /// <remarks>
    /// Called by the loading screen. Gameplay asks for assets by key every frame, and a
    /// first-use disk hit during a match would show up as a stutter.
    /// </remarks>
    public void PreloadAll()
    {
        foreach (var key in TexturePaths.Keys)
        {
            _ = Texture(key);
        }

        foreach (var key in AudioPaths.Keys)
        {
            _ = Sound(key);
        }

        foreach (var set in SoundVariants.Values)
        {
            foreach (var path in set.Paths)
            {
                _ = Sound(path);
            }
        }
    }
}
