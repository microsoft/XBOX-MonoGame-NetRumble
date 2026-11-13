using NetRumble.Game.UI;
using NetRumble.Game.UI.Screens;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game;

/// <summary>
/// Publishes Xbox rich presence (XR-067) as the player moves between the menus, a lobby
/// and a match. Owned by <see cref="NetRumbleGame"/> alongside <see cref="InviteRouter"/>,
/// for the same reason: it needs the screen stack and the platform, and no screen should
/// have to know about presence.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the presence calls in <c>scripts/autoload/net_manager.gd</c> and
/// <c>scripts/services/platform_session.gd</c>, <b>with a deliberate and necessary
/// correction</b>. The Godot source calls <c>update_presence()</c> with free display text -
/// "Hosting a match", "Practice match", "In a match", "Playing {mode}", "In the menus" -
/// and passes it straight to the GDK. XSAPI has no way to show text a title invents at
/// runtime: presence strings live in the title's service configuration and are referenced
/// by id. <c>activity_service.gd</c>'s own comment concedes this ("the status strings have
/// to exist in the title's service configuration; an unconfigured one fails at the
/// service") - and none of those five strings is a configured id, so every one of them
/// would be rejected service-side. The Godot presence path is latently broken. Copying it
/// verbatim would reproduce the bug, so this class maps game state onto the ids that
/// actually exist instead.
/// </para>
/// <para>
/// <b>Three states, because the service configuration defines three strings.</b>
/// <c>GodotRumble/docs/localization.xml</c> is the source of truth and declares exactly
/// three presence entries - see <see cref="PresenceStrings"/>. Godot's five text variants
/// collapse onto them cleanly: hosting, joining and practice are all "playing a game", and
/// the mode name cannot be shown because no configured string has a token for it.
/// </para>
/// <para>
/// <b>Popups deliberately do not change presence.</b> The state is resolved by looking for
/// a gameplay or lobby screen anywhere in the stack rather than by reading the top screen,
/// so a dialog, the roster overlay or the in-game menu floating over a match all leave
/// presence as "playing a game" - which is what the player is still doing. Gameplay is
/// tested before the lobby because the lobby stays on the stack beneath a running match.
/// </para>
/// <para>
/// <b>Unverifiable here.</b> Setting presence needs a signed-in Xbox account and a live
/// service; on any other provider <see cref="PlatformCapabilities.Presence"/> is absent and
/// this class makes no calls at all. What has been verified is the state machine: the
/// mapping, the de-duplication and the re-publish on sign-in are ordinary game-layer code.
/// See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
public sealed class PresencePublisher
{
    private readonly UiContext _context;
    private readonly object _gate = new();
    private string? _wantedAccount;
    private string _wanted = string.Empty;
    private string? _publishedAccount;
    private bool _inFlight;
    private long _revision;
    private long _accountGeneration;

    /// <summary>
    /// The last id successfully published, so a presence call only goes out when the
    /// state actually changes. Screens push and pop constantly - every dialog, every
    /// options visit - and each publish is a service round trip, so re-sending the id
    /// already in effect would be pure traffic.
    /// </summary>
    private string _published = string.Empty;

    public PresencePublisher(UiContext context)
    {
        _context = context;

        context.Screens.ScreenPushed += OnScreenChanged;
        context.Screens.ScreenPopped += OnScreenChanged;
        context.Platform.Identity.UserChanged += OnUserChanged;

        Publish();
    }

    private void OnScreenChanged(Screen screen) => Publish();

    private void OnUserChanged(PlatformUser? user) => Publish();

    private void Publish()
    {
        // Without the native presence capability, there is no service to call.
        if (!_context.Platform.Capabilities.HasFlag(PlatformCapabilities.Presence))
        {
            return;
        }

        lock (_gate)
        {
            var account = _context.Platform.Identity.CurrentUser?.LocalId;
            if (account != _wantedAccount)
            {
                _publishedAccount = null;
                _accountGeneration++;
            }
            _wantedAccount = account;
            _wanted = Resolve();
            _revision++;
        }
        Kick();
    }

