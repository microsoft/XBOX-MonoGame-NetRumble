using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// The mouse aiming reticle, drawn in the gameplay world. Ported from
/// <c>scripts/gameplay/aim_reticle.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Mouse aiming (<c>World.SetLocalInput</c>'s cursor argument) needs a visible cursor:
/// the system arrow is too small to aim with and is drawn in screen space, so it slides
/// against the world as the camera moves.
/// </para>
/// <para>
/// This draws in world space, inside the same batch transform the entities use, so the
/// reticle sits exactly on the world point the shots will travel towards. It is drawn
/// from the 1x1 pixel texture rather than textured, so it stays crisp at any resolution
/// and needs no new art.
/// </para>
/// <para>
/// Visibility follows the device actually in use: it appears on mouse input and gets out
/// of the way as soon as the player touches a pad, so a controller session never has a
/// stray crosshair parked wherever the mouse was left. The OS cursor is hidden while the
/// reticle is showing and restored the moment it is not - including on teardown, so a
/// match can never leave the player without a pointer in the menus.
/// </para>
/// </remarks>
public sealed class AimReticle
{
    /// <summary>Ring radius in world units, and how far the four ticks sit outside it.</summary>
    private const float Radius = 17.0f;

    private const float TickLength = 7.0f;
    private const float TickGap = 4.0f;
    private const float LineWidth = 2.0f;
    private const float CenterDotRadius = 1.75f;

    /// <summary>Segments in the ring. Godot's <c>draw_arc</c> default for this size.</summary>
    private const int RingSegments = 48;

    /// <summary>
    /// How far the ring contracts while the fire button is held, as a fraction of
    /// <see cref="Radius"/>.
    /// </summary>
    private const float FiringScale = 0.82f;

    /// <summary>How quickly the ring converges on its firing/idle size, in 1/seconds.</summary>
    private const float ScaleSmoothing = 18.0f;

    /// <summary>
    /// Degrees per second the tick ring rotates while firing. It is the only moving part;
    /// the crosshair itself stays nailed to the aim point.
    /// </summary>
    private const float FiringSpin = 90.0f;

    private static readonly Color IdleColor = new(0.72f, 0.78f, 1.0f, 0.72f);
    private static readonly Color FiringColor = new(1.0f, 0.45f, 0.35f, 0.95f);

    private Vector2 _position;
    private bool _active;
    private bool _firing;
    private bool _uiOccluded;
    private float _ringScale = 1.0f;
    private float _spinDegrees;

    /// <summary>True when the reticle is drawing and the OS cursor should be hidden.</summary>
    public bool IsVisible => _active && !_uiOccluded;

    /// <summary>
    /// Samples this frame's pointer state.
    /// </summary>
    /// <param name="worldPosition">The cursor, resolved to world space.</param>
    /// <param name="mouseIsActiveDevice">
    /// Whether the mouse was the last pointing device touched.
    /// </param>
    /// <param name="firing">Whether the aim button is held.</param>
    /// <param name="uiOccluded">
    /// True while a popup covers the match. The reticle is deliberately world-space, so
    /// menu coverage hands the OS pointer back rather than drawing a second, partly
    /// hidden cursor under the pause rows.
    /// </param>
    public void Update(
        Vector2 worldPosition,
        bool mouseIsActiveDevice,
        bool firing,
        bool uiOccluded,
        float delta)
    {
        _active = mouseIsActiveDevice;
        _uiOccluded = uiOccluded;
        _position = worldPosition;

        if (!IsVisible)
        {
            return;
        }

        _firing = firing;

        // Godot's lerpf with a delta-scaled weight, reproduced rather than "corrected" to
        // the framerate-independent form, for the same reason MatchCamera does.
        var target = _firing ? FiringScale : 1.0f;
        _ringScale = MathHelper.Lerp(_ringScale, target, Math.Clamp(ScaleSmoothing * delta, 0.0f, 1.0f));

        if (_firing)
        {
            _spinDegrees = (_spinDegrees + (FiringSpin * delta)) % 360.0f;
        }
    }

    /// <summary>Draws the reticle into an already-open world-space batch.</summary>
    public void Draw(SpriteBatch batch, Texture2D pixel)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!IsVisible)
        {
            return;
        }

        var color = _firing ? FiringColor : IdleColor;
        var ringRadius = Radius * _ringScale;

        DrawRing(batch, pixel, ringRadius, color);
        DrawDot(batch, pixel, color);

        var spin = MathHelper.ToRadians(_spinDegrees);

        for (var index = 0; index < 4; index++)
        {
            var angle = spin + (MathHelper.TwoPi * index / 4.0f);
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));

            DrawLine(
                batch,
                pixel,
                _position + (direction * (ringRadius + TickGap)),
                _position + (direction * (ringRadius + TickGap + TickLength)),
                color);
        }
    }

    private void DrawRing(SpriteBatch batch, Texture2D pixel, float radius, Color color)
    {
        var step = MathHelper.TwoPi / RingSegments;
        var previous = _position + new Vector2(radius, 0.0f);

        for (var i = 1; i <= RingSegments; i++)
        {
            var angle = step * i;
            var next = _position + new Vector2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
            DrawLine(batch, pixel, previous, next, color);
            previous = next;
        }
    }

    /// <summary>
    /// The centre dot, as a short fat line rather than a filled circle: at this radius the
    /// two are indistinguishable and this needs no extra geometry.
    /// </summary>
    private void DrawDot(SpriteBatch batch, Texture2D pixel, Color color)
        => batch.Draw(
            pixel,
            _position,
            null,
            color,
            0.0f,
            new Vector2(0.5f, 0.5f),
            CenterDotRadius * 2.0f,
            SpriteEffects.None,
            0.0f);

    /// <summary>A 1x1 pixel stretched and rotated to span the segment.</summary>
    private static void DrawLine(SpriteBatch batch, Texture2D pixel, Vector2 from, Vector2 to, Color color)
    {
        var delta = to - from;
        var length = delta.Length();

        if (length <= 0.0f)
        {
            return;
        }

        batch.Draw(
            pixel,
            from,
            null,
            color,
            MathF.Atan2(delta.Y, delta.X),

            // Origin on the left edge, vertically centred, so the quad grows along the
            // segment and straddles it rather than sitting to one side.
            new Vector2(0.0f, 0.5f),
            new Vector2(length, LineWidth),
            SpriteEffects.None,
            0.0f);
    }
}
