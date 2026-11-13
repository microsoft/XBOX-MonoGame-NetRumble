using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Net.Wire;
using NetRumble.Game;
using NetRumble.Game.Profile;
using NetRumble.Game.UI;
using NetRumble.Game.UI.Elements;
using NetRumble.Game.UI.Screens;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;

if (args.Contains("--native-host-input-check"))
{
    using var check = new NativeHostInputCheck();
    check.Run();
    return check.Passed ? 0 : 1;
}

var failures = 0;
void Check(string label, Action<TestUi> test)
{
    using var ui = new TestUi();
    try
    {
        test(ui);
        Console.WriteLine($"PASS {label}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {label}: {ex.Message}");
    }
}

Check("PC system-keyboard cancellation returns to the main menu without joining", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Canceled());
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("failed system-keyboard entry cannot strand the connecting lobby", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Unavailable("Test keyboard unavailable."));
    ui.Frame();
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("Escape cancels the whole manual join and ignores a late keyboard result", ui =>
{
    ui.OpenManualJoin();
    ui.Frame(keys: new KeyboardState(Keys.Escape));
    ui.AssertMenu();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("abcde"));
    ui.Frame();
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("controller B cancels manual join without opening a leave confirmation", ui =>
{
    ui.OpenManualJoin();
    ui.Frame(pad: new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, Buttons.B));
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("clicking Cancel with a valid code never submits it", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("abcde"));
    ui.Frame();
    ui.Frame(mouse: new MouseState(960, 680, 0, ButtonState.Pressed,
        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("an incomplete code cannot submit and Enter activates Cancel instead", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("abc"));
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("Enter submits a normalized code once and preserves the successful lobby", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("abcde"));
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    TestUi.Require(ui.Party.Codes.SequenceEqual(["ABCDE"]), "Expected exactly one normalized join.");
    TestUi.Require(ui.Screens.Current is LobbyScreen { IsOnline: true }, "Successful lobby was lost.");
    TestUi.Require(ui.Screens.Find<JoinCodeEntry>() is null, "Entry must pop before submitting.");
});

Check("controller A preserves successful code submission", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("ABCDE"));
    ui.Frame();
    ui.Frame(pad: new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, Buttons.A));
    TestUi.Require(ui.Party.Codes.SequenceEqual(["ABCDE"]), "Controller submission did not join.");
    TestUi.Require(ui.Screens.Current is LobbyScreen { IsOnline: true }, "Successful lobby was lost.");
});

Check("clicking OK preserves successful code submission", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("ABCDE"));
    ui.Frame();
    ui.Frame(mouse: new MouseState(960, 610, 0, ButtonState.Pressed,
        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));
    TestUi.Require(ui.Party.Codes.SequenceEqual(["ABCDE"]), "Pointer submission did not join.");
    TestUi.Require(ui.Screens.Current is LobbyScreen { IsOnline: true }, "Successful lobby was lost.");
});

Check("failed manual join returns to menu and permits a fresh intentional code entry", ui =>
{
    ui.Party.JoinResult = PlatformResult.Fail(PlatformStatus.SessionNotJoinable, "Test stale code.");
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("ABCDE"));
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    TestUi.Require(ui.Screens.Current is DialogBox, "Expected the connection failure dialog.");
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.AssertMenu();
    TestUi.Require(ui.Screens.Find<JoinCodeEntry>() is null, "Failure retained code entry.");
    ui.OpenManualJoin();
    TestUi.Require(ui.Screens.Current is JoinCodeEntry, "Fresh manual join did not request a code.");
});

foreach (var failure in new[] { PlatformStatus.SessionNotJoinable, PlatformStatus.NetworkFailure })
{
    Check($"failed invite ({failure}) returns to menu without code entry", ui =>
    {
        ui.Party.JoinResult = PlatformResult.Fail(failure, "Test invite refusal.");
        ui.AcceptInvite();
        TestUi.Require(ui.Screens.Current is DialogBox, "Expected the connection failure dialog.");
        ui.Frame(keys: new KeyboardState(Keys.Enter));
        ui.AssertMenu();
        TestUi.Require(ui.Party.Invites.SequenceEqual(["test-invite"]), "Invite was not attempted once.");
        TestUi.Require(ui.TextRequests == 0, "Invite failure opened code entry.");
        TestUi.Require(ui.Party.Leaves == 1, "Failed attempt was not cleaned up.");
    });
}

