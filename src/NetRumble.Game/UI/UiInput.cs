using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace NetRumble.Game.UI;

/// <summary>
/// The menu-facing half of the <c>[input]</c> map in <c>project.godot</c>.
/// </summary>
/// <remarks>
/// <para>
/// Godot's <c>InputMap</c> is data; MonoGame has no equivalent, so the bindings become the
/// switch in <see cref="IsHeld"/>. Every binding from the project file is reproduced,
/// including the ones a keyboard-only developer never exercises: the D-pad and left stick
/// both drive navigation, Enter/keypad-Enter/Space and A all accept, and Escape and B both
/// go back.
/// </para>
/// <para>
/// <b>Auto-repeat is added, not ported.</b> Godot's UI repeats held directions through
/// <c>ui_up</c>/<c>ui_down</c> echo events with the engine's own timings. Nothing in the
/// GDScript configured them, so the values here are Godot's defaults
/// (<c>gui/timers/button_activate_delay</c>-adjacent behaviour): a longer first delay, then
/// a fast repeat. Only directional actions repeat - holding A must not re-activate a button.
/// </para>
/// </remarks>
public sealed class UiInput
{
    /// <summary>Godot's <c>input_devices/buffering/...</c> initial repeat delay.</summary>
    private const float RepeatDelay = 0.4f;

    /// <summary>Interval between repeats once <see cref="RepeatDelay"/> has elapsed.</summary>
    private const float RepeatInterval = 0.1f;

    /// <summary>
    /// Stick deflection past which an analogue axis counts as a digital press, matching the
    /// <c>"deadzone": 0.5</c> every action in the project file declares.
    /// </summary>
    private const float StickDeadzone = 0.5f;

    private readonly Dictionary<MenuAction, float> _heldFor = [];
    private readonly HashSet<MenuAction> _pressed = [];

    private KeyboardState _keys;
    private GamePadState _pad;
    private MouseState _mouse;
    private KeyboardState _previousKeys;
    private MouseState _previousMouse;
    private bool _mouseIsActiveDevice;
    private Point _pointer;
    private Point _previousPointer;
    private bool _pointerMoved;
    private bool _pointerPressed;
    private bool _anyPointerButtonPressed;
    private bool _pointerReleased;

    /// <summary>Current keyboard state, for the screens that read raw text.</summary>
    public KeyboardState Keyboard => _keys;

    /// <summary>
    /// Current gamepad state, so gameplay reads the same devices the menus sampled.
    /// </summary>
    public GamePadState GamePad => _pad;

    /// <summary>Current mouse state, for mouse aiming and the reticle.</summary>
    public MouseState Mouse => _mouse;

    /// <summary>
    /// True when the mouse was the last pointing device the player touched.
    /// </summary>
    /// <remarks>
    /// Godot decided this from which <c>InputEvent</c> subclass arrived. MonoGame polls
    /// rather than delivering events, so the equivalent is a frame-to-frame comparison:
    /// the mouse claims the flag when it moves or a button changes, and the pad takes it
    /// back when a stick leaves its dead zone or any button is down. A player who is
    /// using neither keeps whichever device they used last, which is the point - it stops
    /// a stray crosshair being parked wherever the mouse was left.
    /// </remarks>
    public bool IsMouseActiveDevice => _mouseIsActiveDevice;

    /// <summary>
    /// The cursor in design-resolution units, so widgets can hit-test against the same
    /// rectangles they lay out and draw with.
    /// </summary>
    /// <remarks>
    /// The backbuffer position MonoGame reports is in window pixels, while every UI
    /// rectangle is authored against the 1920x1080 canvas and drawn through the
    /// letterbox transform. Undoing that transform here - once, per frame - is what makes
    /// a click land on the row the player is actually pointing at at any window size.
    /// </remarks>
    public Point Pointer => _pointer;

    /// <summary>True on a frame where the cursor moved, so hover only steals focus then.</summary>
    /// <remarks>
    /// Without this, a stationary cursor resting over a row would fight every keyboard or
    /// pad press by re-focusing that row on the following frame.
    /// </remarks>
    public bool PointerMoved => _pointerMoved;

    /// <summary>True on the frame the left mouse button goes down, until consumed.</summary>
    public bool PointerPressed => _pointerPressed;

