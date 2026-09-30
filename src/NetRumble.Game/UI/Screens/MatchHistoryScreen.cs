using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Game.Profile;
using NetRumble.Game.UI.Widgets;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/screens/match_history_screen.gd</c>, which replaces the C++
/// sample's <c>ExtrasMenuScreen</c> and <c>MatchHistoryScreen</c>: a navigation-only
/// menu screen and a list screen collapsed into one.
/// </summary>
/// <remarks>
/// This screen was <c>ExtrasScreen</c>, carrying a "View" spinner that switched between
/// Leaderboards and Match History. Leaderboards were removed (see
/// <c>docs/design-notes.md</c>), which left the spinner with nothing to switch between
/// and the screen titled "Extras" holding a single list - so it became what it actually
/// is. The two main-menu entries that deep-linked into the spinner collapsed into one.
/// </remarks>
public sealed class MatchHistoryScreen : MenuScreen
{
    /// <summary>
    /// True while <see cref="LoadRowsAsync"/> is in flight, so the list shows a
    /// placeholder row rather than a premature "nothing here" state.
    /// </summary>
    private bool _loading = true;

    private MatchHistoryEntry[] _entries = [];

    private bool _exited;

    protected override Point PanelSize => new(960, 800);

    protected override string Title => "Match History";

    public override void Enter(UiContext context)
    {
        // Skips the base Rebuild() call and does its own - LoadRowsAsync rebuilds
        // immediately with the loading placeholder before it awaits anything, then
        // again once the fetch resolves.
        LoadRowsAsync(context);
    }

    public override void Exit()
    {
        _exited = true;
        base.Exit();
    }

    protected override void BuildRows(UiContext context)
    {
        if (_loading)
        {
            // No callback: this row exists only so a controller has something to focus
            // while the fetch is in flight, matching the source's Callable() button.
            List.AddButton("Loading\u2026");
        }
        else
        {
            foreach (var entry in _entries)
            {
                // Entries stay focusable buttons even though they do nothing - focus is
                // what scrolls the list on a gamepad, so an unfocusable row would be
                // unreachable once the list runs past one screenful.
                List.AddRow(new MatchHistoryRow(entry));
            }

            if (_entries.Length == 0)
            {
                List.AddNote("No match history yet.");
            }
        }

        List.AddButton("Back", () => context.Screens.Pop());
    }

    /// <summary>Kicks off the fetch and rebuilds when it lands.</summary>
    private async void LoadRowsAsync(UiContext context)
    {
        _loading = true;
        _entries = [];
        Rebuild(context);

        // Match history in the source is a local user:// JSON file Services appended to
        // after every match. MatchHistoryStore is that file, over IGameSaveService's
        // local tier; GameplayScreen appends to it when the director reports a result.
        // Local only - see MatchHistoryStore for why this one save is not synced.
        var entries = await context.MatchHistory.LoadAsync();

        if (_exited)
        {
            // The screen left the stack while this was in flight, so the fetch's answer
            // is no longer wanted.
            return;
        }

        _loading = false;
        _entries = [.. entries];
        Rebuild(context);
    }

    private sealed class MatchHistoryRow(MatchHistoryEntry entry) : Widget
    {
        private const int HorizontalPadding = 18;

        public override bool Focusable => true;

        public override int PreferredHeight => 72;

        public override void Draw(UiContext context)
        {
            if (!Visible)
            {
                return;
            }

            context.Theme.Box(
                context.Batch,
                Bounds,
                HasFocus ? UiTheme.ButtonHover : UiTheme.ButtonNormal,
                HasFocus ? UiTheme.Accent : null);

            var colour = HasFocus ? UiTheme.Accent : UiTheme.Text;
            var maxWidth = Bounds.Width - (HorizontalPadding * 2);
            var x = Bounds.X + HorizontalPadding;

            UiTheme.TextLeft(
                context.Batch,
                context.Theme.Small,
                Ellipsize(context.Theme.Small, entry.ToTimestamp(), maxWidth),
                new Vector2(x, Bounds.Y + 22),
                colour);

            UiTheme.TextLeft(
                context.Batch,
                context.Theme.Small,
                Ellipsize(context.Theme.Small, entry.ToDetail(), maxWidth),
                new Vector2(x, Bounds.Y + 50),
                colour);
        }

        private static string Ellipsize(SpriteFont font, string text, float maxWidth)
        {
            if (font.MeasureString(text).X <= maxWidth)
            {
                return text;
            }

            const string Ellipsis = "\u2026";
            var low = 0;
            var high = text.Length;

            while (low < high)
            {
                var length = (low + high + 1) / 2;
                var candidate = text[..length].TrimEnd() + Ellipsis;

                if (font.MeasureString(candidate).X <= maxWidth)
                {
                    low = length;
                }
                else
                {
                    high = length - 1;
                }
            }

            return text[..low].TrimEnd() + Ellipsis;
        }
    }
}