Check("join-in-progress refusal after the roster handshake returns to menu", ui =>
{
    ui.AcceptInvite();
    ui.Party.RefuseJoin();
    TestUi.Require(ui.Screens.Current is DialogBox, "Expected the refusal dialog.");
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.AssertMenu();
    TestUi.Require(ui.TextRequests == 0, "Refusal opened code entry.");
});

Check("successful invite bypasses code entry and retains its connected lobby", ui =>
{
    ui.AcceptInvite();
    TestUi.Require(ui.Screens.Current is LobbyScreen { IsOnline: true }, "Invite did not connect.");
    TestUi.Require(ui.TextRequests == 0 && ui.Party.Codes.Count == 0, "Invite requested a manual code.");
});

Check("removed lobby cannot open code entry after its communications check completes", ui =>
{
    var pending = new TaskCompletionSource<PrivilegeVerdict>();
    ui.Communications = pending.Task;
    ui.OpenManualJoin(expectEntry: false);
    ui.Communications = Task.FromResult(PrivilegeVerdict.Allow());
    ui.Party.JoinResult = PlatformResult.Fail(PlatformStatus.SessionNotJoinable, "Test match in progress.");
    ui.AcceptInvite();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.AssertMenu();
    pending.SetResult(PrivilegeVerdict.Allow());
    ui.Frame();
    ui.AssertMenu();
    TestUi.Require(ui.TextRequests == 0, "An obsolete manual lobby opened code entry.");
});

Check("removed menu cannot restart manual join after an invite fails", ui =>
{
    var pending = new TaskCompletionSource<PrivilegeVerdict>();
    ui.Multiplayer = pending.Task;
    ui.Screens.Current!.HandleAction(ui.Context, MenuAction.Down);
    ui.Screens.Current!.HandleAction(ui.Context, MenuAction.Accept);
    ui.Multiplayer = Task.FromResult(PrivilegeVerdict.Allow());
    ui.Party.JoinResult = PlatformResult.Fail(PlatformStatus.SessionNotJoinable, "Test match in progress.");
    ui.AcceptInvite();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    ui.AssertMenu();
    pending.SetResult(PrivilegeVerdict.Allow());
    ui.Frame();
    ui.AssertMenu();
    TestUi.Require(ui.TextRequests == 0, "An obsolete menu restarted manual join.");
});

Check("removed invite cannot report failure over a new manual join", ui =>
{
    var pending = new TaskCompletionSource<PlatformResult>();
    ui.Party.PendingJoin = pending.Task;
    ui.AcceptInvite();
    ui.Screens.ReplaceAll(new MainMenuScreen());
    ui.OpenManualJoin();
    var entry = ui.Screens.Current;
    pending.SetResult(PlatformResult.Fail(PlatformStatus.SessionNotJoinable, "Test late failure."));
    TestUi.Require(ReferenceEquals(ui.Screens.Current, entry), "Old invite changed the active join.");
    TestUi.Require(ui.Party.Leaves == 0, "Old invite tore down a newer attempt.");
});

Check("removed invite cannot bind a successful result to a newer screen", ui =>
{
    var pending = new TaskCompletionSource<PlatformResult>();
    ui.Party.PendingJoin = pending.Task;
    ui.AcceptInvite();
    var abandoned = (LobbyScreen)ui.Screens.Current!;
    ui.Screens.ReplaceAll(new MainMenuScreen());
    pending.SetResult(PlatformResult.Ok());
    ui.AssertMenu();
    TestUi.Require(!abandoned.IsOnline, "An exited lobby bound the obsolete network result.");
});

Check("removed code entry ignores a late result without popping the replacement screen", ui =>
{
    ui.OpenManualJoin();
    var entry = ui.Screens.Current!;
    ui.Screens.ReplaceAll(new MainMenuScreen());
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("ABCDE"));
    entry.Update(ui.Context);
    entry.HandleAction(ui.Context, MenuAction.Accept);
    entry.HandleAction(ui.Context, MenuAction.Back);
    ui.AssertMenu();
    ui.AssertNoJoin();
});

Check("PC Space readies the connected lobby through screen dispatch", ui =>
{
    ui.AcceptInvite();
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.Party.ReadyValues.SequenceEqual([true]), "Space did not publish ready=true.");
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.Party.ReadyValues.Count == 1, "Held Space repeated ready.");
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.Party.ReadyValues.SequenceEqual([true, false]), "Second Space did not unready.");
});