    /// <summary>
    /// True on the frame any mouse button goes down, until consumed. Separate from
    /// <see cref="PointerPressed"/> because the widgets only act on the left button, while
    /// an "any button to continue" prompt means what it says.
    /// </summary>
    public bool AnyPointerButtonPressed => _anyPointerButtonPressed;

    /// <summary>True on the frame the left mouse button is released, until consumed.</summary>
    public bool PointerReleased => _pointerReleased;

    /// <summary>Wheel movement since the previous frame, in notches (positive is up).</summary>
    public int PointerWheelDelta { get; private set; }

    /// <summary>True when the primary player has an attached gamepad this frame.</summary>
    public bool IsGamePadConnected => _pad.IsConnected;

    /// <summary>Prompt text for a generic continue action, matching the active device.</summary>
    public string ContinuePrompt
        => IsGamePadConnected ? "Press any button to continue" : "Press any key or click to continue";

    /// <summary>Prompt text for submitting a focused text field.</summary>
    /// <remarks>
    /// The <c>{A}</c>-style tokens become button icons - see <see cref="ButtonPrompt"/>.
    /// Cert requires the icon rather than the letter, and a prompt that names a button in
    /// text is only correct by accident on a pad whose labels match.
    /// </remarks>
    public string TextSubmitPrompt
        => IsGamePadConnected ? "{A} Send    {B} Cancel" : "Enter to send - Esc to cancel";

    /// <summary>Prompt text for readying in the lobby.</summary>
    public string ReadyPrompt
        => IsGamePadConnected ? "Press {X} when you are set" : "Press Ready when you are set";

    /// <summary>Footer controls for the lobby, matching the active device.</summary>
    public string LobbyHint(bool canToggleVoice, bool isMuted)
    {
        if (IsGamePadConnected)
        {
            return canToggleVoice
                ? $"{{LB}} Customize    {{RB}} Roster    {{X}} Ready    {{Y}} {(isMuted ? "Unmute" : "Mute")}    {{B}} Leave"
                : "{LB} Customize    {RB} Roster    {X} Ready    {B} Leave";
        }

        return canToggleVoice
            ? $"[Q] Customize    [E] Roster    [Space] Ready    [T] {(isMuted ? "Unmute" : "Mute")}    [Esc] Leave"
            : "[Q] Customize    [E] Roster    [Space] Ready    [Esc] Leave";
    }

    /// <summary>
    /// The one-line control reminder shown over the first seconds of a match.
    /// </summary>
    /// <remarks>
    /// Firing is bound to the right stick, which nothing else in the game uses and which
    /// no first-time player guesses - bug-bash testers sat in a live match unable to
    /// work out how to shoot. The binding itself is unchanged (it is what the original
    /// sample used, and changing it would break every player who does know it), so this
    /// is the discoverability half of the fix: the match says what fires, up front,
    /// rather than leaving it to be found by accident.
    /// </remarks>
    public string MatchControlsHint => IsGamePadConnected
        ? "Left Stick Fly    Right Stick Aim and Fire    Right Trigger Mine    {Y} Chat    {MENU} Menu"
        : "[WASD] Fly    [Arrows] Aim and Fire    [Space] Mine    [T] Chat    [Esc] Menu";

    /// <summary>
    /// The full control scheme, as label/binding pairs, for the Controls screen. Both
    /// device columns are always listed rather than only the attached one: a player
    /// reading this on a keyboard may be about to pick up a pad, and a controls page
    /// that hides half the scheme is the gap this screen exists to close.
    /// </summary>
    public static IReadOnlyList<(string Action, string Keyboard, string Gamepad)> ControlScheme { get; } =
    [
        ("Fly", "W A S D", "Left Stick"),
        ("Aim and fire", "Arrow keys", "Right Stick"),
        ("Drop mine", "Space", "Right Trigger"),
        ("Chat message", "T", "Y"),
        ("Pause menu", "Esc", "Menu"),
    ];

    /// <summary>Keyboard state from the previous frame, for edge detection.</summary>
    public KeyboardState PreviousKeyboard => _previousKeys;

