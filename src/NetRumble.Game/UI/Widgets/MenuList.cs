using Microsoft.Xna.Framework;

namespace NetRumble.Game.UI.Widgets;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_menu_list.gd</c>, itself
/// <c>Game/UI/UIElements/MenuListUIElement</c> + <c>FocusListUIElement</c>: a vertical stack
/// of rows with wrap-around focus navigation.
/// </summary>
/// <remarks>
/// <para>
/// Godot's focus system did the neighbour walking and the GDScript only rewired
/// <c>focus_neighbor_top</c>/<c>bottom</c> into a ring. Without that system the ring becomes
/// an index and a modulo, which is the same behaviour with far less machinery - and it makes
/// the rule the original had to encode by hand ("skip headers and notes") fall out of
/// keeping two lists: every child for drawing, focusable rows for navigation.
/// </para>
/// <para>
/// <b>Scrolling is new.</b> The Godot panel wrapped its list in a <c>ScrollContainer</c>.
/// Here the list scrolls itself, keeping the focused row inside <see cref="Bounds"/>. Only
/// the lobby and options lists ever overflow, but a list that silently clipped its last row
/// would be a navigation dead end on a controller.
/// </para>
/// </remarks>
public sealed class MenuList
{
    private readonly List<Widget> _children = [];
    private readonly List<Widget> _rows = [];

    private int _focusIndex = -1;
    private int _scroll;
    private bool _layoutDirty = true;
    private Rectangle _bounds;
    private int _rowSeparation = 5;

    /// <summary>The area the list lays out and clips into, in design units.</summary>
    public Rectangle Bounds
    {
        get => _bounds;
        set
        {
            _bounds = value;
            _layoutDirty = true;
        }
    }

    /// <summary>
    /// Gap between rows. The main menu stacks 54 px rows at a 54 px pitch, so it sets 0; the
    /// boxed lists keep a small gap so their backgrounds stay distinct.
    /// </summary>
    public int RowSeparation
    {
        get => _rowSeparation;
        set
        {
            _rowSeparation = value;
            _layoutDirty = true;
        }
    }

    /// <summary>Applied to buttons added through this list; false gives the bare-text rows.</summary>
    public bool BoxedRows { get; set; } = true;

    /// <summary>Raised with the focused row's index whenever focus moves.</summary>
    public event Action<int>? FocusChanged;

    /// <summary>
    /// Raised when the pointer put focus on a row, including when that row was already
    /// the focused one. The lobby uses it to move its focus zone into the roster, which
    /// no amount of index watching could tell it on its own.
    /// </summary>
    public event Action<int>? PointerFocused;

    /// <summary>Focusable rows, in order.</summary>
    public IReadOnlyList<Widget> Rows => _rows;

    /// <summary>The focused row, or null when the list is empty or unfocused.</summary>
    public Widget? Focused => _focusIndex >= 0 && _focusIndex < _rows.Count ? _rows[_focusIndex] : null;

    public int FocusIndex => _focusIndex;

    public Button AddButton(string text, Action? onSelected = null)
    {
        var button = new Button(text, onSelected) { Boxed = BoxedRows };
        AddRow(button);
        return button;
    }

    public Spinner<T> AddSpinner<T>(string label, IEnumerable<T> options, Action<int, T>? onChanged = null)
    {
        var spinner = new Spinner<T>(label, options, onChanged);
        AddRow(spinner);
        return spinner;
    }

    /// <summary>A 0-100 percent spinner bound to a normalised 0..1 value.</summary>
    public Spinner<int> AddPercentSpinner(string label, float initial, Action<float> onPercent)
    {
        var spinner = AddSpinner(
            label,
            Enumerable.Range(0, 101),
            (_, value) => onPercent(value / 100f));

        spinner.Format = value => $"{value}%";
        spinner.SelectValue(Math.Clamp((int)MathF.Round(initial * 100f), 0, 100));
        return spinner;
    }

