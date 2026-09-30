namespace NetRumble.Platform;

/// <summary>
/// Friends list and player profiles. Ported from
/// <c>scripts/services/social_service.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no <c>FriendsChanged</c> event.</b> One existed until Phase
/// 20 and no implementation could ever raise it, so it was removed rather than left as
/// a permanently dead member that callers might reasonably believe in.
/// </para>
/// <para>
/// The Godot source it was ported from gets change notifications for free because it
/// holds a Social Manager tracked group, a live graph object the platform maintains in
/// the background. This port deliberately dropped that shape - see
/// <c>GameCoreSocialService</c>'s remarks - and re-queries on each
/// <see cref="GetFriendsAsync"/> call instead, because maintaining the group needs a
/// <c>DoWork</c> pump and per-user add/remove lifecycle for a list the player opens
/// occasionally rather than every frame. Re-querying is a real trade, but an event that
/// only ever fired as a side effect of the caller's own query would be worse than none:
/// the caller already holds the fresh list the moment it could fire.
/// </para>
/// <para>
/// A live-updating friends UI would need the tracked group restored. Until one exists,
/// callers refresh by calling <see cref="GetFriendsAsync"/> again, which is what
/// <c>FriendList</c> does when it opens.
/// </para>
/// </remarks>
public interface ISocialService
{
    /// <summary>Reads the signed-in user's friends. Empty when unavailable.</summary>
    Task<PlatformResult<IReadOnlyList<PlatformFriend>>> GetFriendsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the platform's own gamertag for a xuid (XR-046/XR-048). Used to
    /// override a display name a peer sent over the wire with the account's live,
    /// service-verified one - the wire value is whatever string the sender's own build
    /// chose to send, which nothing stops from being stale, wrong, or the previous
    /// account signed into that console.
    /// </summary>
    Task<PlatformResult<string>> ResolveDisplayNameAsync(
        string xboxUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves gamerpic URLs for a set of xuids (XR-046).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="ResolveDisplayNameAsync"/> and batched, because the
    /// surfaces that want pictures want them for a whole roster or friends list at once
    /// and the platform resolves a batch in one round trip. A xuid the platform has no
    /// answer for is simply absent from the result rather than present with an empty URL:
    /// "no picture" and "not asked about" are the same thing to a caller that falls back
    /// to initials either way.
    /// </para>
    /// <para>
    /// The result is a URL rather than image bytes. Gamerpic URLs are public and
    /// unauthenticated, so the caller fetches and caches the image itself - which it has
    /// to do regardless, since decoding to a GPU texture is a title-side concern this
    /// layer has no business knowing about.
    /// </para>
    /// </remarks>
    Task<PlatformResult<IReadOnlyDictionary<string, string>>> ResolveGamerPicturesAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default);

    /// <summary>Drops cached social state. Called when the active user changes.</summary>
    void Clear();
}

/// <summary>One entry in the friends list.</summary>
public sealed record PlatformFriend
{
    public required string XboxUserId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsOnline { get; init; }
    public bool IsFavorite { get; init; }

    /// <summary>Rich presence text, when the platform provides it.</summary>
    public string PresenceText { get; init; } = string.Empty;

    /// <summary>
    /// The friend's gamerpic URL, or empty when the platform did not supply one
    /// (XR-046). Public and unauthenticated; see
    /// <see cref="ISocialService.ResolveGamerPicturesAsync"/>.
    /// </summary>
    public string GamerPictureUri { get; init; } = string.Empty;

    /// <summary>
    /// Connection string when this friend is in a joinable session, else empty.
    /// Populated by cross-referencing <see cref="IActivityService.GetJoinableActivitiesAsync"/>.
    /// </summary>
    public string JoinableConnectionString { get; init; } = string.Empty;

    public bool IsJoinable => JoinableConnectionString.Length > 0;
}
