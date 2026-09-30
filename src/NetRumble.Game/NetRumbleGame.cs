using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NetRumble.Core.Tuning;
using NetRumble.Game.Audio;
using NetRumble.Game.Content;
using NetRumble.Game.Fx;
using NetRumble.Game.Profile;
using NetRumble.Game.UI;
using NetRumble.Game.UI.Screens;
using NetRumble.Platform;
using NetRumble.Platform.Composition;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game;

/// <summary>
/// The root game object, replacing <c>scenes/main.tscn</c> + <c>scripts/main.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Responsibilities are deliberately thin, matching the Godot original: own the
/// platform provider, pump it, own the screen stack, and route input. The simulation
/// itself lives in <c>NetRumble.Core</c> and knows nothing about MonoGame; a running
/// match lives in <see cref="Gameplay.MatchSession"/>, owned by the gameplay screen.
/// </para>
/// <para>
/// <b>Pump ordering.</b> <see cref="IPlatformRuntime.Pump"/> runs first in
/// <see cref="Update"/>, before any game logic. Every platform task and event resolves
/// inside that call, so by the time gameplay code runs the platform state for this
/// frame is already settled and no locking is needed anywhere.
/// </para>
/// </remarks>
public sealed class NetRumbleGame : Microsoft.Xna.Framework.Game
{
    /// <summary>
    /// Design resolution, from <c>project.godot</c>
    /// (<c>window/size/viewport_width|height</c>). All UI is authored against this and
    /// scaled to the backbuffer, matching Godot's "canvas_items" stretch mode.
    /// </summary>
    public const int DesignWidth = 1920;

    public const int DesignHeight = 1080;

    private readonly GraphicsDeviceManager _graphics;
    private readonly IPlatformProvider _platform;
    private readonly string[] _commandLineArgs;
    private readonly string? _localDataRoot;

    /// <summary>
    /// Unattended playthrough driver. Null unless <c>--autopilot=</c> was passed; see
    /// <see cref="Autopilot"/> for why it drives the screens rather than the netcode.
    /// </summary>
    /// <remarks>
    /// Debug builds only. Both this and <see cref="_capture"/> are compiled out of a
    /// release build entirely rather than merely left null (XR-003), so the switches that
    /// drive them cannot ship. Every use site is null-conditional already, which is what
    /// makes removing the fields outright possible.
    /// </remarks>
#if DEBUG
    private readonly Autopilot? _autopilot;

    /// <summary>
    /// Saves frames to disk at requested times. Null unless <c>--screenshot-at=</c> was
    /// passed; see <see cref="FrameCapture"/> for why it reads the back buffer.
    /// </summary>
    private readonly FrameCapture? _capture;
#endif
    private readonly Random _random = new();
    private readonly UiInput _uiInput = new();
    private readonly ScreenManager _screens = new();

    private SpriteBatch? _spriteBatch;
    private Texture2D? _pixel;
    private AssetRegistry? _assets;
    private UiTheme? _theme;
    private AudioManager? _audio;
    private PlayerProfile? _profile;
    private AchievementTracker? _achievements;
    private CloudSettingsSync? _cloudSettings;
    private MatchHistoryStore? _matchHistory;
    private UiContext? _context;
    private InviteRouter? _inviteRouter;
    private PresencePublisher? _presence;
    private LifecycleCoordinator? _lifecycle;

    /// <summary>XR-115: whether the player still has a controller, and the prompt if not.</summary>
    private ControllerWatcher? _controllers;
    private readonly ControllerDisconnectOverlay _controllerPrompt = new();

    /// <summary>XR-046: other players' gamerpics, fetched and decoded lazily.</summary>
    private GamerPictureCache? _pictures;

    private PlatformBootState _bootState = PlatformBootState.NotStarted;
    private string _bootDetail = string.Empty;

    /// <summary>Marshals <c>await</c> continuations back onto the game thread.</summary>
    private readonly GameThreadContext _mainThread;

