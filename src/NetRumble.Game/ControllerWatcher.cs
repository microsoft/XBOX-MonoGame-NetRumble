using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NetRumble.Platform;

namespace NetRumble.Game;

/// <summary>
/// Notices when the player's controller goes away and when it comes back (XR-115).
/// Ported from <c>scripts/services/device_service.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the <i>association</i> half of the controller requirement and nothing more. It
/// answers one question - does the player still have a pad - and deliberately does not try
/// to answer "which device index is theirs". Godot records that the second question sank an
/// earlier attempt: the platform speaks device ids with no documented correspondence to the
/// small integers an engine uses, and a guessed correlation was wrong often enough to make
/// the title unplayable. Knowing that a pad vanished never required knowing which one.
/// </para>
/// <para>
/// <b>Two sources, because neither covers every machine.</b>
/// <see cref="IIdentityService.ControllerAssociationChanged"/> is scoped to the signed-in
/// account, which is the question XR-115 actually asks, since a second player's pad
/// disconnecting is not this player's problem. MonoGame's <see cref="GamePad"/> is all
/// there is on a desktop machine with no GDK, and is also the only thing that sees a pad
/// that was already associated before the title registered for change notifications. Both
/// collapse into <see cref="Observe"/>, so one disconnect noticed twice raises one
/// reaction.
/// </para>
/// <para>
/// <b>Polled, not evented, on the desktop side.</b> MonoGame raises nothing when a pad is
/// unplugged - there is no equivalent of Godot's <c>joy_connection_changed</c> - so the only
/// way to see it is to look. <see cref="Update"/> is cheap: four struct reads a frame, and
/// it stops at the first connected pad.
/// </para>
/// </remarks>
public sealed class ControllerWatcher
{
    /// <summary>
    /// How many pad slots to look at. MonoGame exposes four, and this only ever asks
    /// whether <i>any</i> of them is present, so there is nothing to gain from more.
    /// </summary>
    private const int PadSlots = 4;

    private readonly IIdentityService _identity;

    /// <summary>
    /// The last answer given, so only transitions are reported.
    /// </summary>
    /// <remarks>
    /// Seeded by <see cref="Start"/> rather than assumed, which is what keeps a
    /// keyboard-only desktop machine quiet: it never has a pad, so it never transitions
    /// away from having one, so the prompt never appears. Starting this as <c>true</c>
    /// would raise the prompt on the first frame of every keyboard session.
    /// </remarks>
    private bool _hasController;
    private bool _started;

    public ControllerWatcher(IIdentityService identity)
    {
        _identity = identity;
        _identity.ControllerAssociationChanged += OnAssociationChanged;
    }

    /// <summary>The player had a controller and no longer does.</summary>
    public event Action? ControllerLost;

    /// <summary>The player has a controller again, after having lost one.</summary>
    public event Action? ControllerBound;

    /// <summary>Whether the player currently has a controller.</summary>
    public bool HasController => _hasController;

    /// <summary>
    /// Takes the baseline silently. Whatever the player has right now is the state they
    /// are already in, and there is nothing to announce about that.
    /// </summary>
    public void Start()
    {
        _started = true;
        _hasController = Observe();
    }

    /// <summary>Polls the desktop source. Call once per frame.</summary>
    public void Update()
    {
        if (_started)
        {
            Set(Observe());
        }
    }

    private void OnAssociationChanged()
    {
        if (_started)
        {
            Set(Observe());
        }
    }

    /// <summary>
    /// The current answer, from whichever sources are credible on this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Union, not precedence</b> (XR-115). The platform used to win outright once it
    /// had ever answered, and that produced a false "controller lost" on console. The GDK
    /// raises <c>XUserDeviceAssociationChanged</c> only for associations that change
    /// <i>after</i> registration; it never replays the ones that already exist, and there
    /// is no enumeration of a user's devices reachable from GDK.Net - the flat API only
    /// goes the other way, from a device to its user, and getting the device list at all
    /// means binding GameInput. So a player who signed in holding the pad they are still
    /// holding is, as far as the association set is concerned, holding nothing. The set
    /// is therefore evidence that a pad is present, never evidence that none is.
    /// </para>
    /// <para>
    /// The cost of the union is that a second player's pad can keep this prompt down for
    /// the first. That is strictly better than the alternative: an unclosable overlay
    /// over a paused match for a player whose controller is plugged in and working.
    /// </para>
    /// </remarks>
    private bool Observe()
        => (_identity.TracksControllerAssociations && _identity.HasAssociatedController)
            || AnyPadConnected();

    private static bool AnyPadConnected()
    {
        for (var i = 0; i < PadSlots; i++)
        {
            if (GamePad.GetState((PlayerIndex)i).IsConnected)
            {
                return true;
            }
        }

        return false;
    }

    private void Set(bool value)
    {
        if (_hasController == value)
        {
            return;
        }

        _hasController = value;

        if (value)
        {
            ControllerBound?.Invoke();
        }
        else
        {
            ControllerLost?.Invoke();
        }
    }
}