    public Spinner<bool> AddBoolSpinner(string label, bool initial, Action<bool> onToggle)
    {
        var spinner = AddSpinner(label, new[] { false, true }, (_, value) => onToggle(value));
        spinner.Format = value => value ? "On" : "Off";
        spinner.SelectValue(initial);
        return spinner;
    }

    public Spinner<int> AddChoiceSpinner(
        string label,
        IReadOnlyList<string> choices,
        int initialIndex,
        Action<int> onChoice)
    {
        var spinner = AddSpinner(label, Enumerable.Range(0, choices.Count), (index, _) => onChoice(index));
        spinner.Format = index => choices[index];
        spinner.SelectValue(Math.Clamp(initialIndex, 0, Math.Max(choices.Count - 1, 0)));
        return spinner;
    }

    /// <summary>Non-focusable heading that groups the rows beneath it.</summary>
    public Label AddHeader(string text)
    {
        var header = new Label(text) { Color = UiTheme.Neutral };
        AddChild(header);
        return header;
    }

    /// <summary>Non-focusable informational row, for sections with nothing to configure.</summary>
    public Label AddNote(string text)
    {
        var note = new Label(text) { Align = TextAlign.Centre, Color = UiTheme.TextDisabled };
        AddChild(note);
        return note;
    }

    /// <summary>Adds an already-built widget as a focusable row.</summary>
    public void AddRow(Widget row)
    {
        AddChild(row);
        _rows.Add(row);
    }

    private void AddChild(Widget child)
    {
        _children.Add(child);
        _layoutDirty = true;
    }

    /// <summary>
    /// Empties the list. Headers and notes go too, so they cannot survive a rebuild - the
    /// GDScript freed every child for the same reason.
    /// </summary>
    public void Clear()
    {
        foreach (var row in _rows)
        {
            row.HasFocus = false;
        }

        _children.Clear();
        _rows.Clear();
        _focusIndex = -1;
        _scroll = 0;
        _layoutDirty = true;
    }