    public NetRumbleGame(PlatformProviderMode mode, string[] args)
    {
        // First, before anything can await: from here on, a continuation in game or UI
        // code resumes on this thread instead of on a thread pool thread. See
        // GameThreadContext for what that was costing.
        _mainThread = GameThreadContext.Install();

        _commandLineArgs = args;

        // XR-003: the three debug switches below are compiled out of release builds, the
        // same way ResolveUserToken compiles out the developer sign-in token. Autopilot's
        // own remarks say "nothing here should ever be reachable in a shipping build" -
        // it force-quits the process when its run ends - and a packaged PC title launched
        // through its execution alias does receive command-line arguments, so leaving the
        // parse in a retail build means shipping that switch to players.
#if DEBUG
        _autopilot = Autopilot.FromCommandLine(args);
        _capture = FrameCapture.FromCommandLine(args);

        // Before anything can read a mode's tuning, and ignored unless asked for.
        TuningLibrary.OverrideMatchTimeLimit(ResolveMatchSeconds(args));
#endif

        _localDataRoot = ResolveLocalDataRoot();

        CrashLog.Mark($"boot: local data root {_localDataRoot ?? "(per-user default)"}");
        _platform = PlatformProviderFactory.Create(mode, _localDataRoot, PlayFabConfiguration.Resolve(args));

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;

        // The simulation is fixed-step; the Godot port ran its physics at 60 Hz and the
        // netcode cadences (30 Hz snapshots, 30 Hz input) are derived from that.
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
    }

    /// <summary>The platform provider, for screens that need it.</summary>
    public IPlatformProvider Platform => _platform;

    /// <summary>The screen stack. The front end lives entirely inside this.</summary>
    public ScreenManager Screens => _screens;

    /// <summary>Local identity plus persisted settings.</summary>
    public PlayerProfile Profile => _profile!;

    /// <summary>Design-to-backbuffer scale for the current frame.</summary>
    public float RenderScale { get; private set; } = 1f;

    /// <summary>Letterbox offset in backbuffer pixels for the current frame.</summary>
    public Vector2 LetterboxOffset { get; private set; }

    /// <summary>How far the platform got during boot, for the acquire-user screen.</summary>
    public string PlatformStatus => _bootState.ToString();

    /// <summary>
    /// The process lifecycle handler, for the one caller that needs to read the constrain
    /// state rather than be told about a change: a <c>MatchDirector</c> built while the
    /// title is already constrained.
    /// </summary>
    internal LifecycleCoordinator? Lifecycle => _lifecycle;

    /// <summary>Detail behind <see cref="PlatformStatus"/>; empty when there is none.</summary>
    public string PlatformStatusDetail => _bootDetail;

    /// <summary>True once the platform has reported a final state, good or bad.</summary>
    public bool PlatformSettled
        => _bootState is PlatformBootState.Ready or PlatformBootState.Degraded or PlatformBootState.Failed;

    /// <summary>True when the platform initialised cleanly.</summary>
    public bool PlatformReady => _bootState == PlatformBootState.Ready;

    /// <summary>Applies the profile's window mode, replacing <c>apply_display_settings</c>.</summary>
    /// <remarks>
    /// Nothing to apply on console: the platform owns the display, the title is always
    /// full screen, and honouring the profile's stored <c>fullscreen</c> flag here would
    /// drive <c>IsFullScreen</c> to false at startup. The Options screen compiles the row
    /// that sets it out for the same reason.
    /// </remarks>
    public void ApplyDisplaySettings()
    {
#if !GDKX
        if (_profile is null || _graphics.IsFullScreen == _profile.Fullscreen)
        {
            return;
        }

        _graphics.IsFullScreen = _profile.Fullscreen;
        _graphics.ApplyChanges();
#endif
    }

    protected override void Initialize()
    {
        Window.Title = "NetRumble";
        Window.AllowUserResizing = true;
        StartPlatform();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        // A 1x1 white texture is the workhorse for the UI layer: every solid rectangle
        // (panels, bars, dividers) is this stretched and tinted, which keeps the whole
        // front end inside a single SpriteBatch without a dedicated shape renderer.
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);

        _assets = new AssetRegistry(Content);
        _assets.PreloadAll();

        _theme = new UiTheme(
            _pixel,
            Content.Load<SpriteFont>("Fonts/UI20"),
            Content.Load<SpriteFont>("Fonts/UI26"),
            Content.Load<SpriteFont>("Fonts/UI29"),
            Content.Load<SpriteFont>("Fonts/UI32"),
            Content.Load<SpriteFont>("Fonts/UI38"),
            Content.Load<SpriteFont>("Fonts/UI51"),
            Content.Load<SpriteFont>("Fonts/UI64"));

        _audio = new AudioManager(_assets, _random);

        var userToken = ResolveUserToken();

        _profile = new PlayerProfile(userToken, _localDataRoot);
        _profile.AttachAudio(_audio);
        _profile.Load();
        ApplyDisplaySettings();

