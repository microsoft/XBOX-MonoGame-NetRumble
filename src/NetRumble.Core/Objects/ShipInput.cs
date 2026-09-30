using System.Numerics;

namespace NetRumble.Core.Objects;

/// <summary>
/// Per-ship input snapshot, ported from <c>scripts/gameplay/ship_input.gd</c>
/// (originally <c>Game/Gameplay/ShipInput.cpp</c>).
/// </summary>
/// <remarks>
/// A single instance drives the local ship every frame; remote ships receive their state
/// over the wire. Movement and fire are expressed in screen space where up is -Y.
/// </remarks>
public sealed class ShipInput
{
    /// <summary>
    /// Legitimate input is an analog stick (at most 1) plus un-normalised digital keys
    /// (at most the square root of 2); anything past this bound is treated as synthetic
    /// and clamped.
    /// </summary>
    public const float MaxInputMagnitude = 4.0f;

    /// <summary>
    /// Seconds the authority keeps acting on a remote player's last input before treating
    /// them as idle.
    /// </summary>
    /// <remarks>
    /// Input arrives on an unreliable channel, so silence means the peer stalled or
    /// dropped. Without this their ship keeps thrusting in the last direction it heard
    /// about and sails off across the map.
    /// </remarks>
    public const float RemoteInputTimeout = 1.0f;

    private bool _canDeployMine = true;
    private int _lastRemoteInputSequence;
    private float _timeSinceRemoteInput;

    public Vector2 MovementDirection { get; set; }

    public Vector2 FireDirection { get; set; }

    public bool DeployMinePressed { get; set; }

    /// <summary>
    /// True while the last local frame aimed with the mouse rather than the right stick
    /// or the fire keys.
    /// </summary>
    /// <remarks>
    /// The reticle reads this to decide whether to draw itself, so it appears the moment
    /// the mouse is used and gets out of the way for a pad.
    /// </remarks>
    public bool UsingMouseAim { get; private set; }

    /// <summary>
    /// Applies this frame's local device state.
    /// </summary>
    /// <param name="deployMineHeld">
    /// Whether the deploy-mine control is currently held. The press edge is derived here
    /// rather than by the caller so the rule stays with the rest of the input model.
    /// </param>
    /// <param name="mouseAim">
    /// The world-space direction from this ship to the cursor while the player is holding
    /// the mouse to aim, or zero otherwise. <see cref="World"/> computes it because
    /// resolving the cursor to world space needs the ship's position and the gameplay
    /// camera, neither of which this class can see.
    /// </param>
    /// <remarks>
    /// <para>
    /// The Godot version read <c>Input.get_vector</c> directly. This assembly has no input
    /// device access, so the game layer samples the keyboard and gamepad and passes the
    /// already-resolved vectors in.
    /// </para>
    /// <para>
    /// Mouse aim wins over the stick when present: holding the button is an unambiguous
    /// "shoot there", whereas the stick may just be resting off-centre.
    /// </para>
    /// </remarks>
    public void ProcessLocalInput(
        Vector2 movement,
        Vector2 fire,
        bool deployMineHeld,
        Vector2 mouseAim = default)
    {
        // Deploy-mine is a one-frame event: cleared each frame so it is true only on the
        // press edge, otherwise a single press would deploy mines forever.
        DeployMinePressed = false;

        MovementDirection = movement;

        UsingMouseAim = !IsZeroApprox(mouseAim);
        FireDirection = UsingMouseAim ? mouseAim : fire;

        if (deployMineHeld)
        {
            if (_canDeployMine)
            {
                DeployMinePressed = true;
                _canDeployMine = false;
            }
        }
        else
        {
            _canDeployMine = true;
        }
    }

    /// <summary>Applies input received for a remote player.</summary>
    /// <remarks>
    /// Remote input is attacker-controlled. A non-finite vector would reach the velocity
    /// clamp as infinity, where infinity times (max / infinity) yields NaN and poisons
    /// every snapshot built from it, so the packet is dropped rather than applied.
    /// </remarks>
    public void UpdateRemoteInput(Vector2 movement, Vector2 fire, bool deploy, int sequence)
    {
        if (!IsFinite(movement) || !IsFinite(fire))
        {
            return;
        }

        if (sequence > _lastRemoteInputSequence)
        {
            MovementDirection = ClampMagnitude(movement);
            FireDirection = ClampMagnitude(fire);
            _lastRemoteInputSequence = sequence;
            _timeSinceRemoteInput = 0.0f;
        }

        DeployMinePressed = DeployMinePressed || deploy;
    }

    /// <summary>
    /// Ages the last input received for a remote player and drops it once the peer has
    /// gone quiet. Called by the authority for every ship it does not own locally.
    /// </summary>
    public void TickRemoteInput(float delta)
    {
        if (IsZeroApprox(MovementDirection) && IsZeroApprox(FireDirection))
        {
            return;
        }

        _timeSinceRemoteInput += delta;

        if (_timeSinceRemoteInput >= RemoteInputTimeout)
        {
            MovementDirection = Vector2.Zero;
            FireDirection = Vector2.Zero;
        }
    }

    /// <summary>
    /// Clears everything a respawning ship must not inherit from its last life.
    /// </summary>
    /// <remarks>
    /// The remote input sequence is deliberately left alone: rewinding the receiver's
    /// counter would make every packet still in flight look newer than it is, so the
    /// ship would immediately re-apply the very input this call exists to discard.
    /// </remarks>
    public void Reset()
    {
        MovementDirection = Vector2.Zero;
        FireDirection = Vector2.Zero;
        UsingMouseAim = false;
        _timeSinceRemoteInput = 0.0f;
        ResetMineInput();
    }

    public void ResetMineInput()
    {
        DeployMinePressed = false;
        _canDeployMine = true;
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    /// <remarks>
    /// Godot's <c>Vector2.is_zero_approx</c> tests each component against its epsilon of
    /// 1e-6, which is not the same as testing the vector's length, so it is reproduced
    /// component-wise here.
    /// </remarks>
    private static bool IsZeroApprox(Vector2 value)
        => MathF.Abs(value.X) < 1e-6f && MathF.Abs(value.Y) < 1e-6f;

    private static Vector2 ClampMagnitude(Vector2 value)
    {
        var lengthSquared = value.LengthSquared();

        return lengthSquared <= MaxInputMagnitude * MaxInputMagnitude
            ? value
            : value * (MaxInputMagnitude / MathF.Sqrt(lengthSquared));
    }
}