    public void FocusFirst()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].CanFocus)
            {
                SetFocus(i, null);
                return;
            }
        }
    }

    /// <summary>Focuses a row by position, silently ignoring an unfocusable target.</summary>
    public void FocusRow(int index, UiContext? context = null)
    {
        if (index >= 0 && index < _rows.Count && _rows[index].CanFocus)
        {
            SetFocus(index, context);
        }
    }

    public void Update(UiContext context)
    {
        Layout();

        // A row can be disabled or hidden between frames - a rebuild of the lobby does it
        // constantly - so focus has to be able to fall off it rather than becoming stuck.
        if (Focused is { CanFocus: false })
        {
            MoveFocus(context, 1);
        }

        foreach (var child in _children)
        {
            child.Update(context);
        }

        // Last, because activating a row commonly rebuilds or clears this list, and the
        // loop above must not be walking the children when that happens.
        UpdatePointer(context);
    }

    /// <summary>
    /// Hover-to-focus and click-to-activate, the mouse half of navigation.
    /// </summary>
    /// <remarks>
    /// Godot's <c>Control</c>s did this themselves: a <c>Button</c> grabbed focus under the
    /// cursor and emitted <c>pressed</c> on a click, for free. With the focus system gone
    /// the list has to do it, and the rules are the same two: moving the cursor over a
    /// focusable row focuses it, and a click focuses and activates it. Hover is gated on
    /// actual movement so a cursor parked over a row cannot fight the keyboard or pad for
    /// focus every frame.
    /// </remarks>
    private void UpdatePointer(UiContext context)
    {
        var input = context.Input;

        // The wheel steps focus rather than scrolling independently of it: this list only
        // scrolls to keep the focused row in view, so a detached scroll offset would snap
        // straight back the next time focus moved.
        if (input.PointerWheelDelta != 0 && Bounds.Contains(input.Pointer))
        {
            MoveFocus(context, input.PointerWheelDelta > 0 ? -1 : 1);
            input.ConsumeWheel();
        }

        if (!input.PointerMoved && !input.PointerPressed)
        {
            return;
        }

        var pointer = input.Pointer;
        var index = RowAt(pointer);

        if (index < 0)
        {
            return;
        }

        SetFocus(index, context);
        PointerFocused?.Invoke(index);

        if (!input.PointerPressed)
        {
            return;
        }

        // Claimed here, so the same click cannot also be read by a screen underneath or
        // by whatever this activation pushes on top.
        input.ConsumePointer();
        _rows[index].HandleClick(context, pointer);
    }

    /// <summary>
    /// The focusable row under a design-space point, or -1. Points outside
    /// <see cref="Bounds"/> miss on purpose: that is the same clip the draw pass applies
    /// to scrolled-away rows, so a row the player cannot see cannot be clicked either.
    /// </summary>
    public int RowAt(Point position)
    {
        if (!Bounds.Contains(position))
        {
            return -1;
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].CanFocus && _rows[i].HitTest(position))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Offers an action to the list: up/down navigate, anything else goes to the focused row.
    /// </summary>
    public bool HandleAction(UiContext context, MenuAction action)
    {
        if (_rows.Count == 0)
        {
            return false;
        }

        switch (action)
        {
            case MenuAction.Up:
                MoveFocus(context, -1);
                return true;
            case MenuAction.Down:
                MoveFocus(context, 1);
                return true;
            default:
                return Focused?.HandleAction(context, action) ?? false;
        }
    }

    public void Draw(UiContext context)
    {
        Layout();

        foreach (var child in _children)
        {
            // Rows scrolled out of the panel are skipped rather than clipped, which is enough
            // because rows never straddle the edge by much at this row height.
            if (child.Bounds.Bottom < Bounds.Y || child.Bounds.Y > Bounds.Bottom)
            {
                continue;
            }

            child.Draw(context);
        }
    }

    /// <summary>Total height the rows want, before scrolling.</summary>
    public int ContentHeight()
    {
        var height = 0;

        foreach (var child in _children)
        {
            if (child.Visible)
            {
                height += child.PreferredHeight + RowSeparation;
            }
        }

        return Math.Max(0, height - RowSeparation);
    }

    private void MoveFocus(UiContext? context, int direction)
    {
        var count = _rows.Count;

        if (count == 0)
        {
            return;
        }

        // Walks the ring at most once, so a list whose rows are all disabled terminates
        // instead of spinning.
        for (var step = 1; step <= count; step++)
        {
            var index = (((_focusIndex + (direction * step)) % count) + count) % count;

            if (_rows[index].CanFocus)
            {
                SetFocus(index, context);
                return;
            }
        }
    }

    private void SetFocus(int index, UiContext? context)
    {
        if (_focusIndex == index)
        {
            return;
        }

        if (Focused is { } previous)
        {
            previous.HasFocus = false;
        }

        _focusIndex = index;
        var row = _rows[index];
        row.HasFocus = true;

        if (context is not null)
        {
            row.OnFocusEntered(context);
        }

        _layoutDirty = true;
        FocusChanged?.Invoke(index);
    }

    /// <summary>
    /// Stacks the visible children from the top of <see cref="Bounds"/>, then shifts the whole
    /// stack so the focused row stays inside.
    /// </summary>
    private void Layout()
    {
        if (!_layoutDirty)
        {
            return;
        }

        _layoutDirty = false;

        var y = Bounds.Y - _scroll;

        foreach (var child in _children)
        {
            if (!child.Visible)
            {
                continue;
            }

            child.Bounds = new Rectangle(Bounds.X, y, Bounds.Width, child.PreferredHeight);
            y += child.PreferredHeight + RowSeparation;
        }

        if (Focused is not { } focused)
        {
            return;
        }

        var delta = 0;

        if (focused.Bounds.Y < Bounds.Y)
        {
            delta = focused.Bounds.Y - Bounds.Y;
        }
        else if (focused.Bounds.Bottom > Bounds.Bottom)
        {
            delta = focused.Bounds.Bottom - Bounds.Bottom;
        }

        if (delta == 0)
        {
            return;
        }

        _scroll += delta;

        foreach (var child in _children)
        {
            child.Bounds = child.Bounds with { Y = child.Bounds.Y - delta };
        }
    }
}