        // The save services hang off the platform's save tier rather than reaching for
        // the file system themselves, so the GDKX build stores under its own LocalData
        // root and a signed-in player gets the cloud tier for free. Local stats are read
        // now, before sign-in, because that tier always works; the cloud merge happens on
        // the acquire-user screen once there is an account to merge with.
        _achievements = new AchievementTracker(_platform, _platform.GameSaves);
        _cloudSettings = new CloudSettingsSync(_platform.GameSaves, _profile);
        _matchHistory = new MatchHistoryStore(_platform.GameSaves);

        _profile.AttachCloudSave(_cloudSettings);
        _ = _achievements.LoadAsync();

        _audio.PlayMusic();

        _context = new UiContext
        {
            Batch = _spriteBatch,
            Theme = _theme,
            Assets = _assets,
            Audio = _audio,
            Screens = _screens,
            Platform = _platform,
            Activity = new ActivityCoordinator(_platform),
            Pictures = _pictures = new GamerPictureCache(GraphicsDevice, _platform),
            Input = _uiInput,
            Profile = _profile,
            Achievements = _achievements,
            CloudSettings = _cloudSettings,
            MatchHistory = _matchHistory,
            Pixel = _pixel,
            Game = this,
            Random = _random,
            DeveloperCustomId = userToken,
        };

        _screens.Context = _context;
        _inviteRouter = new InviteRouter(_context);

        // Constructed before the first push so it observes the whole stack lifetime.
        _presence = new PresencePublisher(_context);
        _lifecycle = new LifecycleCoordinator(_context, _inviteRouter);

        // XR-115. Constructed here for the same reason as the two above - it has to see
        // the whole session - and started immediately so that whatever the player has
        // right now is the silent baseline. A keyboard-only desktop machine therefore
        // never raises the prompt: it has no pad to lose.
        _controllers = new ControllerWatcher(_platform.Identity);
        _controllers.ControllerLost += () =>
        {
            _controllerPrompt.Show();
            _lifecycle.SetControllerMissing(true);
        };
        _controllers.ControllerBound += () =>
        {
            _controllerPrompt.Hide();
            _lifecycle.SetControllerMissing(false);
        };
        _controllers.Start();

        // Window activation is the desktop analogue of the console constrain: the Guide
        // covering the title and the window losing focus are the same event as far as the
        // game is concerned, and on a dev box with no PLM it is the only source there is.
        // The coordinator de-duplicates, so a console driving both acts once.
        Activated += (_, _) => _lifecycle.SetConstrained(false);
        Deactivated += (_, _) => _lifecycle.SetConstrained(true);

        // Boot flow from main.gd: the acquire-user screen gates the *menu*, not the game -
        // it offers "Continue Offline" for players who cannot or will not sign in.
        _screens.Push(new AcquireUserScreen());
    }

    /// <summary>
    /// Reads the developer identity override used by the local multi-instance Party test
    /// path, from <c>--pf-user=&lt;name&gt;</c> or the <c>PF_CUSTOM_ID</c> environment
    /// variable.
    /// </summary>
    /// <remarks>
    /// This does two jobs, and for a long time only did the first. It gives each instance
    /// its own settings file, so two clients on one PC do not share and overwrite each
    /// other's appearance; and it becomes <see cref="SignInOptions.DeveloperCustomId"/>,
    /// which is what actually gives the instance a distinct identity. Without the second,
    /// every instance signs in as "Player" - indistinguishable in the roster, the
    /// scoreboard and the standings - and the PlayFab custom-id sign-in path declines to
    /// run at all, because it requires a non-empty id.
    /// </remarks>
    private string? ResolveUserToken()
    {
#if DEBUG
        const string Prefix = "--pf-user=";

        foreach (var arg in _commandLineArgs)
        {
            if (arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[Prefix.Length..];
            }
        }

        var environment = Environment.GetEnvironmentVariable("PF_CUSTOM_ID");

        return string.IsNullOrWhiteSpace(environment) ? null : environment;
#else
        // XR-013: LoginWithCustomID authenticates nobody - anyone who guesses the
        // string owns the account. The multi-instance local test path this token also
        // names settings files for has no reason to exist in a shipping build, so the
        // whole path - not just the PlayFab call it eventually reaches - is compiled
        // out here rather than relying on downstream composition to leave it unreachable.
        return null;
#endif
    }

    /// <summary>
    /// Chooses where local saves and settings live.
    /// </summary>
    /// <remarks>
    /// Desktop returns null, which lets each service pick its own per-user default under
    /// LocalAppData. Console has to be told, and the answer is *not* the folder beside
    /// the executable: a packaged title has its install mapped read-only, so every write
    /// there fails - quietly, because the save paths treat a failed write as "settings
    /// are not worth crashing over". Persistent local storage is the writable per-title
    /// location; the old path stays as a last resort so a runtime that refuses the call
    /// is no worse off than before.
    /// </remarks>
    private static string? ResolveLocalDataRoot()
    {
#if GDKX
        // T: is the fallback rather than the install folder: it is writable without asking
        // the GDK anything, so settings at least survive within a session if persistent
        // local storage is refused. The install folder cannot take a write at all.
        return PlatformProviderFactory.ResolveConsoleLocalDataRoot()
            ?? @"T:\NetRumble\LocalData";
#else
        return null;
#endif
    }

    /// <summary>
    /// Reads <c>--match-seconds=&lt;n&gt;</c>, a debug switch that shortens the match clock
    /// so an unattended run can reach the end of a match and its results screen.
    /// </summary>
    /// <remarks>
    /// Debug builds only (XR-003). A retail build must not let a command-line argument
    /// change the match rules.
    /// </remarks>