    /// <summary>Samples input and recomputes edges and repeats for this frame.</summary>
    /// <param name="renderScale">Design-to-backbuffer scale, for <see cref="Pointer"/>.</param>
    /// <param name="letterboxOffset">Backbuffer offset of the design frame's top-left.</param>
    public void Update(
        KeyboardState keys,
        GamePadState pad,
        MouseState mouse,
        float delta,
        float renderScale = 1f,
        Vector2 letterboxOffset = default)
    {
        _previousKeys = _keys;
        _keys = keys;
        _pad = pad;
        _previousMouse = _mouse;
        _mouse = mouse;

        _previousPointer = _pointer;
        _pointer = ToDesignSpace(mouse.Position, renderScale, letterboxOffset);
        _pointerMoved = _pointer != _previousPointer;
        _pointerPressed = mouse.LeftButton == ButtonState.Pressed
            && _previousMouse.LeftButton == ButtonState.Released;
        _anyPointerButtonPressed = _pointerPressed
            || (mouse.RightButton == ButtonState.Pressed
                && _previousMouse.RightButton == ButtonState.Released)
            || (mouse.MiddleButton == ButtonState.Pressed
                && _previousMouse.MiddleButton == ButtonState.Released);
        _pointerReleased = mouse.LeftButton == ButtonState.Released
            && _previousMouse.LeftButton == ButtonState.Pressed;
        PointerWheelDelta = (mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue) / 120;

        UpdateActiveDevice();

        _pressed.Clear();

        foreach (var action in Enum.GetValues<MenuAction>())
        {
            if (action == MenuAction.None)
            {
                continue;
            }

            var held = IsHeld(action);

            if (!held)
            {
                _heldFor.Remove(action);
                continue;
            }

            if (!_heldFor.TryGetValue(action, out var elapsed))
            {
                // Rising edge: fires immediately, before any repeat timing applies.
                _heldFor[action] = 0f;
                _pressed.Add(action);
                continue;
            }

            if (!Repeats(action))
            {
                _heldFor[action] = elapsed + delta;
                continue;
            }

            var next = elapsed + delta;

            if (elapsed < RepeatDelay && next >= RepeatDelay)
            {
                _pressed.Add(action);
            }
            else if (elapsed >= RepeatDelay
                && MathF.Floor((elapsed - RepeatDelay) / RepeatInterval)
                    < MathF.Floor((next - RepeatDelay) / RepeatInterval))
            {
                _pressed.Add(action);
            }

            _heldFor[action] = next;
        }
    }

    /// <summary>True on the frame an action fires, including auto-repeat.</summary>
    public bool Pressed(MenuAction action) => _pressed.Contains(action);

    /// <summary>Clears this frame's edges, so one screen cannot see another's input.</summary>
    /// <remarks>
    /// Called after a screen transition. Godot got this for free: a screen pushed during
    /// <c>_unhandled_input</c> never saw the event that pushed it, because the event was
    /// already consumed. Here every screen polls the same state, so the accept press that
    /// opened a screen would otherwise immediately activate that screen's default row -
    /// and the same is true of the click that opened it, which is why the pointer edges
    /// are cleared alongside the button ones.
    /// </remarks>
    public void ConsumeAll()
    {
        _pressed.Clear();
        ConsumePointer();
    }

    /// <summary>
    /// Drops this frame's pointer edges once a widget has acted on them, so a single
    /// click cannot also be claimed by a row underneath or by the next screen.
    /// </summary>
    public void ConsumePointer()
    {
        _pointerPressed = false;
        _anyPointerButtonPressed = false;
        _pointerReleased = false;
        _pointerMoved = false;
        PointerWheelDelta = 0;
    }

    /// <summary>Drops the wheel delta once a list has scrolled on it.</summary>
    public void ConsumeWheel() => PointerWheelDelta = 0;

    /// <summary>
    /// Maps a backbuffer position into the design canvas, undoing the letterbox
    /// translation and uniform scale the UI is drawn through.
    /// </summary>
    private static Point ToDesignSpace(Point position, float renderScale, Vector2 letterboxOffset)
    {
        if (renderScale <= 0f)
        {
            return position;
        }

        return new Point(
            (int)MathF.Round((position.X - letterboxOffset.X) / renderScale),
            (int)MathF.Round((position.Y - letterboxOffset.Y) / renderScale));
    }

    /// <summary>Directional actions repeat while held; activation actions do not.</summary>
    private static bool Repeats(MenuAction action) => action
        is MenuAction.Up or MenuAction.Down or MenuAction.Left or MenuAction.Right;

