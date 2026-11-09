namespace NetRumble.Platform.GameCore.Services;

/// <summary>Input readings belong to MonoGame; controller associations belong to identity.</summary>
internal sealed class NullGameInputService : IGameInputService
{
    public IReadOnlyList<GameInputDevice> Devices => Array.Empty<GameInputDevice>();

#pragma warning disable CS0067 // Never raised; part of the contract.
    public event Action<GameInputDevice>? DeviceConnected;
    public event Action<GameInputDevice>? DeviceDisconnected;
#pragma warning restore CS0067

    public GamepadReading Read(uint deviceId) => GamepadReading.Empty;
    public GamepadReading ReadPrimary() => GamepadReading.Empty;
    public void SetVibration(uint deviceId, float lowFrequency, float highFrequency, float leftTrigger, float rightTrigger) { }
    public void StopVibration(uint deviceId) { }
}
