using NetRumble.Game.UI;
using NetRumble.Game.UI.Screens;
using NetRumble.Platform;

namespace NetRumble.Game;

/// <summary>
/// Turns an accepted Xbox invite or a protocol launch into a join, exactly as
/// <c>scripts/autoload/invite_router.gd</c> does. Lives as its own object, owned by
/// <see cref="NetRumbleGame"/>, for the same reason the GDScript gave for its own
/// separate autoload: the decision needs both <see cref="ScreenManager"/> and the
/// active match screen, and neither should have to know about invites.
/// </summary>
/// <remarks>
/// <b>What is actually verifiable here.</b> <see cref="IActivityService.InviteAccepted"/>
/// only ever fires from a real Xbox-signed-in session receiving a real platform
/// activation - see <c>GameCoreActivityService</c>'s own remarks for exactly what that
/// needs and why it cannot be exercised in this environment. This class's own logic
/// (queuing until sign-in, the "already mid-match" confirmation, and handing the
/// connection string to <see cref="LobbyScreen"/>) is ordinary, synchronous game-layer
/// code and needs no platform at all to be correct, but the one thing that would prove
/// it end-to-end - a real invite arriving - could not be produced here.
/// </remarks>
public sealed class InviteRouter
{
    private readonly UiContext _context;

    /// <summary>An invite that arrived before the title could act on it, held until it
    /// can be. A cold launch from an invite is the normal case, not an edge case - the
    /// activation fires while the player is still on the acquire-user screen, before
    /// sign-in has even started.</summary>
    private string? _pendingConnectionString;

    /// <summary>Set for the short synchronous hand-off to <see cref="LobbyScreen"/>, so
    /// a second invite arriving in the same instant is dropped rather than racing it -
    /// mirrors <c>invite_router.gd</c>'s own <c>_joining</c> guard, though that guard
    /// spans the whole network join there; here the join itself runs inside
    /// <see cref="LobbyScreen"/>'s own connect flow once handed off, so this only
    /// needs to cover the hand-off.</summary>
    private bool _joining;

    public InviteRouter(UiContext context)
    {
        _context = context;
        context.Platform.Identity.UserChanged += OnUserChanged;
        context.Platform.Activity.InviteAccepted += OnInviteAccepted;
    }

    /// <summary>
    /// True while an invite is held waiting for sign-in. Read by the lifecycle path on
    /// resume: an invite accepted while the title was suspended is *why* the player came
    /// back, so it gets the front end instead of a dialog about the match they lost.
    /// </summary>
    public bool HasPendingInvite => _pendingConnectionString is not null;

    private void OnInviteAccepted(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString) || _joining)
        {
            return;
        }

        _pendingConnectionString = connectionString;
        TryFlush();
    }

    private void OnUserChanged(PlatformUser? user)
    {
        if (user is null)
        {
            return;
        }

        TryFlush();
    }

    /// <summary>
    /// Pumped once a frame by <see cref="NetRumbleGame"/>, after the screens have
    /// updated. The event handlers above cannot be the only trigger: the last condition a
    /// held invite waits on is the front end coming up, and no identity event is raised
    /// when that happens.
    /// </summary>
    public void Update() => TryFlush();

    /// <summary>
    /// Acts on a held invite once it actually can be acted on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sign-in is not one event.</b> The Xbox account resolves first and the PlayFab
    /// entity is added after it, and Party refuses a join outright without that entity
    /// ("Online play needs a PlayFab entity for the signed-in Xbox user"). A cold launch
    /// from an invite is precisely the case that hits the gap, since the invite is already
    /// waiting when sign-in starts, so this waits for
    /// <see cref="IIdentityService.IsSignInInProgress"/> to clear rather than for the
    /// first user event. It deliberately does not require a non-empty entity id: the
    /// PlayFab step is allowed to fail, and when it has, joining and showing the specific
    /// refusal beats silently swallowing the invite.
    /// </para>
    /// <para>
    /// <b>And the front end has to exist.</b> <see cref="AcquireUserScreen"/>
    /// replaces the whole stack with the main menu when it is done, which would throw away
    /// a lobby pushed underneath it a moment earlier - so the invite waits for that screen
    /// to leave instead of racing its hand-off.
    /// </para>
    /// </remarks>
    private void TryFlush()
    {
        if (_joining || _pendingConnectionString is not { } connectionString)
        {
            return;
        }

        var identity = _context.Platform.Identity;
        if (identity.CurrentUser is null || identity.IsSignInInProgress)
        {
            return;
        }

        if (_context.Screens.Find<AcquireUserScreen>() is not null)
        {
            return;
        }

        _pendingConnectionString = null;
        Join(connectionString);
    }

    /// <summary>Mirrors <c>invite_router.gd</c>'s <c>_join</c>: confirm before
    /// abandoning a live match, then land on the lobby exactly as a typed join code
    /// would via <c>main_menu_screen._on_join_code_submitted</c>.</summary>
    private void Join(string connectionString)
    {
        if (IsMidMatch())
        {
            _context.Screens.ShowDialog(
                "Join Match",
                "Leave the current match and join your friend's match?",
                DialogSeverity.Warning,
                showCancel: true,
                onDismissed: confirmed =>
                {
                    if (confirmed)
                    {
                        DoJoin(connectionString);
                    }
                });
            return;
        }

        DoJoin(connectionString);
    }

    private void DoJoin(string connectionString)
    {
        _joining = true;
        _context.Screens.ReplaceAll(new LobbyScreen(LobbyIntent.Join, connectionString));
        _joining = false;
    }

    /// <summary>
    /// Whether the player is already in a live online session - checks both screens
    /// that can own an <see cref="NetRumble.Core.Net.IMatchNetwork"/> rather than a
    /// second network field the way the pre-Phase-6 UI kept, matching
    /// <c>NetManager.is_offline()</c> + <c>NetManager.local_player() != null</c>.
    /// </summary>
    private bool IsMidMatch()
        => _context.Screens.Find<LobbyScreen>()?.IsOnline == true
            || _context.Screens.Find<GameplayScreen>()?.IsOnline == true;
}