Check("PC Space readies a hosted lobby without starting a solo online match", ui =>
{
    ui.OpenHost();
    var lobby = (LobbyScreen)ui.Screens.Current!;
    var network = ui.LobbyNetwork;
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(network.LocalPlayer!.IsReady, "Hosted local player did not ready.");
    var roster = (MenuList)typeof(LobbyScreen).GetField("_list",
        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lobby)!;
    var displayedPlayer = (PlayerState)typeof(RosterRow).GetField("_state",
        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(roster.Rows[0])!;
    TestUi.Require(ReferenceEquals(displayedPlayer, network.LocalPlayer) && displayedPlayer.IsReady,
        "Displayed host roster row did not reflect readiness.");
    TestUi.Require(ReferenceEquals(ui.Screens.Current, lobby), "Solo host started without another player.");
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(!network.LocalPlayer!.IsReady, "Hosted local player did not unready.");
});

foreach (var zone in new[] { "ShipTabs", "ColorTabs", "Roster" })
{
    Check($"host Space takes priority over Accept with {zone} focus", ui =>
    {
        ui.OpenHost();
        var lobby = ui.Screens.Current!;
        var focus = typeof(LobbyScreen).GetField("_focusZone", BindingFlags.NonPublic | BindingFlags.Instance)!;
        focus.SetValue(lobby, Enum.Parse(focus.FieldType, zone));
        if (zone == "Roster")
        {
            var list = (MenuList)typeof(LobbyScreen).GetField("_list",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lobby)!;
            var row = (RosterRow)list.Rows[0];
            row.SetActionsContext(true, false);
            list.FocusFirst();
        }
        var sent = ui.Party.SentTypes.Count;
        ui.Frame(keys: new KeyboardState(Keys.Space));
        TestUi.Require(ui.LobbyNetwork.LocalPlayer!.IsReady, "Space was consumed by the focused widget.");
        TestUi.Require(ReferenceEquals(ui.Screens.Current, lobby), "Space activated a roster action.");
        TestUi.Require(ui.Party.SentTypes.Skip(sent).All(type => type == MessageType.RosterSnapshot),
            "Space activated customization instead of only readying.");
    });
}

Check("host Space still readies when Enter is already held", ui =>
{
    ui.OpenHost();
    ui.Frame(keys: new KeyboardState(Keys.Enter));
    TestUi.Require(!ui.LobbyNetwork.LocalPlayer!.IsReady, "Enter must not ready.");
    ui.Frame(keys: new KeyboardState(Keys.Enter, Keys.Space));
    TestUi.Require(ui.LobbyNetwork.LocalPlayer!.IsReady, "Held Accept prevented the Ready edge.");
});

Check("controller X still readies and keyboard T still toggles lobby voice mute", ui =>
{
    ui.OpenHost();
    ui.Frame(pad: new GamePadState(Vector2.Zero, Vector2.Zero, 0, 0, Buttons.X));
    TestUi.Require(ui.LobbyNetwork.LocalPlayer!.IsReady, "Controller X did not ready.");
    ui.Frame(keys: new KeyboardState(Keys.T));
    TestUi.Require(ui.Party.IsSelfMuted, "Keyboard voice hotkey was lost.");
});

Check("Space dismissing a modal does not ready the host underneath", ui =>
{
    ui.OpenHost();
    ui.Screens.ShowDialog("Test modal", "Space should accept this dialog, not ready the lobby.");
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.Screens.Current is LobbyScreen, "Space no longer accepts dialogs.");
    TestUi.Require(!ui.LobbyNetwork.LocalPlayer!.IsReady, "Modal dismissal leaked Space to the host.");
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(!ui.LobbyNetwork.LocalPlayer!.IsReady, "Held dismissal leaked on the next frame.");
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.LobbyNetwork.LocalPlayer!.IsReady, "Fresh Space failed after modal dismissal.");
});

Check("Space submitting code entry does not also ready the resulting lobby", ui =>
{
    ui.OpenManualJoin();
    ui.TextEntry.SetResult(PlatformResult<string>.Ok("ABCDE"));
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.Party.Codes.SequenceEqual(["ABCDE"]), "Space no longer accepts code entry.");
    TestUi.Require(!ui.LobbyNetwork.LocalPlayer!.IsReady, "Code submission leaked into ready-up.");
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(!ui.LobbyNetwork.LocalPlayer!.IsReady, "Held code submission readied on the next frame.");
    ui.Frame();
    ui.Frame(keys: new KeyboardState(Keys.Space));
    TestUi.Require(ui.LobbyNetwork.LocalPlayer!.IsReady, "Fresh Space failed after code entry.");
});

Console.WriteLine($"{(failures == 0 ? "PASS" : "FAIL")}: {failures} failed join-flow checks.");
return failures == 0 ? 0 : 1;

