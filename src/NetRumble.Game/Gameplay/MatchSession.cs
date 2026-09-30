using NetRumble.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Objects;
using NetRumble.Core.Tuning;
using NetRumble.Game.Audio;
using NetRumble.Game.Content;
using NetRumble.Game.Fx;
using NetRumble.Game.Profile;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// One running match and everything drawn inside it: the simulation, the director that
/// drives its state machine, the camera, the renderer, the particle system and the sky.
/// </summary>
/// <remarks>
/// <para>
/// This is the split the Godot version got through the scene tree: <c>world.tscn</c> owned
/// the simulation and its visuals, and <c>gameplay_screen.tscn</c> owned the HUD on top.
/// Pulling it out of the game class means the screen stack can create, suspend and destroy
/// a match the same way the original freed the world scene.
/// </para>
/// <para>
/// The session deliberately does <em>not</em> own the audio manager or the asset registry -
/// those outlive any single match and are passed in.
/// </para>
/// </remarks>
public sealed class MatchSession
{
    private readonly AudioManager _audio;
    private readonly LocalInput _input = new();
    private readonly Color _effectTint;

    /// <summary>
    /// Screen-to-world transform, cached from the last <see cref="Draw"/>.
    /// </summary>
    /// <remarks>
    /// The cursor has to be resolved during <see cref="Update"/>, which does not know the
    /// backbuffer size or the render scale - those arrive with the draw call. Reusing the
    /// previous frame's transform is only ever stale across a window resize, by one frame,
    /// which no player can see. Null until the first frame has been drawn.
    /// </remarks>
    private Matrix? _screenToWorld;

    public MatchSession(
        AssetRegistry assets,
        UiTheme theme,
        AudioManager audio,
        Random random,
        PlayerProfile profile,
        IMatchNetwork network)
    {
        _audio = audio;

        World = new World(random);
        Director = new MatchDirector(network);
        Director.Setup(World);

        // The world only decides *when* an event happens; the FX layer owns the single
        // event-to-particles-and-sound mapping, exactly as ParticleManager did.
        World.GameplayEvent += OnGameplayEvent;

        var tint = TuningLibrary.Palette.ColorAt(profile.ShipColorId);
        _effectTint = new Color(tint.R, tint.G, tint.B, tint.A);

        Particles = new ParticleSystem(assets, random);
        Starfield = new Starfield();
        Camera = new MatchCamera(NetRumbleGame.DesignWidth, NetRumbleGame.DesignHeight);
        Renderer = new MatchRenderer(assets, theme, random);
        Reticle = new AimReticle();
        World.LocalShipChanged += OnLocalShipChanged;

        if (World.LocalShip is { } localShip)
        {
            CenterCameraOn(localShip);
        }
        else
        {
            // A client does not have a local ship until MatchCreated arrives. Keep the
            // initial transform valid, then snap to the ship from OnLocalShipChanged.
            Camera.SnapTo(
                new Vector2(World.WorldWidth / 2f, World.WorldHeight / 2f),
                World.WorldWidth,
                World.WorldHeight);
        }
    }

    public World World { get; }

    public MatchDirector Director { get; }

    public MatchCamera Camera { get; }

    /// <summary>
    /// True once the initial camera snap has used the local ship rather than the world
    /// centre. Observed by the end-to-end autopilot.
    /// </summary>
    internal bool CameraStartedOnLocalShip { get; private set; }

    public MatchRenderer Renderer { get; }

    /// <summary>The mouse aiming crosshair. Draws only while the mouse is in use.</summary>
    public AimReticle Reticle { get; }

    public ParticleSystem Particles { get; }

    public Starfield Starfield { get; }

    /// <summary>
    /// Set false while a popup covers the match, so the in-game menu freezes the
    /// simulation the way pausing the Godot scene tree did.
    /// </summary>
    public bool IsSimulating { get; set; } = true;

