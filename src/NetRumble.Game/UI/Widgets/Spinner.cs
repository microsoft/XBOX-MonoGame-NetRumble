using Microsoft.Xna.Framework;

namespace NetRumble.Game.UI.Widgets;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_spinner.gd</c> and
/// <c>Game/UI/UIElements/SpinnerUIElement</c>: a left-arrow / value / right-arrow option
/// cycler.
/// </summary>
/// <remarks>
/// <para>
/// Generic over the option type so callers keep their own values rather than round-tripping
/// through <c>Variant</c>. The Godot version carried a parallel <c>display_texts</c> array
/// to override rendering (the "Off"/"On" of a boolean spinner); here that is a formatting
/// delegate, which covers the same cases without the two arrays being able to fall out of
/// step.
/// </para>
/// <para>
/// Wrap-around is on by default, matching the C++ <c>MoveNext</c>/<c>MovePrevious</c> and
/// the GDScript's <c>posmod</c>. Stepping plays <c>MenuScroll</c> only when the index
/// actually changed, so a clamped spinner at its end is silent.
/// </para>
/// </remarks>
public sealed class Spinner<T> : Widget
{
    /// <summary>Arrow glyph size, from the .tscn's TextureRects.</summary>
    private const int ArrowSize = 24;

    /// <summary>
    /// How far outside the glyph a click still counts. A 24 px target is fine to look at
    /// and far too small to hit, so the clickable arrow is padded to roughly a finger's
    /// worth of slack without the two arrows ever meeting.
    /// </summary>
    private const int ArrowClickPadding = 22;

    private readonly List<T> _options = [];
    private int _index;

    public Spinner(string label, IEnumerable<T> options, Action<int, T>? onChanged = null)
    {
        Label = label;
        _options.AddRange(options);
        Changed = onChanged;
    }

    /// <summary>Row caption. Empty hides the label and centres the value.</summary>
    public string Label { get; set; }

    /// <summary>Raised with the new index and value whenever the user steps the spinner.</summary>
    public Action<int, T>? Changed { get; set; }

    /// <summary>Renders an option. Defaults to <see cref="object.ToString"/>.</summary>
    public Func<T, string> Format { get; set; } = value => value?.ToString() ?? string.Empty;

    public bool WrapAround { get; set; } = true;

    public override bool Focusable => true;

    public int SelectedIndex => _index;

    public T? Current => _options.Count == 0 ? default : _options[Math.Clamp(_index, 0, _options.Count - 1)];

    /// <summary>Replaces the options, keeping the index in range.</summary>
    public void SetOptions(IEnumerable<T> options)
    {
        _options.Clear();
        _options.AddRange(options);
        _index = Math.Clamp(_index, 0, Math.Max(_options.Count - 1, 0));
    }

    /// <summary>Selects the option equal to <paramref name="value"/> without raising <see cref="Changed"/>.</summary>
    public void SelectValue(T value)
    {
        var found = _options.IndexOf(value);

        if (found >= 0)
        {
            _index = found;
        }
    }

    /// <summary>Selects by position without raising <see cref="Changed"/>.</summary>
    public void SelectIndex(int index)
        => _index = _options.Count == 0 ? 0 : Math.Clamp(index, 0, _options.Count - 1);

    public override void OnFocusEntered(UiContext context) => context.Audio.Play("MenuScroll");

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (Disabled || !Visible)
        {
            return false;
        }

        return action switch
        {
            MenuAction.Left => Step(context, -1),
            MenuAction.Right => Step(context, 1),
            _ => false,
        };
    }

    /// <summary>
    /// A click steps the spinner when it lands on one of the arrows, and otherwise only
    /// takes focus - the arrows are the only affordance the row draws, so clicking the
    /// label or the value has no obvious direction to mean.
    /// </summary>
    public override bool HandleClick(UiContext context, Point position)
    {
        if (Disabled || !Visible)
        {
            return false;
        }

        if (ClickableArrow(LeftArrowBounds()).Contains(position))
        {
            return Step(context, -1);
        }

        if (ClickableArrow(RightArrowBounds()).Contains(position))
        {
            return Step(context, 1);
        }

        return true;
    }

    /// <summary>The left arrow's glyph rect, also the anchor for its click target.</summary>
    private Rectangle ArrowBounds(int rightInset) => new(
        Bounds.Right - rightInset,
        Bounds.Center.Y - (ArrowSize / 2),
        ArrowSize,
        ArrowSize);

    private Rectangle LeftArrowBounds() => ArrowBounds(240);

    private Rectangle RightArrowBounds() => ArrowBounds(44);

    /// <summary>Arrow rect padded out to something a mouse can realistically hit.</summary>
    private Rectangle ClickableArrow(Rectangle arrow)
    {
        var padded = arrow;
        padded.Inflate(ArrowClickPadding, ArrowClickPadding);
        return Rectangle.Intersect(padded, Bounds);
    }

    /// <summary>Returns true even when the step is refused, so left/right never escape to the list.</summary>
    private bool Step(UiContext context, int direction)
    {
        if (_options.Count <= 1)
        {
            return true;
        }

        var next = _index + direction;

        next = WrapAround
            ? ((next % _options.Count) + _options.Count) % _options.Count
            : Math.Clamp(next, 0, _options.Count - 1);

        if (next == _index)
        {
            return true;
        }

        _index = next;
        context.Audio.Play("MenuScroll");
        Changed?.Invoke(_index, _options[_index]);
        return true;
    }

    public override void Draw(UiContext context)
    {
        if (!Visible)
        {
            return;
        }

        var fill = Disabled
            ? UiTheme.ButtonDisabled
            : HasFocus ? UiTheme.ButtonHover : UiTheme.ButtonNormal;

        context.Theme.Box(context.Batch, Bounds, fill, HasFocus && !Disabled ? UiTheme.Accent : null);

        var textColour = Disabled ? UiTheme.TextDisabled : HasFocus ? UiTheme.Accent : UiTheme.Text;
        var midY = Bounds.Center.Y;

        if (!string.IsNullOrEmpty(Label))
        {
            UiTheme.TextLeft(
                context.Batch, context.Theme.Body, Label, new Vector2(Bounds.X + 20, midY), textColour);
        }

        var value = _options.Count == 0 ? string.Empty : Format(_options[_index]);

        // The arrows are drawn from the shared UI atlas, as the .tscn's TextureRects were.
        var leftArrow = LeftArrowBounds();
        var rightArrow = RightArrowBounds();
        var arrowColour = _options.Count > 1 && !Disabled ? textColour : UiTheme.TextDisabled;

        context.Batch.Draw(context.Assets.Texture("Shape_LeftArrow"), leftArrow, arrowColour);
        context.Batch.Draw(context.Assets.Texture("Shape_RightArrow"), rightArrow, arrowColour);

        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Body,
            value,
            new Vector2((leftArrow.Right + rightArrow.Left) / 2f, midY),
            textColour);
    }
}
