using Microsoft.Xna.Framework;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/screens/main_menu_screen.gd</c>, itself
/// <c>Game/Screens/MainMenuScreen</c>: the Xbox and NetRumble logos, the signed-in
/// gamertag, and a vertical menu list drawn straight over the background with no panel
/// - unlike every <see cref="MenuScreen"/>-derived screen, this one is <see cref="Screen"/>
/// directly, because the C++ screen never set a button background texture for its rows
/// and the theme kept that inconsistency (<c>NRTitleButton</c> vs <c>NRMenuButton</c>).
/// </summary>
/// <remarks>
/// <para>
/// Sign-in itself belongs to <see cref="AcquireUserScreen"/>, which normally runs before
/// this screen is ever shown; this screen only reflects the identity that leaves behind
/// and offers a "Sign In" row back to it for a player who chose "Continue Offline".
/// </para>
/// <para>
/// <b>Divergence from the source's menu shape.</b> The GDScript's Join row opened an
/// in-place submenu (Join Friend / Lobby Code / Back) and Options replaced the rows in
/// place rather than pushing a screen, because both were built from the same
/// <c>NRMenuList</c> the top-level menu already owned. This port routes Join and
/// Practice straight to <c>LobbyScreen</c> with an intent, and Options to its own
/// pushed <see cref="OptionsScreen"/>, per this port's screen contract - so the friend
/// list and lobby-code sub-flows are the lobby screen's job now, not this one's, and
/// there is no in-place options submenu to maintain here.
/// </para>
/// </remarks>
public sealed class MainMenuScreen : Screen
{
    /// <summary>
    /// The logo, gamertag and menu rects, straight from <c>main_menu_screen.tscn</c> -
    /// authored against the 1920x1080 design canvas, so these are absolute rather than
    /// derived from panel geometry.
    /// </summary>
    private static readonly Rectangle XboxLogoRect = new(750, 128, 420, 150);

    private static readonly Rectangle NetRumbleLogoRect = new(266, 284, 1380, 239);

    private static readonly Rectangle MenuRect = new(768, 540, 384, 486);

    private static readonly string BuildNumber =
        typeof(NetRumbleGame).Assembly.GetName().Version?.ToString() ?? "unknown";

    private readonly MenuList _list = new() { BoxedRows = false, RowSeparation = 0 };

    /// <summary>
    /// Whether the menu currently carries the "Sign In" row, so it is rebuilt only when
    /// the identity state actually crosses that boundary rather than on every unrelated
    /// profile change - a submenu mid-edit rebuilding out from under the player would
    /// drop unsaved state.
    /// </summary>
    private bool _signInRowShown;

    private UiContext? _enteredContext;

    /// <summary>The top-level menu never dismisses via Back; it is the root screen.</summary>
    public MainMenuScreen() => AllowBack = false;

    public override void Enter(UiContext context)
    {
        _enteredContext = context;
        context.Profile.IdentityChanged += OnIdentityChanged;
        BuildMenu(context);
    }

    public override void Exit()
    {
        if (_enteredContext is { } context)
        {
            context.Profile.IdentityChanged -= OnIdentityChanged;
        }

        base.Exit();
    }

    public override void OnRevealed()
    {
        base.OnRevealed();

        // Reached when the acquire-user screen (or any other pushed screen) is popped;
        // the identity may well have just changed, and nothing else refreshes it.
        if (_enteredContext is { } context)
        {
            RefreshSignInRow(context);
        }

        if (_list.Focused is null)
        {
            _list.FocusFirst();
        }
    }

    public override void Update(UiContext context) => _list.Update(context);

    public override bool HandleAction(UiContext context, MenuAction action)
        => _list.HandleAction(context, action) || base.HandleAction(context, action);

    public override void Draw(UiContext context)
    {
        // main_menu_screen.tscn's BackgroundFill. The device clear is the starfield's
        // near-black blue, which is the gameplay backdrop, not the menu one.
        context.Theme.Fill(context.Batch, context.Screen, UiTheme.ScreenBackground);

        UiTheme.TextureFit(
            context.Batch, context.Assets.Texture("Logo_Xbox"), XboxLogoRect, Color.White);
        UiTheme.TextureFit(
            context.Batch, context.Assets.Texture("Logo_NetRumble"), NetRumbleLogoRect, Color.White);

        var gamertag = context.Profile.IsSignedIn ? context.Profile.DisplayName : "Not signed in";
        UiTheme.TextLeft(
            context.Batch, context.Theme.Body, gamertag, new Vector2(192, 126), UiTheme.Text);

        UiTheme.TextRight(
            context.Batch,
            context.Theme.Small,
            $"Build {BuildNumber}",
            new Vector2(context.Screen.Right - 24, context.Screen.Bottom - 24),
            UiTheme.TextDisabled);

        _list.Draw(context);
    }