sealed class TestUi : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netrumble-join-{Guid.NewGuid():N}");
    internal ScreenManager Screens { get; } = new();
    internal UiContext Context { get; }
    internal TestParty Party { get; } = new();
    internal TaskCompletionSource<PlatformResult<string>> TextEntry { get; } = new();
    internal Task<PrivilegeVerdict> Multiplayer { get; set; } = Task.FromResult(PrivilegeVerdict.Allow());
    internal Task<PrivilegeVerdict> Communications { get; set; } = Task.FromResult(PrivilegeVerdict.Allow());
    internal int TextRequests { get; private set; }
    private Action<string>? _inviteAccepted;

    internal TestUi()
    {
        var runtime = Stub<IPlatformRuntime>.Create((name, _) => name switch
        {
            "get_IsOnline" => true,
            "get_DeviceKind" => PlatformDeviceKind.Desktop,
            _ => throw new NotSupportedException(name),
        });
        var identity = Stub<IIdentityService>.Create((name, _) => name switch
        {
            "get_CurrentUser" => new PlatformUser { LocalId = "test", DisplayName = "Test" },
            "get_IsSignInInProgress" => false,
            "add_UserChanged" => null,
            _ => throw new NotSupportedException(name),
        });
        var privileges = Stub<IPrivilegeService>.Create((name, args) => name switch
        {
            "EnsureAsync" => Multiplayer,
            "CheckAsync" when (GamePrivilege)args[0]! == GamePrivilege.Communications => Communications,
            _ => throw new NotSupportedException(name),
        });
        var keyboard = Stub<IPlatformUiService>.Create((name, _) =>
        {
            if (name == "get_RequiresVirtualKeyboard") { return true; }
            if (name != "ShowTextEntryAsync") { throw new NotSupportedException(name); }
            TextRequests++;
            return TextEntry.Task;
        });
        var activity = Stub<IActivityService>.Create((name, args) =>
        {
            if (name == "DeleteActivityAsync") { return Task.FromResult(PlatformResult.Ok()); }
            if (name != "add_InviteAccepted") { throw new NotSupportedException(name); }
            _inviteAccepted += (Action<string>)args[0]!;
            return null;
        });
        var privacy = Stub<IPrivacyService>.Create((name, _) => name switch
        {
            "RefreshListsAsync" => Task.FromResult(PlatformResult.Ok()),
            _ => throw new NotSupportedException(name),
        });
        var platform = Stub<IPlatformProvider>.Create((name, _) => name switch
        {
            "get_Runtime" => runtime,
            "get_Identity" => identity,
            "get_Privileges" => privileges,
            "get_PlatformUi" => keyboard,
            "get_Party" => Party,
            "get_Activity" => activity,
            "get_Privacy" => privacy,
            "get_Capabilities" => PlatformCapabilities.None,
            _ => throw new NotSupportedException(name),
        });

        // Only font metrics are used. No graphics device, window, native UI or live service is created.
        var characters = Enumerable.Range(32, 95).Select(i => (char)i).ToList();
        var font = new SpriteFont(null!, characters.Select(_ => new Rectangle(0, 0, 8, 16)).ToList(),
            characters.Select(_ => new Rectangle(0, 0, 8, 16)).ToList(), characters,
            16, 0, characters.Select(_ => new Vector3(0, 8, 0)).ToList(), '?');
        Context = new UiContext
        {
            Screens = Screens, Platform = platform, Input = new UiInput(), Profile = new PlayerProfile(_root),
            Theme = new UiTheme(null!, font, font, font, font, font, font, font),
            Batch = null!, Assets = null!, Audio = null!, Activity = new ActivityCoordinator(platform), Pictures = null!,
            Achievements = null!, CloudSettings = null!, MatchHistory = null!, Pixel = null!,
            Game = null!, Random = new Random(0),
        };
        Screens.Context = Context;
        Screens.ScreenPushed += screen =>
        {
            var field = screen.GetType().GetField(screen is MainMenuScreen ? "_list" : "_buttons",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(screen) is MenuList list)
            {
                foreach (var button in list.Rows.OfType<Button>()) { button.SoundsEnabled = false; }
            }
        };
        Screens.Push(new MainMenuScreen());
        _ = new InviteRouter(Context);
    }

    internal void OpenManualJoin(bool expectEntry = true)
    {
        Screens.Push(new LobbyScreen(LobbyIntent.Join));
        Require(!expectEntry || Screens.Current is JoinCodeEntry, "Manual join did not request a code.");
    }

    internal void AcceptInvite() => _inviteAccepted!("test-invite");

    internal void OpenHost()
    {
        Party.Hosting = true;
        Screens.Push(new LobbyScreen(LobbyIntent.Host));
        Require(Screens.Current is LobbyScreen { IsOnline: true }, "Host did not connect.");
    }

    internal IMatchNetwork LobbyNetwork => (IMatchNetwork)typeof(LobbyScreen).GetField("_network",
        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Screens.Current)!;

    internal void Frame(KeyboardState keys = default, GamePadState pad = default, MouseState mouse = default)
    {
        Context.Input.Update(keys, pad, mouse, 1f / 60f);
        Screens.Update(Context);
    }

    internal void AssertMenu()
        => Require(Screens.Current is MainMenuScreen && Screens.StackSize == 1,
            $"Expected only the main menu, got {Screens.Current?.GetType().Name} ({Screens.StackSize} screens).");

    internal void AssertNoJoin()
        => Require(Party.Codes.Count == 0 && Party.Invites.Count == 0, "Cancellation started a join.");

    internal static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    public void Dispose()
    {
        Screens.Clear();
        Directory.Delete(_root, recursive: true);
    }
}