    private void Kick()
    {
        string id;
        string account;
        long revision;
        long accountGeneration;
        lock (_gate)
        {
            if (_inFlight || _wantedAccount is null
                || (_publishedAccount == _wantedAccount && _published == _wanted))
            {
                return;
            }
            _inFlight = true;
            account = _wantedAccount;
            id = _wanted;
            revision = _revision;
            accountGeneration = _accountGeneration;
        }
        _ = SendAsync(account, id, revision, accountGeneration);
    }

    private async Task SendAsync(string account, string id, long revision, long accountGeneration)
    {
        var succeeded = false;
        try
        {
            var result = await _context.Platform.Activity.SetPresenceAsync(id).ConfigureAwait(false);
            succeeded = result.Succeeded;
            if (result.Failed)
            {
                CrashLog.Mark($"presence: publication failed ({result}).");
            }
        }
        catch (Exception ex)
        {
            CrashLog.Mark($"presence: publication failed ({ex.GetType().Name}).");
        }
        finally
        {
            bool sendLatest;
            lock (_gate)
            {
                if (accountGeneration == _accountGeneration)
                {
                    _publishedAccount = succeeded ? account : null;
                    if (succeeded) { _published = id; }
                }
                _inFlight = false;
                // A failed send waits for another event, unless one arrived in flight.
                sendLatest = succeeded || revision != _revision;
            }
            if (sendLatest) { Kick(); }
        }
    }

    /// <summary>
    /// Maps the screen stack onto a configured presence string id. Order matters:
    /// <see cref="LobbyScreen"/> remains on the stack beneath a running match, so
    /// gameplay has to win.
    /// </summary>
    private string Resolve()
    {
        if (_context.Screens.Find<GameplayScreen>() is not null)
        {
            return PresenceStrings.InGame;
        }

        if (_context.Screens.Find<LobbyScreen>() is not null)
        {
            return PresenceStrings.InLobby;
        }

        return PresenceStrings.InMenu;
    }
}

/// <summary>
/// The presence string ids defined in the title's service configuration.
/// </summary>
/// <remarks>
/// <para>
/// Taken from <c>GodotRumble/docs/localization.xml</c>, which is the only record of this
/// title's presence configuration in either checkout. It declares three entries:
/// </para>
/// <list type="table">
///   <item><term><c>Presence_RP_IN_MENU_5e24f20d-7657-4138-a4ee-4a85d69c6366</c></term>
///     <description>"Browsing the menus"</description></item>
///   <item><term><c>Presence_RP_IN_LOBBY_3e412e0a-f3a7-4be6-9f5c-db90ec2559d7</c></term>
///     <description>"In a Lobby"</description></item>
///   <item><term><c>Presence_RP_IN_GAME_136dee55-3185-407b-a404-02be99dd477f</c></term>
///     <description>"Playing a Game"</description></item>
/// </list>
/// <para>
/// <b>The ids below are an inference from that file's naming, not a confirmed value.</b>
/// Localization entries are named <c>&lt;FieldKind&gt;_&lt;guid&gt;</c> - the achievement
/// entries in the same file are <c>AchievementNameId_&lt;guid&gt;</c> and
/// <c>LockedDescriptionId_&lt;guid&gt;</c> - and the presence entries carry one extra token
/// between the kind and the guid. That token is the friendly presence string id a title
/// passes to <c>XblPresenceSetPresenceAsync</c>, which is why it is present for presence
/// and absent for achievements. Nothing in this checkout confirms it against Partner
/// Center, and an id the service does not define is rejected service-side rather than
/// caught locally.
/// </para>
/// <para>
/// This is deliberately three one-line constants so that confirming - or correcting - them
/// against Partner Center is a trivial edit. It is tracked in <c>docs/known-gaps.md</c> beside
/// the unconfirmed SCID, which presence also depends on.
/// </para>
/// </remarks>
public static class PresenceStrings
{
    /// <summary>"Browsing the menus".</summary>
    public const string InMenu = "RP_IN_MENU";

    /// <summary>"In a Lobby".</summary>
    public const string InLobby = "RP_IN_LOBBY";

    /// <summary>"Playing a Game" - hosting, joining and practice alike.</summary>
    public const string InGame = "RP_IN_GAME";
}