    /// <summary>
    /// Decides which pointing device the player is currently using. See
    /// <see cref="IsMouseActiveDevice"/> for why this is a poll rather than an event.
    /// </summary>
    private void UpdateActiveDevice()
    {
        if (_mouse.Position != _previousMouse.Position
            || _mouse.LeftButton != _previousMouse.LeftButton
            || _mouse.RightButton != _previousMouse.RightButton
            || _mouse.MiddleButton != _previousMouse.MiddleButton)
        {
            _mouseIsActiveDevice = true;
            return;
        }

        if (!_pad.IsConnected)
        {
            return;
        }

        var stickMoved = MathF.Abs(_pad.ThumbSticks.Left.X) > StickDeadzone
            || MathF.Abs(_pad.ThumbSticks.Left.Y) > StickDeadzone
            || MathF.Abs(_pad.ThumbSticks.Right.X) > StickDeadzone
            || MathF.Abs(_pad.ThumbSticks.Right.Y) > StickDeadzone;

        if (stickMoved || _pad.Buttons != default || _pad.DPad != default)
        {
            _mouseIsActiveDevice = false;
        }
    }

    private bool IsHeld(MenuAction action) => action switch
    {
        MenuAction.Up => _keys.IsKeyDown(Keys.Up)
            || _pad.IsButtonDown(Buttons.DPadUp)
            || _pad.ThumbSticks.Left.Y > StickDeadzone,

        MenuAction.Down => _keys.IsKeyDown(Keys.Down)
            || _pad.IsButtonDown(Buttons.DPadDown)
            || _pad.ThumbSticks.Left.Y < -StickDeadzone,

        MenuAction.Left => _keys.IsKeyDown(Keys.Left)
            || _pad.IsButtonDown(Buttons.DPadLeft)
            || _pad.ThumbSticks.Left.X < -StickDeadzone,

        MenuAction.Right => _keys.IsKeyDown(Keys.Right)
            || _pad.IsButtonDown(Buttons.DPadRight)
            || _pad.ThumbSticks.Left.X > StickDeadzone,

        // ui_accept binds Enter, keypad Enter and Space as well as A.
        MenuAction.Accept => _keys.IsKeyDown(Keys.Enter)
            || _keys.IsKeyDown(Keys.Space)
            || _pad.Buttons.A == ButtonState.Pressed,

        // ui_back_action and ui_cancel share bindings in the project file.
        MenuAction.Back => _keys.IsKeyDown(Keys.Escape)
            || _pad.Buttons.B == ButtonState.Pressed,

        // prev3 / next3: the shoulder buttons that page the lobby's colour and ship strips.
        MenuAction.PagePrevious => _keys.IsKeyDown(Keys.Q)
            || _pad.Buttons.LeftShoulder == ButtonState.Pressed,

        MenuAction.PageNext => _keys.IsKeyDown(Keys.E)
            || _pad.Buttons.RightShoulder == ButtonState.Pressed,

        MenuAction.ToggleReady => _keys.IsKeyDown(Keys.Space)
            || _pad.Buttons.X == ButtonState.Pressed,

        MenuAction.GameMenu => _keys.IsKeyDown(Keys.Escape)
            || _pad.Buttons.Start == ButtonState.Pressed,

        MenuAction.VoiceChat => _keys.IsKeyDown(Keys.T)
            || _pad.Buttons.Y == ButtonState.Pressed,

        _ => false,
    };
}

/// <summary>
/// The named actions the UI reacts to, one per relevant entry in the Godot input map.
/// </summary>
public enum MenuAction
{
    None,

    /// <summary><c>ui_up</c>.</summary>
    Up,

    /// <summary><c>ui_down</c>.</summary>
    Down,

    /// <summary><c>ui_left</c>.</summary>
    Left,

    /// <summary><c>ui_right</c>.</summary>
    Right,

    /// <summary><c>ui_accept</c> / <c>ui_select_action</c>.</summary>
    Accept,

    /// <summary><c>ui_back_action</c> / <c>ui_cancel</c>.</summary>
    Back,

    /// <summary><c>prev3</c>.</summary>
    PagePrevious,

    /// <summary><c>next3</c>.</summary>
    PageNext,

    /// <summary><c>toggle_ready</c>.</summary>
    ToggleReady,

    /// <summary><c>toggle_game_menu</c>.</summary>
    GameMenu,

    /// <summary><c>open_voice_chat</c>.</summary>
    VoiceChat,
}
