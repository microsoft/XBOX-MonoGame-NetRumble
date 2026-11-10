using NetRumble.Game;
using NetRumble.Game.UI;
using NetRumble.Game.UI.Screens;
using NetRumble.Platform;

internal static class PresenceChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var identity = new Identity();
        var activity = new Activity();
        var provider = new Provider(identity, activity);
        var screens = new ScreenManager();
        var context = new UiContext
        {
            Platform = provider, Screens = screens, Input = new UiInput(),
            Batch = null!, Theme = null!, Assets = null!, Audio = null!,
            Activity = null!, Pictures = null!, Profile = null!, Achievements = null!,
            CloudSettings = null!, MatchHistory = null!, Pixel = null!, Game = null!,
            Random = new Random(0)
        };
        screens.Context = context;
        var publisher = new PresencePublisher(context);

        check("presence starts one request", activity.Calls.Count == 1);
        activity.Complete(PlatformResult.Fail(PlatformStatus.NetworkFailure, "test failure"));
        await WaitUntilAsync(() => !InFlight(publisher));
        check("failed presence does not spin retrying", activity.Calls.Count == 1);
        identity.Change("first");
        check("same-account UserChanged retries a failed presence", activity.Calls.Count == 2);
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => !InFlight(publisher));
        identity.Change("first");
        screens.Push(new Popup());
        screens.Pop();
        check("successful presence suppresses duplicate identity and screen events", activity.Calls.Count == 2);

        identity.Change("second");
        identity.Change("third");
        identity.Change("fourth");
        check("overlapping presence writes remain single flight", activity.Calls.Count == 3);
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => activity.Calls.Count == 4);
        check("overlapping writes coalesce to the latest account",
            activity.Calls[3] == ("fourth", PresenceStrings.InMenu));
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => !InFlight(publisher));
        identity.Change("fourth");
        check("coalesced success is deduplicated", activity.Calls.Count == 4);

        identity.Change("fifth");
        identity.Change("fifth");
        activity.Complete(PlatformResult.Fail(PlatformStatus.NetworkFailure, "test failure"));
        await WaitUntilAsync(() => activity.Calls.Count == 6);
        check("an event arriving during a failed send gets one coalesced retry", activity.Calls.Count == 6);
        activity.Complete(PlatformResult.Fail(PlatformStatus.NetworkFailure, "test retry failure"));
        await WaitUntilAsync(() => !InFlight(publisher));
        check("a failed coalesced retry stops without a new event", activity.Calls.Count == 6);
        identity.Change("fifth");
        activity.Throw(new InvalidOperationException("test exception"));
        await WaitUntilAsync(() => !InFlight(publisher));
        identity.Change("fifth");
        check("exceptions also allow an event-driven retry", activity.Calls.Count == 8);

        identity.Change(null);
        identity.Change("fifth");
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => activity.Calls.Count == 9);
        check("old-session completion cannot suppress same-account re-sign-in", activity.Calls.Count == 9);
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => !InFlight(publisher));
        identity.Change(null);
        check("signed-out presence sends nothing", activity.Calls.Count == 9);
        identity.Change("fifth");
        check("sign-in republishes the current presence", activity.Calls.Count == 10);
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => !InFlight(publisher));

        identity.Change("sixth");
        var stack = (List<Screen>)typeof(ScreenManager).GetField("_stack",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(screens)!;
        stack.Add(new LobbyScreen(LobbyIntent.Practice));
        identity.Change("sixth");
        stack.Clear();
        identity.Change("sixth");
        stack.Add(new LobbyScreen(LobbyIntent.Practice));
        identity.Change("sixth");
        check("screen changes cannot overlap a pending presence write", activity.Calls.Count == 11);
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => activity.Calls.Count == 12);
        check("screen changes publish only the latest presence id",
            activity.Calls[11] == ("sixth", PresenceStrings.InLobby));
        activity.Complete(PlatformResult.Ok());
        await WaitUntilAsync(() => !InFlight(publisher));
        stack.Clear();
    }

    // Inspect private completion state to synchronize without production test hooks.
    private static bool InFlight(PresencePublisher publisher)
        => (bool)typeof(PresencePublisher).GetField("_inFlight",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(publisher)!;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(1, timeout.Token); }
    }

    private sealed class Popup : Screen
    {
        public override void Draw(UiContext context) { }
    }

    private sealed class Identity : IIdentityService
    {
        public PlatformUser? CurrentUser { get; private set; }
            = new() { LocalId = "first", DisplayName = "Test" };
        public string CurrentStage => string.Empty;
        public event Action<PlatformUser?>? UserChanged;
        public event Action<PlatformAccountChange>? AccountChanged { add { } remove { } }
        public event Action? ControllerAssociationChanged { add { } remove { } }
        public event Action<string>? SignInStageChanged { add { } remove { } }
        public void Change(string? account)
        {
            CurrentUser = account is null ? null : new() { LocalId = account, DisplayName = "Test" };
            UserChanged?.Invoke(CurrentUser);
        }
        public Task<PlatformResult<PlatformUser>> SignInAsync(
            SignInOptions options = default, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SignOutAsync() => throw new NotSupportedException();
    }

    private sealed class Activity : IActivityService
    {
        private readonly object _gate = new();
        private TaskCompletionSource<PlatformResult>? _pending;
        private readonly List<(string Account, string Id)> _calls = [];
        public Identity Identity { get; set; } = null!;
        public IReadOnlyList<(string Account, string Id)> Calls { get { lock (_gate) { return _calls.ToArray(); } } }
        public event Action<string>? InviteAccepted { add { } remove { } }
        public Task<PlatformResult> SetPresenceAsync(string status, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_pending is not null) { throw new InvalidOperationException("Concurrent presence write"); }
                _calls.Add((Identity.CurrentUser!.LocalId, status));
                _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }
        public void Complete(PlatformResult result) => TakePending().SetResult(result);
        public void Throw(Exception exception) => TakePending().SetException(exception);
        private TaskCompletionSource<PlatformResult> TakePending()
        {
            lock (_gate)
            {
                var pending = _pending ?? throw new InvalidOperationException("No pending presence write");
                _pending = null;
                return pending;
            }
        }
        public Task<PlatformResult> SetActivityAsync(string connectionString, int maxPlayers,
            int currentPlayers, string groupId = "", CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformResult> DeleteActivityAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformResult> ShowInviteUiAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformResult<IReadOnlyDictionary<string, string>>> GetJoinableActivitiesAsync(
            IReadOnlyList<string> xboxUserIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<PlatformResult> ReportRecentPlayersAsync(IReadOnlyList<string> xboxUserIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlatformResult> FlushRecentPlayersAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class Provider : IPlatformProvider
    {
        public Provider(Identity identity, Activity activity)
        {
            Identity = identity;
            Activity = activity;
            activity.Identity = identity;
        }
        public string Name => "Presence test";
        public PlatformCapabilities Capabilities => PlatformCapabilities.Presence;
        public IIdentityService Identity { get; }
        public IActivityService Activity { get; }
        public IPlatformRuntime Runtime => throw new NotSupportedException();
        public IPartyService Party => throw new NotSupportedException();
        public IPrivilegeService Privileges => throw new NotSupportedException();
        public IPrivacyService Privacy => throw new NotSupportedException();
        public IModerationService Moderation => throw new NotSupportedException();
        public ISocialService Social => throw new NotSupportedException();
        public IAchievementService Achievements => throw new NotSupportedException();
        public IGameSaveService GameSaves => throw new NotSupportedException();
        public IGameInputService GameInput => throw new NotSupportedException();
        public IPlatformUiService PlatformUi => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
