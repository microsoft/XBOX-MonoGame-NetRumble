using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// Turns keyboard and gamepad state into the movement, fire and mine inputs the
/// simulation consumes. Replaces <c>Input.get_vector</c> and the action map in
/// <c>project.godot</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bindings are taken verbatim from the <c>[input]</c> section: movement on WASD or the
/// left stick, firing on the arrow keys or the right stick, and mine deployment on Space
/// or the right trigger.
/// </para>
/// <para>
/// <b>Y axis.</b> MonoGame reports thumbstick Y as positive-up; Godot's action map binds
/// the same axis positive-down to match screen space, which is also what the simulation
/// expects. The stick Y is therefore negated, and getting this wrong inverts flight
/// controls without any other visible symptom.
/// </para>
/// <para>
/// <b>Mine deploy is reported as a level, not an edge.</b> The press-edge latch lives in
/// <c>ShipInput</c>, exactly as it did in the GDScript. Latching here as well would look
/// harmless but breaks on respawn: <c>ShipInput.ResetMineInput</c> clears the simulation
/// latch, and if the player were still holding the key this class would never produce
/// another edge, so mines would stop working for the rest of the match.
/// </para>
/// </remarks>
public sealed class LocalInput
{
    /// <summary>
    /// Matches Godot's default action deadzone. Applied to the assembled vector rather
    /// than per-axis, which is what <c>Input.get_vector</c> does.
    /// </summary>
    private const float Deadzone = 0.2f;

    /// <summary>Analogue value above which the right trigger counts as pressed.</summary>
    private const float TriggerThreshold = 0.5f;

    /// <summary>Movement direction, magnitude at most one.</summary>
    public System.Numerics.Vector2 Movement { get; private set; }

    /// <summary>Fire direction, magnitude at most one. Zero means hold fire.</summary>
    public System.Numerics.Vector2 Fire { get; private set; }

    /// <summary>True for as long as the mine input is held.</summary>
    public bool DeployMineHeld { get; private set; }

    /// <summary>Samples input for this frame.</summary>
    public void Update(KeyboardState keys, GamePadState pad)
    {
        var movement = KeyVector(keys, Keys.A, Keys.D, Keys.W, Keys.S);
        var fire = KeyVector(keys, Keys.Left, Keys.Right, Keys.Up, Keys.Down);

        if (pad.IsConnected)
        {
            if (movement == Vector2.Zero)
            {
                movement = new Vector2(pad.ThumbSticks.Left.X, -pad.ThumbSticks.Left.Y);
            }

            if (fire == Vector2.Zero)
            {
                fire = new Vector2(pad.ThumbSticks.Right.X, -pad.ThumbSticks.Right.Y);
            }
        }

        Movement = Condition(movement);
        Fire = Condition(fire);

        DeployMineHeld = keys.IsKeyDown(Keys.Space)
            || (pad.IsConnected && pad.Triggers.Right > TriggerThreshold);
    }

    /// <summary>Clears the sampled input, for when the match is not accepting it.</summary>
    public void Reset()
    {
        Movement = System.Numerics.Vector2.Zero;
        Fire = System.Numerics.Vector2.Zero;
        DeployMineHeld = false;
    }

    private static Vector2 KeyVector(KeyboardState keys, Keys left, Keys right, Keys up, Keys down)
    {
        var x = (keys.IsKeyDown(right) ? 1.0f : 0.0f) - (keys.IsKeyDown(left) ? 1.0f : 0.0f);
        var y = (keys.IsKeyDown(down) ? 1.0f : 0.0f) - (keys.IsKeyDown(up) ? 1.0f : 0.0f);
        return new Vector2(x, y);
    }

    /// <summary>
    /// Applies the deadzone and clamps to the unit disc, so a diagonal on the keyboard is
    /// not faster than a cardinal. Returns the simulation's vector type, since that is the
    /// only consumer.
    /// </summary>
    private static System.Numerics.Vector2 Condition(Vector2 value)
    {
        var length = value.Length();

        if (length <= Deadzone)
        {
            return System.Numerics.Vector2.Zero;
        }

        if (length > 1.0f)
        {
            value /= length;
        }

        return new System.Numerics.Vector2(value.X, value.Y);
    }
}
