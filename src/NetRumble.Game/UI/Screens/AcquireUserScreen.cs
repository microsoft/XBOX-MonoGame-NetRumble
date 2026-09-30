using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NetRumble.Game.Fx;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/screens/acquire_user_screen.gd</c>, modelled on the ATG
/// <c>AcquireUserScreen</c> pattern
/// (<c>Samples/_Deprecated/GameSave_Desktop/GameLogic/AcquireUserScreen.cpp</c>): the
/// title resolves an identity <i>before</i> the front end becomes interactive, rather
/// than signing in behind an already-live main menu.
/// </summary>
/// <remarks>
/// <para>
/// Why this is its own screen and not a spinner over the main menu: the platform's
/// account picker needs the game foregrounded and an answer from the player, and
/// running that underneath a live menu meant the picker fought the menu for input while
/// a failure showed as nothing more than a quiet "Not signed in" label. Here sign-in
/// owns the screen and every outcome is an explicit choice.
/// </para>
/// <para>
/// <b>Boot vs sign-in are two different waits.</b> <see cref="NetRumbleGame.PlatformSettled"/>
/// covers the runtime coming up (the GDK-equivalent init the game already runs before
/// pushing this screen); <see cref="IIdentityService.SignInAsync"/> is the separate,
/// player-visible chain this screen drives once that is done. The source only ever
/// waited on the second because Godot's autoloads did not model the first as a state
/// with its own failure mode; this port shows a boot status first, for the fraction of
/// a second (or, on a cold provider, longer) where the platform itself is still coming
/// up.
/// </para>
/// </remarks>
public sealed class AcquireUserScreen : MenuScreen
{
    /// <summary>How long the resolved gamertag stays up before the menu takes over.</summary>
    private const float ReadyDwellSeconds = 0.9f;

    /// <summary>
    /// How long a single sign-in step may run before the screen stops being a dead end
    /// and offers a way out (XR-074). Deliberately generous: one step waits on the
    /// player in system UI, and cancelling that would be wrong, so this never abandons
    /// the attempt - it keeps running underneath, and <see cref="RunSignInAsync"/>
    /// still takes over normally if it completes. This only adds an escape hatch for a
    /// call that never returns.
    /// </summary>
    private const float StallSeconds = 25f;

    /// <summary>
    /// Keys that only qualify another key. Godot's <c>_is_any_key_press</c> excluded
    /// these so a modifier held on the way to a shortcut could not itself arm sign-in.
    /// </summary>
    private static readonly Keys[] ModifierKeys =
    [
        Keys.LeftShift, Keys.RightShift, Keys.LeftControl, Keys.RightControl,
        Keys.LeftAlt, Keys.RightAlt, Keys.LeftWindows, Keys.RightWindows,
        Keys.CapsLock, Keys.NumLock, Keys.Scroll,
    ];

    private enum Stage
    {
        /// <summary>Waiting on <see cref="NetRumbleGame.PlatformSettled"/>.</summary>
        WaitingForPlatform,

        /// <summary>Idle prompt; any key or button starts the sign-in chain.</summary>
        WaitingForInput,

        /// <summary>Awaiting <see cref="IIdentityService.SignInAsync"/>.</summary>
        SigningIn,

        /// <summary>Sign-in did not complete; the player picks what happens next.</summary>
        NeedsInteraction,

        /// <summary>Signed in; showing the gamertag before handing over.</summary>
        Ready,
    }

    private readonly MenuBackdrop _backdrop = new();

    private Stage _stage = Stage.WaitingForPlatform;
    private string _status = "Starting the platform";
    private string _detail = string.Empty;
    private float _stageStartedAt;
    private float _readyEnteredAt;
    private bool _stalled;
    private bool _signingIn;
    private bool _exited;
    private UiContext? _enteredContext;

    /// <summary>
    /// Sign-in is the gate to everything online, so unlike most screens this is never
    /// dismissed by Back; the player leaves through one of its own rows.
    /// </summary>
    public AcquireUserScreen() => AllowBack = false;

