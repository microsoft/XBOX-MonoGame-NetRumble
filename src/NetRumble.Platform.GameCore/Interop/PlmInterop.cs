using System.Runtime.InteropServices;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.GameCore.Interop;

/// <summary>
/// The GDK's Process Lifetime Management (PLM) registrations: suspend/resume and
/// constrain/unconstrain, bound by hand because GDK.Net does not wrap them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is hand-written.</b> GDK.Net exposes <c>GameRuntime</c> and the subsystem
/// managers, but nothing for PLM - there is no lifecycle type in the assembly at all.
/// The four entry points here are part of the same flat API GDK.Net binds to, exported
/// from the same <c>xgameruntime.thunks.dll</c>, so they are reachable the same way
/// <see cref="GameRuntimeProbe"/> reaches <c>XGameRuntimeInitialize</c>.
/// </para>
/// <para>
/// <b>Bound lazily, by export, and allowed to be absent.</b> PLM is a console concept.
/// A desktop GDK may export these and never deliver a notification, and a partial SDK
/// may not export them at all. Neither case may crash the game, so the delegates are
/// resolved through <see cref="NativeLibrary.TryGetExport"/> and a missing export
/// degrades to "no lifecycle events", exactly as an absent GDK degrades to offline.
/// </para>
/// <para>
/// <b>The callbacks arrive on an OS thread, not the pump.</b> Everything here does is
/// hand the notification to a caller-supplied delegate; marshalling it onto the pump
/// thread is <c>GameCoreRuntime</c>'s job, because the provider's threading contract
/// says every event the game sees surfaces from <c>Pump</c>.
/// </para>
/// <para>
/// The delegates are held in static fields for the process lifetime. A collected
/// delegate would leave the runtime calling a freed thunk, which is a crash that happens
/// only on a console, only under memory pressure, and only when the Guide opens.
/// </para>
/// </remarks>
internal static class PlmInterop
{
    /// <summary>Raised with true when the title is being suspended, false on resume.</summary>
    internal delegate void AppStateChangeCallback(bool quiesced);

    /// <summary>Raised with true when the title is constrained, false when it returns.</summary>
    internal delegate void ConstrainChangeCallback(bool constrained);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeStateChangeRoutine(byte quiesced, nint context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NativeConstrainRoutine(byte constrained, nint context);

    private delegate int RegisterStateChange(NativeStateChangeRoutine callback, nint context, out nint token);

    private delegate int RegisterConstrainChange(NativeConstrainRoutine callback, nint context, out nint token);

    // Held for the process lifetime: the runtime keeps native pointers to these.
    private static NativeStateChangeRoutine? _stateRoutine;
    private static NativeConstrainRoutine? _constrainRoutine;
    private static AppStateChangeCallback? _stateCallback;
    private static ConstrainChangeCallback? _constrainCallback;

    private static nint _stateToken;
    private static nint _constrainToken;

    /// <summary>True once <see cref="TryRegister"/> has bound at least one notification.</summary>
    internal static bool IsRegistered { get; private set; }

    /// <summary>Why PLM is unavailable, for logs. Empty when it registered.</summary>
    internal static string UnavailableReason { get; private set; } = string.Empty;

    /// <summary>
    /// Binds both notifications. Returns false, with a reason, when the runtime does not
    /// offer them - which is the ordinary case on a desktop dev box.
    /// </summary>
    internal static bool TryRegister(
        AppStateChangeCallback onAppStateChanged,
        ConstrainChangeCallback onConstrainChanged)
    {
        if (IsRegistered)
        {
            return true;
        }

        if (!GameRuntimeProbe.IsAvailable)
        {
            UnavailableReason = GameRuntimeProbe.UnavailableReason;
            return false;
        }

        if (!NativeLibrary.TryLoad(GameRuntimeProbe.Library, out var handle))
        {
            UnavailableReason = $"'{GameRuntimeProbe.Library}' could not be loaded for PLM registration.";
            return false;
        }

        if (!NativeLibrary.TryGetExport(handle, "RegisterAppStateChangeNotification", out var stateExport)
            || !NativeLibrary.TryGetExport(handle, "RegisterAppConstrainedChangeNotification", out var constrainExport))
        {
            UnavailableReason =
                $"'{GameRuntimeProbe.Library}' does not export the PLM registration entry points. " +
                "This is expected on a desktop GDK, which delivers no lifecycle notifications.";
            return false;
        }

        _stateCallback = onAppStateChanged;
        _constrainCallback = onConstrainChanged;

        // Trampolines rather than the caller's delegate directly: the native signature
        // carries a context pointer and a BOOLEAN, neither of which belongs in the
        // provider-facing contract.
        //
        // Both are exception-guarded. The caller is native GDK code with no managed frame
        // above it, so an escaping exception does not unwind to a handler - it tears the
        // process down inside a suspend, which is the one moment certification always
        // exercises. A lifecycle notification that fails is worth a breadcrumb and
        // nothing more.
        _stateRoutine = static (quiesced, _) => Guard(
            "plm: app-state callback threw",
            () => _stateCallback?.Invoke(quiesced != 0));

        _constrainRoutine = static (constrained, _) => Guard(
            "plm: constrain callback threw",
            () => _constrainCallback?.Invoke(constrained != 0));

        try
        {
            var registerState = Marshal.GetDelegateForFunctionPointer<RegisterStateChange>(stateExport);
            var registerConstrain = Marshal.GetDelegateForFunctionPointer<RegisterConstrainChange>(constrainExport);

            var stateResult = registerState(_stateRoutine, nint.Zero, out _stateToken);
            var constrainResult = registerConstrain(_constrainRoutine, nint.Zero, out _constrainToken);

            if (stateResult < 0 || constrainResult < 0)
            {
                UnavailableReason =
                    $"PLM registration failed (state 0x{stateResult:X8}, constrain 0x{constrainResult:X8}).";
                Reset();
                return false;
            }
        }
        catch (Exception ex)
        {
            // Binding a flat-API entry point that exists but has drifted must degrade,
            // never crash - the same rule the rest of this provider follows.
            UnavailableReason = $"PLM registration threw: {ex.Message}";
            Reset();
            return false;
        }

        IsRegistered = true;
        UnavailableReason = string.Empty;
        return true;
    }

    /// <summary>Releases both registrations. Safe when never registered.</summary>
    internal static void Unregister()
    {
        if (!IsRegistered)
        {
            return;
        }

        if (NativeLibrary.TryLoad(GameRuntimeProbe.Library, out var handle))
        {
            TryUnregister(handle, "UnregisterAppStateChangeNotification", _stateToken);
            TryUnregister(handle, "UnregisterAppConstrainedChangeNotification", _constrainToken);
        }

        Reset();
        IsRegistered = false;
    }

    private static void TryUnregister(nint handle, string export, nint token)
    {
        if (token == nint.Zero || !NativeLibrary.TryGetExport(handle, export, out var address))
        {
            return;
        }

        try
        {
            Marshal.GetDelegateForFunctionPointer<Action<nint>>(address)(token);
        }
        catch (Exception)
        {
            // Shutdown path. A failed unregister cannot be acted on and must not throw.
        }
    }

    private static void Guard(string context, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            try
            {
                CrashLog.Fatal(context, ex);
            }
            catch (Exception)
            {
                // Logging the failure must not become the failure.
            }
        }
    }

    private static void Reset()
    {
        _stateRoutine = null;
        _constrainRoutine = null;
        _stateCallback = null;
        _constrainCallback = null;
        _stateToken = nint.Zero;
        _constrainToken = nint.Zero;
    }
}
