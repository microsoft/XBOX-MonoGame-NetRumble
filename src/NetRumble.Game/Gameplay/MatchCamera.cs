using Microsoft.Xna.Framework;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// Follows the local ship and keeps the view inside the world bounds. Replaces the
/// <c>Camera2D</c> that <c>scripts/ui/gameplay_screen.gd</c> created at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Godot's <c>position_smoothing</c> is a plain lerp with a factor of
/// <c>smoothing_speed * delta</c> - not the framerate-independent exponential form. It is
/// reproduced exactly here rather than "corrected", because the two differ noticeably at
/// this smoothing speed and the camera feel is the whole point of the setting. The factor
/// is clamped at 1 so a long frame cannot overshoot the target.
/// </para>
/// <para>
/// Godot clamps the camera's <em>view rectangle</em> to the limits, not its centre, so
/// the clamp here is inset by half a viewport. When the world is narrower than the view
/// the inset would invert the clamp range; that case centres on the world instead.
/// </para>
/// </remarks>
public sealed class MatchCamera
{
    /// <summary>From <c>_CAMERA_SMOOTHING</c> in <c>gameplay_screen.gd</c>.</summary>
    private const float SmoothingSpeed = 8.0f;

    private readonly float _viewWidth;
    private readonly float _viewHeight;

    private Vector2 _position;
    private bool _hasTarget;

    /// <summary>Creates a camera for a view of the given size, in world units.</summary>
    public MatchCamera(float viewWidth, float viewHeight)
    {
        _viewWidth = viewWidth;
        _viewHeight = viewHeight;
    }

    /// <summary>Smoothed camera centre, in world space.</summary>
    public Vector2 Position => _position;

    /// <summary>Snaps straight to a target, skipping the smoothing.</summary>
    public void SnapTo(Vector2 target, int worldWidth, int worldHeight)
    {
        _position = Clamp(target, worldWidth, worldHeight);
        _hasTarget = true;
    }

    /// <summary>Advances the smoothing toward <paramref name="target"/>.</summary>
    public void Update(Vector2 target, int worldWidth, int worldHeight, float delta)
    {
        var clamped = Clamp(target, worldWidth, worldHeight);

        if (!_hasTarget)
        {
            _position = clamped;
            _hasTarget = true;
            return;
        }

        _position = Vector2.Lerp(_position, clamped, Math.Clamp(SmoothingSpeed * delta, 0.0f, 1.0f));
    }

    /// <summary>
    /// World-to-screen transform, including the design-resolution scale so gameplay
    /// scales with the backbuffer the same way Godot's <c>canvas_items</c> stretch does.
    /// </summary>
    public Matrix ViewMatrix(float renderScale, float viewportWidth, float viewportHeight) =>
        Matrix.CreateTranslation(-_position.X, -_position.Y, 0.0f)
        * Matrix.CreateScale(renderScale, renderScale, 1.0f)
        * Matrix.CreateTranslation(viewportWidth / 2.0f, viewportHeight / 2.0f, 0.0f);

    private Vector2 Clamp(Vector2 target, int worldWidth, int worldHeight)
    {
        var halfW = _viewWidth / 2.0f;
        var halfH = _viewHeight / 2.0f;

        var x = worldWidth <= _viewWidth
            ? worldWidth / 2.0f
            : Math.Clamp(target.X, halfW, worldWidth - halfW);

        var y = worldHeight <= _viewHeight
            ? worldHeight / 2.0f
            : Math.Clamp(target.Y, halfH, worldHeight - halfH);

        return new Vector2(x, y);
    }
}