    protected override string Title => "Sign In";

    /// <summary>
    /// <c>acquire_user_screen.tscn</c> is a full-screen composition with no panel, so the
    /// "panel" is the whole design canvas and every rect below is the source's own
    /// absolute geometry.
    /// </summary>
    protected override Point PanelSize => new(NetRumbleGame.DesignWidth, NetRumbleGame.DesignHeight);

    protected override bool ShowChrome => false;

    /// <summary>Where <c>MenuList</c> starts, from the scene's <c>(768,786)-(1152,1032)</c>.</summary>
    protected override int ListTopInset => 786;

    protected override int ListBottomInset => 48;

    /// <summary>The scene's <c>XboxLogo</c> rect.</summary>
    private static readonly Rectangle XboxLogoRect = new(750, 128, 420, 150);

    /// <summary>The scene's <c>NetRumbleLogo</c> rect.</summary>
    private static readonly Rectangle NetRumbleLogoRect = new(266, 284, 1380, 239);

    public override void Enter(UiContext context)
    {
        _enteredContext = context;
        base.Enter(context);

        if (!context.Game.PlatformSettled)
        {
            return;
        }

        // Being pushed on top of an existing stack (the main menu's "Sign In" row) is
        // itself the player choosing to sign in, so this skips the idle prompt and goes
        // straight to the picker; only a first-run boot with nothing behind it waits
        // for a press.
        if (Manager.StackSize > 1)
        {
            BeginSignIn(context);
        }
        else
        {
            EnterWaitingForInput(context);
        }
    }

    public override void Exit()
    {
        _exited = true;

        if (_enteredContext is { } context)
        {
            context.Platform.Identity.SignInStageChanged -= OnSignInStageChanged;
        }

        base.Exit();
    }

    public override void OnRevealed()
    {
        base.OnRevealed();

        // Re-focus after a dialog (or a re-push from the main menu) uncovers this
        // screen, so a controller does not land with nothing highlighted.
        if (_stage is Stage.NeedsInteraction || (_stage == Stage.SigningIn && _stalled))
        {
            List.FocusFirst();
        }
    }

    public override void Update(UiContext context)
    {
        base.Update(context);

        switch (_stage)
        {
            case Stage.WaitingForPlatform when context.Game.PlatformSettled:
                EnterWaitingForInput(context);
                break;

            case Stage.WaitingForInput when AnyKeyPressed(context):
                context.Audio.Play("MenuSelect");
                BeginSignIn(context);
                break;

            case Stage.SigningIn when !_stalled && context.Time - _stageStartedAt >= StallSeconds:
                EnterStalled(context);
                break;

            case Stage.Ready when context.Time - _readyEnteredAt >= ReadyDwellSeconds:
                HandOff(context);
                break;
        }
    }

    protected override void BuildRows(UiContext context)
    {
        // Bare-text rows, matching the source's NRTitleButton variation - the same look
        // the main menu uses, and unlike every boxed NRMenuPanel-derived screen.
        List.BoxedRows = false;
        List.RowSeparation = 0;

        if (_stage != Stage.NeedsInteraction && !(_stage == Stage.SigningIn && _stalled))
        {
            // WaitingForPlatform, WaitingForInput, Ready, and a sign-in that has not
            // stalled yet all show no rows - there is nothing yet for the player to do.
            return;
        }

        List.AddButton("Try Again", () => BeginSignIn(context));
        List.AddButton("Continue Offline", () => ContinueOffline(context));

        // Desktop only, for the same reason as the main menu's row: on console the Guide
        // is the way out, and this screen is reached before the player has committed to
        // anything, so there is nothing here that a console player would be trapped in.
        if (context.Platform.Runtime.DeviceKind == PlatformDeviceKind.Desktop)
        {
            List.AddButton("Quit", () => ConfirmQuit(context));
        }
    }

    public override void DrawWorld(UiContext context, float renderScale, int viewportWidth, int viewportHeight)
    {
        _backdrop.Update(context.Time);
        _backdrop.Draw(context, renderScale, viewportWidth, viewportHeight);
    }

