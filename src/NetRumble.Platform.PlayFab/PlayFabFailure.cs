using GDK.Net;
using GDK.Net.PlayFab;

namespace NetRumble.Platform.PlayFab;

internal static class PlayFabFailure
{
    internal static bool IsExpected(Exception ex)
        => ex is GameRuntimeException or DllNotFoundException or EntryPointNotFoundException
            or PlatformNotSupportedException or InvalidOperationException
            or OperationCanceledException;

    internal static PlatformResult FromException(Exception ex, string message)
    {
        if (ex is OperationCanceledException)
        {
            return PlatformResult.Canceled();
        }
        if (ex is GameRuntimeException { HResultCode: PlayFabErrors.LobbyNotJoinable or PlayFabErrors.LobbyDoesNotExist })
        {
            return PlatformResult.Fail(PlatformStatus.Failed,
                "This match is full or no longer accepts players.", ex.Message);
        }

        var status = ex switch
        {
            DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
                => PlatformStatus.Unavailable,
            GameRuntimeException { HResultCode: PlayFabErrors.AccountBanned or PlayFabErrors.AccountNotFound }
                => PlatformStatus.NoPrivilege,
            GameRuntimeException { HResultCode: PlayFabErrors.InvalidTitleId or PlayFabErrors.TitleNotActivated }
                => PlatformStatus.Unavailable,
            GameRuntimeException { HResultCode: HResult.EGameUserSignedOut or HResult.EGameUserNoAuthUser
                or HResult.EGameUserUserNotFound or PlayFabErrors.Noentitytoken
                or PlayFabErrors.CoreLocalUserNotLoggedIn } => PlatformStatus.NotSignedIn,
            GameRuntimeException { HResultCode: PlayFabErrors.ApiClientRequestRateLimitExceeded
                or PlayFabErrors.LobbyRateLimitExceeded or PlayFabErrors.ServiceUnavailable
                or PlayFabErrors.DownstreamServiceUnavailable } => PlatformStatus.NetworkFailure,
            GameRuntimeException { HResultCode: unchecked((int)0x80072EE2) or unchecked((int)0x800705B4) }
                => PlatformStatus.TimedOut,
            GameRuntimeException { HResultCode: unchecked((int)0x80072EE7) or unchecked((int)0x80072EFD)
                or unchecked((int)0x80072EFE) or unchecked((int)0x80072F8F) } => PlatformStatus.NetworkFailure,
            _ => PlatformStatus.Failed,
        };
        return PlatformResult.Fail(status, message, ex.Message);
    }

    internal static PlatformResult<T> For<T>(PlatformResult result)
        => result.WasCanceled
            ? PlatformResult<T>.Canceled(result.Message)
            : PlatformResult<T>.Fail(result.Status, result.Message ?? "PlayFab could not complete the request.", result.Diagnostics);
}
