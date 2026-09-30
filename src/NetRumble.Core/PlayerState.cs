using NetRumble.Core.Tuning;

namespace NetRumble.Core;

/// <summary>
/// Per-player lobby and match state, ported from
/// <c>scripts/gameplay/player_state.gd</c> (originally
/// <c>Game/Gameplay/PlayerState.h</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PeerId"/> is the transport's peer id and is the authoritative key used by
/// the net layer.
/// </para>
/// <para>
/// <b>No PlayFab entity id (XR-014).</b> One used to travel here, in every roster row and
/// in each <c>ShipSpawn</c>. Nothing ever read it - the net layer keys players by peer id
/// and Party authenticates entity keys itself - so all it did was hand every peer in the
/// match a durable identifier for the other players' accounts in the publisher's service.
/// </para>
/// <para>
/// The <c>to_dict</c> and <c>from_dict</c> pair existed only for the GDScript RPC
/// boundary, where roster entries travelled as untyped dictionaries. The C# net layer
/// serialises this type directly, so they are not ported.
/// </para>
/// </remarks>
public sealed class PlayerState
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Xbox user id; empty for offline play and any build without the GDK.</summary>
    /// <remarks>
    /// <b>NOT authenticated.</b> There is no authenticated XUID the way there is an
    /// authenticated Party entity key, so a modified client can claim someone else's. It is
    /// used only for recent-player reporting, so the worst case is a polluted local
    /// recent-players list. Do not treat it as proof of identity or key anything
    /// security-relevant on it.
    /// </remarks>
    public string XboxUserId { get; set; } = string.Empty;

    public int PeerId { get; set; }

    public int ShipColorId { get; set; }

    public int ShipStyleId { get; set; }

    public bool InGame { get; set; }

    public int Score { get; set; }

    public bool IsReady { get; set; }

    /// <summary>Unique id of the ship this player currently controls, 0 when not spawned.</summary>
    public int ShipId { get; set; }

    /// <summary>Local-only; deliberately never replicated.</summary>
    public bool IsLocalPlayer { get; set; }

    /// <summary>
    /// True for an AI practice opponent rather than a person.
    /// </summary>
    /// <remarks>
    /// Local-only, and deliberately never replicated: bots exist in offline practice
    /// matches only, so there is never a peer to tell about one.
    /// </remarks>
    public bool IsBot { get; set; }

    public bool IsValid => !string.IsNullOrEmpty(DisplayName);

    public RgbaColor Color => TuningLibrary.Palette.ColorAt(ShipColorId);
}
