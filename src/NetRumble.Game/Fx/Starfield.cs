using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.Fx;

/// <summary>
/// The three-layer parallax starfield, ported from <c>scripts/fx/starfield.gd</c> and
/// through it from <c>Game/Gameplay/Starfield/StarfieldBackground.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No 1024x1024 textures.</b> The Godot version baked each layer into a tile image
/// because <c>Parallax2D</c> needs a <c>Sprite2D</c> to repeat. That cost 4 MB of RGBA per
/// layer to store about ninety lit pixels. Here the star positions are kept as data and
/// stamped with the shared 1x1 white texture, which is the same picture for a few hundred
/// sprite quads a frame and no texture memory at all.
/// </para>
/// <para>
/// <b>Parallax without a scene graph.</b> <c>Parallax2D</c> moves a layer by
/// <c>scroll_scale</c> times the camera's displacement and wraps it every
/// <see cref="TileSize"/>. Both are done explicitly in <see cref="Draw"/>; the wrap is why
/// the tile loop starts at a negative offset rather than at zero.
/// </para>
/// <para>
/// <b>The drift is absolute, not accumulated.</b> <c>StarfieldBackground::Update</c> walked
/// the field around a slow circle each frame; integrated, that is just a translation, so
/// each layer is positioned from the clock directly. Driving it off total elapsed time
/// rather than a per-instance timer is what let the Godot original keep the sky continuous
/// when a screen change rebuilt the starfield, and the same property is preserved here.
/// </para>
/// <para>
/// <b>Star placement differs from the Godot build.</b> The seeds and the distributions are
/// carried over verbatim, but .NET's <see cref="Random"/> is not Godot's PCG32, so the
/// individual stars land elsewhere. Density, brightness range and the twin-pixel frequency -
/// everything that determines how the field reads - are identical, and there is no gameplay
/// or netcode dependency on where a star is.
/// </para>
/// </remarks>
public sealed class Starfield
{
    /// <summary>
    /// <c>BACKGROUND_COLOR</c>: the near-black blue the whole game clears to.
    /// </summary>
    public static readonly Color BackgroundColor = new(0, 0, 16);

    /// <summary>Godot <c>TILE_SIZE</c>: the parallax repeat period, in design pixels.</summary>
    private const float TileSize = 1024f;

    /// <summary>
    /// Godot <c>PARALLAX_PERIOD</c>. One lap of the drift circle takes
    /// <c>2 * pi * PARALLAX_PERIOD</c>, about 188 seconds, so the sky wanders rather than
    /// scrolling in any fixed direction.
    /// </summary>
    private const float ParallaxPeriod = 30.0f;

    /// <summary>Godot <c>PARALLAX_AMPLITUDE</c>: radius of that circle, in design pixels.</summary>
    private const float ParallaxAmplitude = 2048.0f;

    /// <summary>
    /// <c>LAYER_DATA</c>, verbatim. Nearer layers hold more, brighter stars and react more
    /// strongly to camera movement.
    /// </summary>
    private static readonly LayerDefinition[] LayerData =
    [
        new("Near", 0.9f, 96, 1.0f, 101),
        new("Mid", 0.55f, 88, 0.63f, 202),
        new("Far", 0.25f, 72, 0.38f, 303),
    ];

    private readonly Layer[] _layers;

    private Vector2 _travelled;

    public Starfield()
    {
        _layers = new Layer[LayerData.Length];

        for (var i = 0; i < LayerData.Length; i++)
        {
            _layers[i] = BuildLayer(LayerData[i]);
        }
    }

    /// <summary>
    /// Samples the drift orbit. <paramref name="totalSeconds"/> is wall-clock elapsed time,
    /// not a frame delta.
    /// </summary>
    public void Update(float totalSeconds)
    {
        var t = totalSeconds / ParallaxPeriod;

        // The -1 on the cosine starts the orbit at the origin, so the field opens
        // undisplaced instead of a full amplitude off to one side.
        _travelled = new Vector2(MathF.Cos(t) - 1.0f, MathF.Sin(t)) * ParallaxAmplitude;
    }

    /// <summary>
    /// Draws the field beneath the world.
    /// </summary>
    /// <param name="pixel">The shared 1x1 white texture.</param>
    /// <param name="cameraPosition">Camera centre in world units.</param>
    /// <param name="renderScale">Design-space to backbuffer scale.</param>
    /// <param name="viewportWidth">Backbuffer width, in real pixels.</param>
    /// <param name="viewportHeight">Backbuffer height, in real pixels.</param>
    public void Draw(
        SpriteBatch spriteBatch,
        Texture2D pixel,
        Vector2 cameraPosition,
        float renderScale,
        float viewportWidth,
        float viewportHeight)
    {
        // Everything below is computed in design pixels and scaled to the backbuffer in one
        // step, so a star stays one design pixel however the window is sized - which is what
        // Godot's "canvas_items" stretch mode did to the original ImageTexture tiles.
        var view = Matrix.CreateScale(renderScale, renderScale, 1.0f);

        var visibleWidth = viewportWidth / renderScale;
        var visibleHeight = viewportHeight / renderScale;

        spriteBatch.Begin(
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.PointClamp,
            transformMatrix: view);

        foreach (var layer in _layers)
        {
            // A layer trails the camera by scroll_scale, and the drift is applied at the
            // same depth factor - Parallax2D.scroll_offset translates 1:1 and is not itself
            // scaled, which is why starfield.gd multiplied it in by hand.
            var offset = -(cameraPosition + _travelled) * layer.ScrollScale;

            for (var x = Wrap(offset.X); x < visibleWidth; x += TileSize)
            {
                for (var y = Wrap(offset.Y); y < visibleHeight; y += TileSize)
                {
                    var tile = new Vector2(x, y);

                    foreach (var star in layer.Stars)
                    {
                        spriteBatch.Draw(pixel, tile + star.Position, star.Color);
                    }
                }
            }
        }

        spriteBatch.End();
    }

    /// <summary>
    /// Brings a layer origin into <c>[-TileSize, 0)</c>, so the tile loop always starts one
    /// tile off the top-left and covers the viewport whichever way the layer has slid.
    /// </summary>
    private static float Wrap(float value)
    {
        var wrapped = value % TileSize;
        return wrapped >= 0f ? wrapped - TileSize : wrapped;
    }

    private static Layer BuildLayer(LayerDefinition definition)
    {
        var random = new Random(definition.Seed);
        var stars = new List<Star>(definition.Count);

        for (var i = 0; i < definition.Count; i++)
        {
            var x = random.Next(0, (int)TileSize);
            var y = random.Next(0, (int)TileSize);
            var brightness = 0.55f + ((float)random.NextDouble() * 0.45f);

            // Color.White * a rather than a straight colour: the rest of the frame is
            // premultiplied, and folding alpha into RGB here keeps this batch consistent
            // with it.
            var colour = Color.White * (definition.Alpha * brightness);
            stars.Add(new Star(new Vector2(x, y), colour));

            // Godot gave 18% of stars a dimmer neighbour to the right, which is what stops
            // a field of single pixels reading as uniform noise.
            if (random.NextDouble() < 0.18 && x + 1 < TileSize)
            {
                stars.Add(new Star(new Vector2(x + 1, y), colour * 0.7f));
            }
        }

        return new Layer(definition.ScrollScale, [.. stars]);
    }

    private readonly record struct LayerDefinition(
        string Name,
        float ScrollScale,
        int Count,
        float Alpha,
        int Seed);

    private readonly record struct Star(Vector2 Position, Color Color);

    private readonly record struct Layer(float ScrollScale, Star[] Stars);
}
