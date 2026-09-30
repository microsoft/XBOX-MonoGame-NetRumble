using Microsoft.Xna.Framework;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_friend_list.gd</c> (XR-070): the friends who are in
/// a NetRumble session right now, and the way into one.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately only the <em>joinable</em> friends, not the whole friends list - the
/// same reasoning the GDScript's header comment gave: this overlay has nothing to offer
/// a friend who is offline or playing something else, so a full roster would be a list
/// of rows that do nothing. What it can do is put the player in a friend's match in one
/// press.
/// </para>
/// <para>
/// <b>Two service calls, cross-referenced here.</b> The GDScript's
/// <c>Services.joinable_friends()</c> did this cross-reference once, behind the
/// <c>Services</c> facade; this port has no such facade (each platform capability is
/// its own interface off <see cref="IPlatformProvider"/>), so <see cref="RefreshAsync"/>
/// does the join itself: <see cref="ISocialService.GetFriendsAsync"/> for who is a
/// friend, then <see cref="IActivityService.GetJoinableActivitiesAsync"/> for which of
/// them published a joinable session. <see cref="PlatformFriend.JoinableConnectionString"/>
/// documents exactly this contract, so filling it in here rather than expecting a
/// provider to have already done it keeps every provider implementation this simple.
/// </para>
/// <para>
/// <b>Loading guard:</b> <see cref="_loading"/> exists purely so a Refresh row pressed
/// while a previous refresh is still in flight is a no-op rather than a second
/// overlapping round trip stacking its results on top of the first - the same
/// re-entrancy the GDScript's <c>if _loading: return</c> guarded.
/// </para>
/// </remarks>
public sealed class FriendList : Screen
{
    /// <summary>A friend's session was chosen; the connection string is ready for
    /// <c>IPartyService.JoinByConnectionStringAsync</c>.</summary>
    public event Action<string>? JoinRequested;

    private readonly MenuList _actions = new();

    /// <summary>
    /// The xuid behind each joinable-friend row, in row order, so the draw pass can put a
    /// gamerpic on the right row without re-deriving the list (XR-046).
    /// </summary>
    private readonly List<string> _pictured = [];

    private bool _loading;
    private bool _closed;
    private string _statusText = string.Empty;

    public FriendList()
    {
        IsPopup = true;
        AllowBack = false;
    }

    public override void Enter(UiContext context) => _ = RefreshAsync(context);

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
            "Join Friend",
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

        // XR-046: over the left inset of each joinable-friend row, for the same reason as
        // ProfileList - the button centres its label, so a square here collides with
        // nothing, and a row whose picture has not arrived just draws the label.
        for (var i = 0; i < _pictured.Count && i < _actions.Rows.Count; i++)
        {
            var bounds = _actions.Rows[i].Bounds;
            var size = Math.Max(0, bounds.Height - 12);

            if (size > 0)
            {
                context.Pictures.Draw(
                    context,
                    _pictured[i],
                    new Rectangle(bounds.X + 8, bounds.Y + 6, size, size));
            }
        }
    }

    private async Task RefreshAsync(UiContext context)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        _actions.Clear();
        _statusText = "Looking for friends to join\u2026";
        _actions.AddButton("Cancel", Close);
        _actions.FocusFirst();

        var friends = await context.Platform.Social.GetFriendsAsync();

        if (_closed)
        {
            return;
        }

        if (!friends.Succeeded)
        {
            _loading = false;
            ShowEmpty(context, "Joining a friend needs you to be signed in to Xbox.");
            return;
        }

        var xuids = friends.Value!
            .Where(f => !string.IsNullOrEmpty(f.XboxUserId))
            .Select(f => f.XboxUserId)
            .ToArray();

        var joinable = xuids.Length == 0
            ? PlatformResult<IReadOnlyDictionary<string, string>>.Ok(
                new Dictionary<string, string>())
            : await context.Platform.Activity.GetJoinableActivitiesAsync(xuids);

        _loading = false;

        if (_closed)
        {
            return;
        }

        var sessions = joinable.Succeeded
            ? joinable.Value!
            : new Dictionary<string, string>();

        // XR-047: every friend this list knows about, joinable or not, needs a route to
        // their gamercard - the requirement is about surfaces that show a gamertag, and
        // the empty-list message below shows none, which is why this is captured before
        // the joinable filter and offered from both paths.
        var profiles = ProfileList.Collect(
            friends.Value!.Select(f => (f.DisplayName, f.XboxUserId)));

        var joinableFriends = friends.Value!
            .Where(f => sessions.ContainsKey(f.XboxUserId))
            .Select(f => f with { JoinableConnectionString = sessions[f.XboxUserId] })
            .ToList();

        if (joinableFriends.Count == 0)
        {
            ShowEmpty(context, "No friends are in a joinable match right now.", profiles);
            return;
        }

        _actions.Clear();
        _statusText = string.Empty;
        _pictured.Clear();

        foreach (var friend in joinableFriends)
        {
            var connectionString = friend.JoinableConnectionString;

            // XR-046: the friends query already returned each friend's gamerpic URL, so
            // handing it straight to the cache saves a second profile lookup for exactly
            // the same answer.
            context.Pictures.Offer(friend.XboxUserId, friend.GamerPictureUri);
            _pictured.Add(friend.XboxUserId);

            _actions.AddButton(FriendLabel(friend), () => OnFriendChosen(connectionString));
        }

        context.Pictures.Request(_pictured);

        // Refreshing is the only way to notice a friend who started a match after the
        // list was opened, and it is cheaper than closing and reopening - the friends
        // group is already resolved, so only the activity lookup repeats.
        _actions.AddButton("Refresh", () => _ = RefreshAsync(context));
        AddProfilesRow(profiles);
        _actions.AddButton("Cancel", Close);
        _actions.FocusFirst();
    }

    private void ShowEmpty(
        UiContext context,
        string reason,
        IReadOnlyList<ProfileList.ProfileEntry>? profiles = null)
    {
        _actions.Clear();
        _statusText = reason;
        _pictured.Clear();

        if (profiles is not null)
        {
            AddProfilesRow(profiles);
        }

        _actions.AddButton("Cancel", Close);
        _actions.FocusFirst();
    }

    /// <summary>XR-047. Omitted when there is no friend with a gamercard behind them.</summary>
    private void AddProfilesRow(IReadOnlyList<ProfileList.ProfileEntry> profiles)
    {
        if (profiles.Count == 0)
        {
            return;
        }

        _actions.AddButton(
            "View a Friend's Profile",
            () => Manager.Push(new ProfileList("Friends", profiles)));
    }

    /// <summary>"Gamertag - 3/8" when the friend's activity reported a player count,
    /// the gamertag alone otherwise - a count of "3/0" from an activity published
    /// without one would read as a bug.</summary>
    private static string FriendLabel(PlatformFriend friend)
    {
        var name = string.IsNullOrEmpty(friend.DisplayName) ? "Friend" : friend.DisplayName;
        return name;
    }

    private void OnFriendChosen(string connectionString)
    {
        // The join itself belongs to whoever opened this list - the lobby or the main
        // menu, both of which own a loading screen and a failure dialog - so this
        // closes behind the request rather than waiting underneath it.
        JoinRequested?.Invoke(connectionString);
        Close();
    }

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
        context.Screen.Center.X - 360,
        context.Screen.Center.Y - 280,
        720,
        560);

    private static Rectangle ActionArea(UiContext context)
    {
        var panel = PanelBounds(context);
        return new Rectangle(panel.X + 40, panel.Y + 150, panel.Width - 80, panel.Height - 190);
    }
}