#if DEBUG
    private static float ResolveMatchSeconds(string[] args)
    {
        const string Prefix = "--match-seconds=";

        foreach (var arg in args)
        {
            if (arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && float.TryParse(
                    arg[Prefix.Length..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var seconds))
            {
                return seconds;
            }
        }

        return 0.0f;
    }
#endif

    /// <summary>
    /// Recomputes <see cref="RenderScale"/> and <see cref="LetterboxOffset"/> from the
    /// current backbuffer. Called from both update and draw: the UI is authored at the
    /// design resolution and scaled uniformly, matching Godot's "canvas_items" stretch
    /// mode (the smaller axis wins, so nothing authored at the edge of the design frame
    /// is cropped away), and input needs the same transform to map the cursor back.
    /// </summary>
    private void UpdateViewportMetrics()
    {
        var viewportWidth = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var viewportHeight = GraphicsDevice.PresentationParameters.BackBufferHeight;

        RenderScale = MathF.Min(
            viewportWidth / (float)DesignWidth,
            viewportHeight / (float)DesignHeight);

        // Letterbox offset, so a non-16:9 window centres the design frame rather than
        // pinning it to the top-left.
        LetterboxOffset = new Vector2(
            (viewportWidth - (DesignWidth * RenderScale)) * 0.5f,
            (viewportHeight - (DesignHeight * RenderScale)) * 0.5f);
    }

    protected override void Update(GameTime gameTime)
    {        // Before the platform pump and before any screen runs: continuations queued since
        // the last frame are the tail halves of work the game already started, so they
        // belong to this frame's beginning rather than trailing a frame behind it.
        _mainThread.Pump();

        // Always first. See the class remarks.
        _platform.Runtime.Pump();

        // MonoGame raises nothing when a pad is unplugged, so the only way to see it is
        // to look. Outside any pause gate on purpose: a disconnect must be noticed while
        // the title is constrained too, which is exactly when it is most likely.
        _controllers?.Update();

        var delta = (float)gameTime.ElapsedGameTime.TotalSeconds;
        // The autopilot substitutes a synthetic keyboard while a match is running, so its
        // ship control travels the same UiInput -> LocalInput path a real player's does.
        // Absent unless --autopilot= was passed, in which case this is the real state.
        var keyboard = Keyboard.GetState();
#if DEBUG
        if (_autopilot is not null)
        {
            keyboard = _autopilot.Filter(keyboard);
        }
#endif

        // Before sampling input, because the cursor has to be mapped into design space
        // with this frame's letterbox, not the one the previous Draw left behind.
        UpdateViewportMetrics();

        _uiInput.Update(
            keyboard,
            GamePad.GetState(PlayerIndex.One),
            Mouse.GetState(),
            delta,
            RenderScale,
            LetterboxOffset);

        if (_context is not null)
        {
            _context.Delta = delta;
            _context.Time = (float)gameTime.TotalGameTime.TotalSeconds;
            _context.Screen = new Rectangle(0, 0, DesignWidth, DesignHeight);
            _screens.Update(_context);

            // After the screens, so a held invite sees the stack they just produced -
            // it waits for the acquire-user screen to hand over before it joins.
            _inviteRouter?.Update();

            // After the screens, so it observes the state they just produced rather than
            // the previous frame's.
#if DEBUG
            _autopilot?.Update(_context);
#endif

            // Last, and on this thread deliberately: it publishes the screen-stack state
            // that the suspend handler reads from an OS thread, so it has to run after
            // every screen change this frame could have made. See
            // LifecycleCoordinator.Tick.
            _lifecycle?.Tick();
        }

        // Turns any gamerpic that finished downloading into a texture. Here rather than in
        // a screen because the graphics device may only be touched from this thread and
        // several screens share the cache.
        _pictures?.Update();

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Starfield.BackgroundColor);

        if (_spriteBatch is null || _context is null)
        {
            base.Draw(gameTime);
            return;
        }

        var viewportWidth = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var viewportHeight = GraphicsDevice.PresentationParameters.BackBufferHeight;

        UpdateViewportMetrics();

        // Screens that own world rendering draw first, outside the UI batch, because they
        // need their own blend states and camera transform.
        _screens.DrawWorld(_context, RenderScale, viewportWidth, viewportHeight);

        var transform = Matrix.CreateScale(RenderScale, RenderScale, 1f)
            * Matrix.CreateTranslation(LetterboxOffset.X, LetterboxOffset.Y, 0f);

        // Premultiplied alpha, because the content pipeline was told to premultiply.
        // BlendState.AlphaBlend is the premultiplied blend in MonoGame; using
        // NonPremultiplied here instead would double-darken every edge pixel.
        _spriteBatch.Begin(
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.LinearClamp,
            transformMatrix: transform);

        _screens.Draw(_context);

        // Above the screen stack, inside the same batch. Last, so nothing can cover it.
        _controllerPrompt.Draw(_context);

        _spriteBatch.End();

        // After everything, so a captured frame is the finished one.
#if DEBUG
        _capture?.Update(GraphicsDevice, gameTime);
#endif

        base.Draw(gameTime);
    }

    private async void StartPlatform()
    {
        _bootState = PlatformBootState.Starting;

        try
        {
            var result = await _platform.Runtime.InitializeAsync().ConfigureAwait(true);

            if (result.Succeeded)
            {
                _bootState = PlatformBootState.Ready;
                _bootDetail = _platform.Name;
                return;
            }

            // A missing native runtime is a supported configuration, not a failure: the
            // front end and offline play still work. Distinguish the two so the UI can say
            // something accurate.
            _bootState = _platform.Runtime.IsRuntimeAvailable
                ? PlatformBootState.Failed
                : PlatformBootState.Degraded;

            _bootDetail = result.Message ?? string.Empty;
        }
        catch (Exception ex)
        {
            _bootState = PlatformBootState.Failed;
            _bootDetail = ex.Message;
        }
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        // First: the loop has stopped, so nothing will drain the continuation queue again,
        // and everything below blocks on work that would otherwise be waiting for a pump
        // that is never coming.
        GameThreadContext.Uninstall();

        // Mirrors _shutdown() in scripts/main.gd: drop the session and release the
        // platform before the window goes away.
        _screens.Clear();
        _audio?.StopAll();

        // The local write first and on its own, because it must not be able to fail or
        // block: the cloud push that Save() normally fires is suppressed here and issued
        // explicitly below, so shutdown waits for it instead of losing it to the process
        // ending mid-request.
        if (_profile is not null)
        {
            _profile.SuppressCloudPush = true;
            _profile.Save();
            _profile.SuppressCloudPush = false;
        }

        FlushSaves();

        _platform.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExiting(sender, args);
    }

    /// <summary>
    /// Waits, briefly, for the cloud writes that would otherwise be abandoned by the
    /// process ending.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. These are best-effort writes to a remote service, and a title
    /// that will not close because a PlayFab request is hanging is a far worse bug than a
    /// settings change that has to be made again on the next machine. The local tier is
    /// already committed by this point, so the timeout costs the cloud copy and nothing
    /// else.
    /// </remarks>
    private void FlushSaves()
    {
        var pending = new List<Task>(2);

        if (_cloudSettings is not null)
        {
            pending.Add(_cloudSettings.PushAsync());
        }

        if (_achievements is not null)
        {
            pending.Add(_achievements.FlushAsync());
        }

        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutdown. Nothing useful can be done with a failure here, and throwing
            // would turn a lost cloud write into a crash on exit.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audio?.Dispose();
            _pictures?.Dispose();
            _pixel?.Dispose();
            _spriteBatch?.Dispose();
        }

        base.Dispose(disposing);
    }

    private enum PlatformBootState
    {
        NotStarted,
        Starting,
        Ready,
        Degraded,
        Failed,
    }
}