    /// <summary>
    /// Nothing: the backdrop is the starfield drawn in <see cref="DrawWorld"/>, and an
    /// opaque fill here would cover it.
    /// </summary>
    protected override void DrawBackground(UiContext context)
    {
    }

    protected override void DrawOverlay(UiContext context)
    {
        UiTheme.TextureFit(
            context.Batch, context.Assets.Texture("Logo_Xbox"), XboxLogoRect, Color.White);
        UiTheme.TextureFit(
            context.Batch, context.Assets.Texture("Logo_NetRumble"), NetRumbleLogoRect, Color.White);

        // StatusLabel spans (360,626)-(1560,670) and is centred in both axes.
        var status = _stage == Stage.WaitingForInput ? context.Input.ContinuePrompt : _status;
        ButtonPrompt.DrawCentre(
            context.Batch, context.Assets, context.Theme.Button, status, new Vector2(960, 648), UiTheme.Text);

        if (_detail.Length > 0)
        {
            // DetailLabel is top-aligned from y=686 across (360..1560), and carries the
            // AccentLabel variation - the one green run of text on the screen.
            var lines = UiTheme.Wrap(context.Theme.Small, _detail, 1200);
            var y = 686f + (context.Theme.Small.LineSpacing / 2f);

            foreach (var line in lines)
            {
                UiTheme.TextCentre(
                    context.Batch, context.Theme.Small, line, new Vector2(960, y), UiTheme.Accent);
                y += context.Theme.Small.LineSpacing + 4;
            }
        }

        if (_stage is Stage.WaitingForPlatform || (_stage == Stage.SigningIn && !_stalled))
        {
            DrawSpinner(context, new Vector2(960, 560));
        }
    }

