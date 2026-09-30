namespace NetRumble.Platform;

/// <summary>
/// Gamepad reading and haptics, over Microsoft GameInput.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>addons/godot_gameinput</c>. The game routes gamepad input through
/// here rather than MonoGame's <c>GamePad</c> because the sample depends on GameInput
/// for device enumeration, hot-plug notification and XR-compliant haptics.
/// </para>
/// <para>
/// Providers that lack GameInput may implement this over any backend — the offline
/// provider wraps MonoGame's own <c>GamePad</c> — so gameplay code never has two input
/// paths to reconcile.
/// </para>
/// </remarks>
public interface IGameInputService
{
    /// <summary>Currently connected gamepads, most recently active first.</summary>
    IReadOnlyList<GameInputDevice> Devices { get; }

    event Action<GameInputDevice>? DeviceConnected;
    event Action<GameInputDevice>? DeviceDisconnected;

    /// <summary>
    /// Reads the current state of a device. Returns a neutral reading for an unknown or
    /// disconnected id, so callers never need a null check.
    /// </summary>
    GamepadReading Read(uint deviceId);

    /// <summary>Reads the device driving the local player, or neutral when there is none.</summary>
    GamepadReading ReadPrimary();

    /// <summary>Sets rumble, 0 to 1 per motor. Providers clamp and no-op when unsupported.</summary>
    void SetVibration(uint deviceId, float lowFrequency, float highFrequency, float leftTrigger, float rightTrigger);

    /// <summary>Stops all haptics on a device. Must be called on suspend and on match end.</summary>
    void StopVibration(uint deviceId);
}

/// <summary>A connected input device.</summary>
public sealed record GameInputDevice
{
    public required uint DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public bool SupportsVibration { get; init; }

    /// <summary>
    /// Index of the user this device is bound to, or -1 when unbound. On console the
    /// GDK binds pads to users; on desktop this is usually -1.
    /// </summary>
    public int UserIndex { get; init; } = -1;
}

/// <summary>
/// One frame of gamepad state, already normalized and deadzoned by the provider.
/// </summary>
/// <remarks>
/// Sticks are -1..1 with Y <b>up-positive</b>, matching the source enum's intent. Note
/// the simulation itself uses Godot's y-down convention (rotation 0 = up, clockwise
/// positive); the conversion happens once, where input is mapped to ship actions.
/// </remarks>
public readonly record struct GamepadReading
{
    public float LeftStickX { get; init; }
    public float LeftStickY { get; init; }
    public float RightStickX { get; init; }
    public float RightStickY { get; init; }
    public float LeftTrigger { get; init; }
    public float RightTrigger { get; init; }
    public GamepadButtons Buttons { get; init; }

    /// <summary>True when the device was connected when this reading was taken.</summary>
    public bool IsConnected { get; init; }

    public bool IsDown(GamepadButtons button) => (Buttons & button) != 0;

    /// <summary>Neutral reading, returned for missing or disconnected devices.</summary>
    public static GamepadReading Empty => default;
}

[Flags]
public enum GamepadButtons
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    DPadUp = 1 << 4,
    DPadDown = 1 << 5,
    DPadLeft = 1 << 6,
    DPadRight = 1 << 7,
    LeftShoulder = 1 << 8,
    RightShoulder = 1 << 9,
    LeftThumbstick = 1 << 10,
    RightThumbstick = 1 << 11,
    Menu = 1 << 12,
    View = 1 << 13,
}
