using Microsoft.Xna.Framework;
using NetRumble.Game.UI.Widgets;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// A list of players whose gamercards can be opened (XR-047).
/// </summary>
/// <remarks>
/// <para>
/// XR-047 requires a route to the system gamercard from <i>every</i> surface that shows
/// another player's gamertag, not only the one that happens to have an action menu. The
/// lobby roster has <see cref="PlayerActions"/>; the end-of-match standings and the
/// friends list did not, because neither is built from focusable per-player rows - the
/// standings are a block of text in a dialog and the friend rows are already bound to
/// joining. Rather than give both their own bespoke affordance, both open this.
/// </para>
/// <para>
/// <b>It does not close on selection.</b> The system card is modal over the title, so
/// coming back from one and opening the next is the expected flow; closing this list
/// under the first card would make viewing two gamercards take two trips through the
/// screen that opened it. Every other popup in the port closes on selection because
/// every other popup performs an action that ends the reason for being there.
/// </para>
/// <para>
/// Players with no Xbox user id are omitted rather than shown as dead rows: an offline
/// practice bot and a LAN peer both have a display name and no gamercard behind it.
/// </para>
/// </remarks>
public sealed class ProfileList : Screen
{
    private readonly string _title;
    private readonly IReadOnlyList<ProfileEntry> _entries;
    private readonly MenuList _actions = new();

    private bool _closed;
    private string _statusText = string.Empty;

    public ProfileList(string title, IReadOnlyList<ProfileEntry> entries)
    {
        _title = title;
        _entries = entries;

        IsPopup = true;
        AllowBack = false;
    }

    /// <summary>One selectable player.</summary>
    /// <param name="DisplayName">The gamertag as the surface that opened this showed it.</param>
    /// <param name="XboxUserId">The xuid the gamercard is opened for. Never empty.</param>
    public readonly record struct ProfileEntry(string DisplayName, string XboxUserId);

    /// <summary>
    /// Builds an entry list from any name-and-xuid pairing, dropping the ones with no
    /// xuid and de-duplicating by xuid - a player can appear twice in a standings list
    /// joined to a roster, and two rows opening the same card is noise.
    /// </summary>
    public static IReadOnlyList<ProfileEntry> Collect(IEnumerable<(string Name, string XboxUserId)> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ProfileEntry>();

        foreach (var (name, xuid) in candidates)
        {
            if (string.IsNullOrEmpty(xuid) || !seen.Add(xuid))
            {
                continue;
            }

            entries.Add(new ProfileEntry(string.IsNullOrEmpty(name) ? "Player" : name, xuid));
        }

        return entries;
    }

    public override void Enter(UiContext context)
    {
        _actions.Clear();

        // XR-046: one batched lookup for the whole list rather than one per row as each
        // is drawn. The rows draw text until the pictures arrive, and draw text for good
        // if they never do.
        context.Pictures.Request(_entries.Select(e => e.XboxUserId));

        foreach (var entry in _entries)
        {
            var xuid = entry.XboxUserId;
            _actions.AddButton(entry.DisplayName, () => OnProfile(context, xuid));
        }

        if (_entries.Count == 0)
        {
            _statusText = "There are no profiles to show.";
        }

        _actions.AddButton("Close", Close);
        _actions.FocusFirst();
    }

    public override void Update(UiContext context)
    {
        _actions.Bounds = ActionArea(context);
        _actions.Update(context);
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (action == MenuAction.Back)
        {
            Close();
            return true;
        }

        return _actions.HandleAction(context, action);
    }

    public override void Draw(UiContext context)
    {
        var panel = PanelBounds(context);
        context.Theme.Fill(context.Batch, context.Screen, Color.FromNonPremultiplied(0, 0, 0, 140));
        context.Theme.Box(context.Batch, panel, UiTheme.DialogPanel, UiTheme.DialogBorder);

        var titleBar = new Rectangle(panel.X, panel.Y, panel.Width, 80);
        context.Theme.Box(context.Batch, titleBar, UiTheme.DialogTitle, UiTheme.Accent);
        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Subtitle,
            _title,
            new Vector2(titleBar.Center.X, titleBar.Center.Y),
            UiTheme.Text);

        if (_statusText.Length > 0)
        {
            UiTheme.TextCentre(
                context.Batch,
                context.Theme.Body,
                _statusText,
                new Vector2(panel.Center.X, titleBar.Bottom + 48),
                UiTheme.TextDisabled);
        }

        _actions.Draw(context);
        DrawGamerPictures(context);
    }

    /// <summary>
    /// Draws each row's gamerpic over the left inset of the button the row already drew
    /// (XR-046).
    /// </summary>
    /// <remarks>
    /// Over the row rather than inside it because <see cref="Widgets.Button"/> centres its
    /// text: a picture at the left inset does not collide with a gamertag of any plausible
    /// length, and this needs no per-row widget type for what is one optional square.
    /// Rows the pictures have not arrived for - or never will, because the platform has no
    /// social service - simply draw nothing extra.
    /// </remarks>
    private void DrawGamerPictures(UiContext context)
    {
        var rows = _actions.Rows;

        for (var i = 0; i < _entries.Count && i < rows.Count; i++)
        {
            var bounds = rows[i].Bounds;
            var size = Math.Max(0, bounds.Height - 12);

            if (size == 0)
            {
                continue;
            }

            context.Pictures.Draw(
                context,
                _entries[i].XboxUserId,
                new Rectangle(bounds.X + 8, bounds.Y + 6, size, size));
        }
    }

    /// <summary>
    /// Deliberately not awaited, and the list stays up behind it: the platform card is
    /// modal, so there is nothing to do here until it comes back, and nothing here
    /// consumes its result.
    /// </summary>
    private static void OnProfile(UiContext context, string xboxUserId)
        => _ = context.Platform.Moderation.ShowProfileCardAsync(xboxUserId);

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Manager.Pop();
    }

    private static Rectangle PanelBounds(UiContext context) => new(
        context.Screen.Center.X - 340,
        context.Screen.Center.Y - 260,
        680,
        520);

    private static Rectangle ActionArea(UiContext context)
    {
        var panel = PanelBounds(context);
        return new Rectangle(panel.X + 40, panel.Y + 150, panel.Width - 80, panel.Height - 190);
    }
}
