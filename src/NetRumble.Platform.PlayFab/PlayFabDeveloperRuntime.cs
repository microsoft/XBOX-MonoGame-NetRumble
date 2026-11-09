using GDK.Net;
using GDK.Net.PlayFab;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.PlayFab;

/// <summary>Native runtime ownership for the offline PlayFab developer path.</summary>
internal sealed class PlayFabDeveloperRuntime(
    IPlatformRuntime inner, PlayFabOnlineServices services,
    Func<GameRuntime> startRuntime, Action<GameRuntime> stopRuntime)
    : IPlatformRuntime
{
    private GameRuntime? _native;
    private bool _shutdown;
    private bool _ready;

    public bool IsInitialized => inner.IsInitialized && (_ready || services.Configuration.IsPlaceholder);
    public bool IsRuntimeAvailable => _native is not null;
    public PlatformDeviceKind DeviceKind => inner.DeviceKind;
    public bool IsConnectivityKnown => inner.IsConnectivityKnown;
    public bool IsOnline => inner.IsOnline;
    public string OfflineReason => inner.OfflineReason;

    public event Action<bool>? ConnectivityChanged
    {
        add => inner.ConnectivityChanged += value;
        remove => inner.ConnectivityChanged -= value;
    }

    private event Action<PlatformResult>? NativeRuntimeError;

    public event Action<PlatformResult>? RuntimeError
    {
        add
        {
            inner.RuntimeError += value;
            NativeRuntimeError += value;
        }
        remove
        {
            inner.RuntimeError -= value;
            NativeRuntimeError -= value;
        }
    }

    public event Action<PlatformLifecycleEvent>? LifecycleChanged
    {
        add => inner.LifecycleChanged += value;
        remove => inner.LifecycleChanged -= value;
    }

    public async Task<PlatformResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (result.Failed || _ready || services.Configuration.IsPlaceholder)
        {
            return result;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _native ??= startRuntime();
            if (!PlayFabRuntime.IsInitialized)
            {
                PlayFabRuntime.Initialize();
            }
            _ready = true;
            return PlatformResult.Ok();
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            CrashLog.Mark($"playfab: native developer runtime unavailable: {ex.Message}");
            return PlayFabFailure.FromException(ex, "PlayFab requires the Microsoft GDK native runtime.");
        }
    }

    public void Pump()
    {
        inner.Pump();
        try
        {
            services.Pump();
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            NativeRuntimeError?.Invoke(PlayFabFailure.FromException(ex, "PlayFab could not process online matches."));
        }
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown)
        {
            return;
        }
        _shutdown = true;
        await services.ShutdownAsync().ConfigureAwait(false);
        if (_native is not null)
        {
            stopRuntime(_native);
        }
        _native = null;
        _ready = false;
        await inner.ShutdownAsync().ConfigureAwait(false);
    }
}