    private static void DrawSpinner(UiContext context, Vector2 centre)
    {
        var ring = context.Assets.Texture("Loading_Ring");
        var origin = new Vector2(ring.Width / 2f, ring.Height / 2f);

        // The source's AnimationPlayer looped one full turn every ~31.4s
        // (2*PI / 31.415926 rad/s); driven from elapsed time instead of a track so nothing
        // has to snap back to a first key.
        var rotation = context.Time * 0.2f;
        context.Batch.Draw(
            ring, centre, null, Color.FromNonPremultiplied(209, 235, 224, 191),
            rotation, origin, 96f / ring.Width, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// True for a genuine key press this frame, keyboard, mouse or bound menu action.
    /// </summary>
    /// <remarks>
    /// The source's <c>_is_any_key_press</c> matched keyboard, gamepad button and mouse
    /// button presses directly from the raw input event. <see cref="UiInput"/> exposes
    /// keyboard and mouse state but not raw pad buttons, so a bound
    /// <see cref="MenuAction"/> firing this frame stands in for "any controller button" -
    /// narrower than the source (an unbound pad button does nothing here, where Godot's
    /// picker would have woken on it too), but the only surface this port's input layer
    /// offers.
    /// </remarks>
    private static bool AnyKeyPressed(UiContext context)
    {
        var input = context.Input;

        // The click is claimed here so it cannot also reach the rows this screen grows
        // once sign-in has something to ask the player.
        if (input.AnyPointerButtonPressed)
        {
            input.ConsumePointer();
            return true;
        }

        foreach (var key in input.Keyboard.GetPressedKeys())
        {
            if (!input.PreviousKeyboard.IsKeyDown(key) && Array.IndexOf(ModifierKeys, key) < 0)
            {
                return true;
            }
        }

        foreach (var action in Enum.GetValues<MenuAction>())
        {
            if (action != MenuAction.None && input.Pressed(action))
            {
                return true;
            }
        }

        return false;
    }

    private void EnterWaitingForInput(UiContext context)
    {
        _stage = Stage.WaitingForInput;
        _status = "Press any key to continue";
        _detail = string.Empty;
        Rebuild(context);
    }

    /// <summary>
    /// One attempt at the whole sign-in chain. Re-entry guarded exactly as
    /// <c>Services.sign_in()</c> was documented to be, so "Try Again" can never start a
    /// second chain racing the one still running underneath a stalled first attempt.
    /// </summary>
    private void BeginSignIn(UiContext context)
    {
        if (_signingIn)
        {
            return;
        }

        _signingIn = true;
        _stage = Stage.SigningIn;
        _stalled = false;
        _stageStartedAt = context.Time;
        _status = "Signing in";
        _detail = context.Platform.Identity.CurrentStage;
        context.Platform.Identity.SignInStageChanged += OnSignInStageChanged;
        Rebuild(context);

        _ = RunSignInAsync(context);
    }

    private async Task RunSignInAsync(UiContext context)
    {
        var result = await context.Platform.Identity.SignInAsync(context.InteractiveSignIn);
        context.Platform.Identity.SignInStageChanged -= OnSignInStageChanged;
        _signingIn = false;

        if (_exited)
        {
            return;
        }

        if (result.Succeeded)
        {
            var user = result.Value!;
            context.Profile.SetIdentity(user.DisplayName, user.EntityId, user.XboxUserId);

            // Re-home the local settings file onto this account's own path before the
            // cloud pull, so two Xbox accounts on the same device never share or
            // overwrite each other's local settings (XR-052).
            context.Profile.Rehome(user.LocalId);

            // Only now is there an account to read a cloud save for, or to credit an
            // achievement to. Both are awaited here rather than fired off, so the menu
            // that opens next is already showing the settings this account last saved
            // instead of visibly changing them a moment later; both are best-effort
            // internally, so neither can hold up or fail the sign-in.
            await context.CloudSettings.PullAsync();
            await context.Achievements.SyncAsync();

            if (_exited)
            {
                // Both awaits above are round trips to a remote service, so the screen
                // can have been left in the meantime - by the stall rows' "Continue
                // Offline", for one.
                return;
            }

            EnterReady(context, user.DisplayName, user.IsDeveloperOverride);
        }
        else
        {
            EnterNeedsInteraction(context, result.Message ?? "Sign-in did not complete.");
        }
    }

    private void OnSignInStageChanged(string stage)
    {
        if (_stage != Stage.SigningIn || string.IsNullOrEmpty(stage))
        {
            return;
        }

        _detail = stage;

        // Progress resets the stall watchdog rather than letting it count the whole
        // chain, so a legitimately slow multi-step sign-in is not mistaken for a stuck one.
        if (_enteredContext is { } context)
        {
            _stageStartedAt = context.Time;
        }
    }

    private void EnterStalled(UiContext context)
    {
        _stalled = true;
        _status = "Still signing in";
        var stage = _detail.Length == 0 ? "Sign-in" : _detail;
        _detail = $"{stage} is taking longer than expected.\nYou can keep waiting, or continue without signing in.";
        Rebuild(context);
    }

    private void EnterNeedsInteraction(UiContext context, string reason)
    {
        _stage = Stage.NeedsInteraction;
        _stalled = false;
        _status = "Not signed in";
        _detail = reason;
        Rebuild(context);
    }

    private void EnterReady(UiContext context, string displayName, bool isDeveloperOverride)
    {
        _stage = Stage.Ready;
        _stalled = false;
        _readyEnteredAt = context.Time;

        // Makes a --pf-user test client obvious so it is never mistaken for a real
        // sign-in while debugging a Party session.
        var suffix = isDeveloperOverride ? "  (test user)" : string.Empty;
        _status = $"Signed in as {displayName}{suffix}";
        _detail = string.Empty;
        Rebuild(context);
    }

    /// <summary>Practice mode needs no identity, so offline is a real choice, not a dead end.</summary>
    private static void ContinueOffline(UiContext context) => context.Screens.ReplaceAll(new MainMenuScreen());

    private static void HandOff(UiContext context) => context.Screens.ReplaceAll(new MainMenuScreen());

    private static void ConfirmQuit(UiContext context)
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