    /// <summary>
    /// Advances the match. <paramref name="acceptInput"/> is false while a popup owns
    /// input, so the ship coasts rather than acting on a stale command.
    /// </summary>
    /// <remarks>
    /// The device states are passed in rather than sampled here. Calling
    /// <c>Keyboard.GetState()</c> at this depth quietly gave gameplay a second, unfiltered
    /// source of input: everything above reads the state <c>NetRumbleGame</c> prepares, so
    /// the two agreed for a player at a real keyboard and disagreed for anything that
    /// substitutes one. The autopilot substitutes one, which is why its ships never moved
    /// while its menus responded perfectly.
    /// </remarks>
    public void Update(
        float delta,
        float totalSeconds,
        bool acceptInput,
        KeyboardState keys,
        GamePadState pad,
        MouseState mouse,
        bool mouseIsActiveDevice)
    {
        if (acceptInput)
        {
            _input.Update(keys, pad);
        }
        else
        {
            _input.Reset();
        }

        var cursor = MouseWorldPosition(mouse);

        // Holding the left button is the aim gesture. Input the match is not accepting -
        // a popup is up - must not aim, or a click on a menu row would fire the ship.
        var aiming = acceptInput && mouse.LeftButton == ButtonState.Pressed;

        Reticle.Update(
            cursor ?? Vector2.Zero,
            mouseIsActiveDevice && cursor is not null,
            aiming,
            uiOccluded: !acceptInput || !IsSimulating,
            delta);

        if (IsSimulating)
        {
            // Input is pushed before the tick so the ship acts on this frame's input rather
            // than last frame's, matching the GDScript reading input in _physics_process
            // before World.tick.
            World.SetLocalInput(
                _input.Movement,
                _input.Fire,
                _input.DeployMineHeld,
                aiming && cursor is { } at ? new System.Numerics.Vector2(at.X, at.Y) : null);

            Director.Tick(delta);
        }

        var target = World.LocalShip is { IsActive: true } ship
            ? new Vector2(ship.Position.X, ship.Position.Y)
            : Camera.Position;

        Camera.Update(target, World.WorldWidth, World.WorldHeight, delta);

        // Positional sounds are heard from the view centre; nothing in the original ever
        // moved the listener away from the camera.
        _audio.SetListener(Camera.Position);

        Renderer.Update(delta);
        Particles.Update(delta);

        // The drift is sampled from the clock rather than integrated, so the sky is
        // continuous across a screen change - see Starfield.
        Starfield.Update(totalSeconds);
    }

    /// <summary>
    /// Draws the world. The caller must not have a batch open: this opens its own, because
    /// the starfield, the world and the particles each need different blend states.
    /// </summary>
    public void Draw(SpriteBatch batch, Texture2D pixel, float renderScale, int viewportWidth, int viewportHeight)
    {
        var view = Camera.ViewMatrix(renderScale, viewportWidth, viewportHeight);
        _screenToWorld = Matrix.Invert(view);

        // Draw order mirrors the Godot node parenting: the starfield lived in the
        // background container, the world next, and the particle manager was added to the
        // world container *after* the world, so effects sit above every entity.
        Starfield.Draw(batch, pixel, Camera.Position, renderScale, viewportWidth, viewportHeight);
        Renderer.Draw(batch, World, view);
        Particles.Draw(batch, view);

        if (Reticle.IsVisible)
        {
            // Its own batch: the reticle is drawn from the pixel texture and sat at
            // z_index 100 in the original, above every entity and every effect.
            batch.Begin(
                blendState: BlendState.AlphaBlend,
                samplerState: SamplerState.PointClamp,
                transformMatrix: view);

            Reticle.Draw(batch, pixel);

            batch.End();
        }
    }

    /// <summary>
    /// The cursor in world space, or null before the first frame has established a
    /// transform to invert.
    /// </summary>
    private Vector2? MouseWorldPosition(MouseState mouse)
        => _screenToWorld is { } inverse
            ? Vector2.Transform(new Vector2(mouse.X, mouse.Y), inverse)
            : null;

    /// <summary>Tears the match down, matching <c>world.queue_free()</c>.</summary>
    public void Dispose()
    {
        World.GameplayEvent -= OnGameplayEvent;
        World.LocalShipChanged -= OnLocalShipChanged;
        Director.Dispose();
        World.DetachNetwork();
        Particles.Clear();
    }

    private void OnLocalShipChanged(Ship? ship)
    {
        if (ship is not null)
        {
            CenterCameraOn(ship);
        }
    }

    private void CenterCameraOn(Ship ship)
    {
        Camera.SnapTo(
            new Vector2(ship.Position.X, ship.Position.Y),
            World.WorldWidth,
            World.WorldHeight);
        CameraStartedOnLocalShip = true;
    }

    private void OnGameplayEvent(GameplayEventType eventType, System.Numerics.Vector2 position)
    {
        var at = new Vector2(position.X, position.Y);
        Particles.PlayEvent(eventType, at, _effectTint);
        _audio.PlayEvent(eventType, at);
    }
}