public class Stub<T> : DispatchProxy where T : class
{
    private Func<string, object?[], object?> _invoke = null!;
    internal static T Create(Func<string, object?[], object?> invoke)
    {
        var result = Create<T, Stub<T>>();
        ((Stub<T>)(object)result)._invoke = invoke;
        return result;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => _invoke(targetMethod!.Name, args ?? []);
}

sealed class TestParty : IPartyService
{
    internal List<bool> ReadyValues { get; } = [];
    internal List<MessageType> SentTypes { get; } = [];
    internal List<string> Codes { get; } = [];
    internal List<string> Invites { get; } = [];
    internal int Leaves { get; private set; }
    internal PlatformResult JoinResult { get; set; } = PlatformResult.Ok();
    internal Task<PlatformResult>? PendingJoin { get; set; }
    public bool HasNetwork => true;
    internal bool Hosting { get; set; }
    public bool IsHost => Hosting;
    public int LocalPeerId => Hosting ? 1 : 2;
    public string ConnectionString => "test-invite";
    public string JoinCode => "ABCDE";
    public bool ChatAllowed { get; set; }
    public bool IsSelfMuted { get; set; }
    public event Action<PlatformResult>? NetworkDestroyed { add { } remove { } }
    public event Action<int>? PeerJoined { add { } remove { } }
    public event Action<int>? PeerLeft { add { } remove { } }
    public event PartyMessageHandler? MessageReceived;
    public event Action? ChatChanged { add { } remove { } }
    public event Action<int, string, ChatTextKind>? ChatTextReceived { add { } remove { } }
    public Task<PlatformResult<string>> HostAsync(int maxPlayers, string gameMode, string protocolVersion,
        CancellationToken cancellationToken = default) => Task.FromResult(PlatformResult<string>.Ok("ABCDE"));
    public Task<PlatformResult> JoinAsync(string joinCode, string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        Codes.Add(joinCode);
        return PendingJoin ?? Task.FromResult(JoinResult);
    }
    public Task<PlatformResult> JoinByConnectionStringAsync(string connectionString, string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        Invites.Add(connectionString);
        return PendingJoin ?? Task.FromResult(JoinResult);
    }
    public Task LeaveAsync() { Leaves++; return Task.CompletedTask; }
    internal void RefuseJoin()
    {
        using var refusal = new MessageWriter(MessageType.JoinRefused, 4);
        MessageReceived?.Invoke(IPartyService.HostPeerId, refusal.Written);
    }
    public void Send(int peerId, ReadOnlySpan<byte> payload, MessageDelivery delivery)
    {
        var reader = new MessageReader(payload);
        if (!reader.TryReadMessageType(out var type)) { throw new InvalidOperationException("Missing packet type."); }
        SentTypes.Add(type);
        if (type == MessageType.SubmitReady && reader.TryReadBool(out var ready))
        {
            ReadyValues.Add(ready);
        }
    }
    public void SetPeerRestrictions(int peerId, bool allowVoice, bool allowText) { }
    public void SetPeerMuted(int peerId, bool muted) { }
    public bool IsPeerMuted(int peerId) => false;
    public ChatIndicator GetChatIndicator(int peerId) => ChatIndicator.None;
    public void ClearChatRestrictions() { }
    public PlatformResult SendChatText(string message) => throw new NotSupportedException();
}