    /// <summary>
    /// Rebuilds the rows from the current identity, adding or dropping the "Sign In"
    /// row, matching <c>_build_menu</c> in the source.
    /// </summary>
    private void BuildMenu(UiContext context)
    {
        _signInRowShown = !context.Profile.IsSignedIn;
        _list.Clear();
        _list.Bounds = MenuRect;

        // Sign-in normally completes on the acquire-user screen before this menu is
        // ever shown, so this row only appears when the player chose to continue
        // offline there.
        if (_signInRowShown)
        {
            _list.AddButton("Sign In", () => context.Screens.Push(new AcquireUserScreen()));
        }

        _list.AddButton("Host Match", () => OnHostSelected(context));
        _list.AddButton("Join Match", () => OnJoinSelected(context));
        _list.AddButton("Practice", () => context.Screens.Push(new LobbyScreen(LobbyIntent.Practice)));
        _list.AddButton("Match History", () => context.Screens.Push(new MatchHistoryScreen()));
        _list.AddButton("Controls", () => context.Screens.Push(new ControlsScreen()));
        _list.AddButton("Options", () => context.Screens.Push(new OptionsScreen()));

        // Desktop only. On console the platform owns leaving the title, through the
        // Guide, and a second route out that behaves differently is exactly what a
        // functional pass flags. The console menu is simply one row shorter.
        if (context.Platform.Runtime.DeviceKind == PlatformDeviceKind.Desktop)
        {
            _list.AddButton("Quit", () => OnQuit(context));
        }

        _list.FocusFirst();
    }

    private void RefreshSignInRow(UiContext context)
    {
        if (_signInRowShown == context.Profile.IsSignedIn)
        {
            BuildMenu(context);
        }
    }

    private void OnIdentityChanged() => RefreshSignInRow(_enteredContext!);

    /// <summary>
    /// Hosting takes a round trip through the platform's networking layer, and an
    /// account already known to lack the multiplayer privilege is refused up front
    /// rather than after that round trip (XR-045), exactly as the source's
    /// <c>_multiplayer_denial</c> check did before <c>NetManager.host_match</c>.
    /// </summary>
    private static async void OnHostSelected(UiContext context)
    {
        var denial = await MultiplayerDenialAsync(context);

        if (!IsStillOnTop(context))
        {
            return;
        }

        if (denial is { Length: > 0 })
        {
            OfferPractice(context, "Cannot Host", denial);
            return;
        }

        context.Screens.Push(new LobbyScreen(LobbyIntent.Host));
    }

    private static async void OnJoinSelected(UiContext context)
    {
        var denial = await MultiplayerDenialAsync(context);

        if (!IsStillOnTop(context))
        {
            return;
        }

        if (denial is { Length: > 0 })
        {
            context.Screens.ShowDialog("Cannot Join", denial, DialogSeverity.Error);
            return;
        }

        context.Screens.Push(new LobbyScreen(LobbyIntent.Join));
    }

    /// <summary>
    /// The reason this player may not play online, or null when they may.
    /// </summary>
    /// <remarks>
    /// Connectivity is folded in here rather than checked separately so that every
    /// refusal site - Host, Join, and anything added later - covers being offline for
    /// free, and so the player is told the real reason rather than watching a connection
    /// attempt fail (XR-074). It is reported first because it is the more fundamental
    /// problem: an account without the multiplayer privilege still cannot fix anything
    /// until the console is back on a network. It is also the cheap half, a device hint
    /// rather than a platform round trip, so asking it first costs nothing.
    /// </remarks>
    private static async Task<string?> MultiplayerDenialAsync(UiContext context)
    {
        var runtime = context.Platform.Runtime;

        if (!runtime.IsOnline)
        {
            return runtime.OfflineReason;
        }

        // XR-045: EnsureAsync (rather than CheckAsync) drives the platform's own
        // resolve-with-UI flow on a denial - a subscription upsell, for instance - so a
        // fixable denial gets the system's fix-it surface instead of just this title's
        // message. A denial the UI cannot resolve (parental restriction, suspension)
        // still falls straight through unchanged.
        var verdict = await context.Platform.Privileges.EnsureAsync(GamePrivilege.Multiplayer);
        return verdict.Allowed ? null : verdict.Message;
    }

    /// <summary>
    /// Guards a privilege check's continuation against the player having already left
    /// this screen while it awaited - the source had no equivalent because GDScript's
    /// coroutines resume into whatever node is still in the tree, but a plain
    /// <c>async</c> continuation here would happily push a screen on top of whatever
    /// the player navigated to in the meantime.
    /// </summary>
    private static bool IsStillOnTop(UiContext context) => context.Screens.Current is MainMenuScreen;

    /// <summary>
    /// Party and matchmaking both require a signed-in user, so a denial is a dead end
    /// rather than something to silently downgrade - offer practice explicitly instead,
    /// matching the source's <c>_offer_practice</c>.
    /// </summary>
    private static void OfferPractice(UiContext context, string title, string reason)
    {
        var message = reason.Length == 0 ? "Online play is unavailable." : reason;
        message += "\n\nPlay a practice match offline instead?";

        context.Screens.ShowDialog(
            title,
            message,
            DialogSeverity.Warning,
            showCancel: true,
            onDismissed: accepted =>
            {
                if (accepted)
                {
                    context.Screens.Push(new LobbyScreen(LobbyIntent.Practice));
                }
            });
    }

    private static void OnQuit(UiContext context)
    {
        context.Screens.ShowDialog(
            "Quit",
            "Exit NetRumble?",
            DialogSeverity.Warning,
            showCancel: true,
            onDismissed: accepted =>
            {
                if (accepted)
                {
                    context.Game.Exit();
                }
            });
    }
}
