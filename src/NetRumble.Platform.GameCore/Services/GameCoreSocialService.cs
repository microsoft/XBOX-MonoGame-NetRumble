using GDK.Net;
using GDK.Net.XboxLive;
using NetRumble.Platform.GameCore.Interop;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK friends list, over GDK.Net's <see cref="SocialService"/> and
/// <see cref="ProfileService"/> query APIs.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/social_service.gd</c>, with one deliberate shape
/// change. The Godot source wraps the addon's Social Manager, a tracked graph object
/// that the platform keeps live in the background - <c>get_friends_async</c> creates a
/// group once and every later read is local. GDK.Net exposes
/// <see cref="XboxLiveService.SocialManager"/> for this, but using it would require a
/// <c>DoWork</c> pump and user add/remove lifecycle for something the title opens
/// occasionally, so this class re-queries via
/// <see cref="SocialService.GetRelationshipsAsync"/> on every call instead. The result
/// is the same data - each call costs a round trip that the Godot version amortises,
/// which is an acceptable trade for a friends list a player opens occasionally rather
/// than every frame, and is called out here rather than silently diverging from the
/// source of truth.
/// </para>
/// <para>
/// <b>Display names need a second call.</b> <see cref="SocialRelationship"/> carries
/// only a xuid and three booleans - Godot's tracked group gets gamertags for free
/// because the Social Manager fetches them internally, but the flat relationship query
/// does not. So this class follows up with a batched
/// <see cref="ProfileService.GetAsync(System.Collections.Generic.IEnumerable{ulong}, CancellationToken)"/>
/// for every xuid the relationship call returned, matching Godot's own fallback order
/// (gamertag, then display name, then the xuid itself) when a field is empty.
/// </para>
/// <para>
/// <b>Unverifiable in this environment.</b> A real friends list needs a signed-in
/// account that actually has Xbox friends; this class was verified only to build and to
/// degrade cleanly with no context. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
internal sealed class GameCoreSocialService : ISocialService
{
    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly GameCoreXblContext _xbl;

    internal GameCoreSocialService(GameCoreRuntime runtime, GameCoreIdentityService identity, GameCoreXblContext xbl)
    {
        _runtime = runtime;
        _identity = identity;
        _xbl = xbl;
    }

    /// <summary>
    /// <b>No <c>FriendsChanged</c> event.</b> There is no tracked group here to notice
    /// a change on - see the type remarks on why every <see cref="GetFriendsAsync"/>
    /// call re-queries instead of subscribing to one - so this class could never have
    /// raised one. <see cref="ISocialService"/> dropped the member in Phase 20 rather
    /// than keep a promise no implementation keeps; its remarks record what restoring
    /// it would cost.
    /// </summary>
    public async Task<PlatformResult<IReadOnlyList<PlatformFriend>>> GetFriendsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_runtime.IsInitialized || !_identity.TryGetUser(out _))
        {
            // Empty, not a failure: matches the Godot source's fail-soft contract - a
            // desktop dev machine with no Xbox account has nobody to be friends with.
            return PlatformResult<IReadOnlyList<PlatformFriend>>.Ok([]);
        }

        if (!_xbl.TryGetContext(out var context))
        {
            return PlatformResult<IReadOnlyList<PlatformFriend>>.Ok([]);
        }

        var xuid = context.XboxUserId;

        IReadOnlyList<SocialRelationship> allRelationships;
        try
        {
            using var firstPage = await _runtime.Dispatcher
                .Marshal(context.Social.GetRelationshipsAsync(
                    xuid,
                    SocialRelationshipFilter.All,
                    startIndex: 0,
                    maxItems: 0,
                    cancellationToken))
                .ConfigureAwait(false);

            allRelationships = await _runtime.Dispatcher
                .Marshal(firstPage.ReadAllAsync(cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult<IReadOnlyList<PlatformFriend>>.Fail(
                PlatformStatus.Failed, "The friends list could not be read.", ex.Message);
        }

        // A "relationship" the local user only follows (a public figure, say) is not a
        // friend by this title's definition - only mutual friends are shown, matching
        // what the Godot addon's default friends group means.
        var relationships = allRelationships
            .Where(r => r.IsFriend)
            .Select(r => (r.XboxUserId, r.IsFavorite))
            .ToList();

        if (relationships.Count == 0)
        {
            return PlatformResult<IReadOnlyList<PlatformFriend>>.Ok([]);
        }

        var displayNames = await TryGetProfilesAsync(context, relationships, cancellationToken)
            .ConfigureAwait(false);

        var friends = relationships
            .Select(r => new PlatformFriend
            {
                XboxUserId = r.XboxUserId.ToString(),
                DisplayName = displayNames.TryGetValue(r.XboxUserId, out var profile)
                    && profile.Name.Length > 0
                        ? profile.Name
                        : r.XboxUserId.ToString(),
                GamerPictureUri = displayNames.GetValueOrDefault(r.XboxUserId).Picture ?? string.Empty,
                IsFavorite = r.IsFavorite,
            })
            // Matches Godot's naturalnocasecmp_to sort on gamertag/display name.
            .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return PlatformResult<IReadOnlyList<PlatformFriend>>.Ok(friends);
    }

    public void Clear()
    {
        // Nothing is cached beyond the platform's own state - see the type remarks on
        // why this class re-queries on every call - so there is nothing to drop, unlike
        // the tracked-group Godot source this clears a handle for.
    }

    /// <inheritdoc />
    public async Task<PlatformResult<string>> ResolveDisplayNameAsync(
        string xboxUserId,
        CancellationToken cancellationToken = default)
    {
        if (!_runtime.IsInitialized || !ulong.TryParse(xboxUserId, out var xuid) || !_xbl.TryGetContext(out var context))
        {
            return PlatformResult<string>.Unavailable();
        }

        try
        {
            var profiles = await _runtime.Dispatcher
                .Marshal(context.Profiles.GetAsync([xuid], cancellationToken))
                .ConfigureAwait(false);

            var profile = profiles.FirstOrDefault(p => p.XboxUserId == xuid);

            // Same fallback order as TryGetDisplayNamesAsync: gamertag, then display
            // name. No xuid fallback here - a caller with a wire value to fall back to
            // should keep using it rather than replace it with a bare number.
            var name = profile?.Gamertag is { Length: > 0 } gamertag ? gamertag
                : profile?.GameDisplayName is { Length: > 0 } displayName ? displayName
                : null;

            return name is null
                ? PlatformResult<string>.Fail(PlatformStatus.Failed, "No profile was returned for that player.")
                : PlatformResult<string>.Ok(name);
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult<string>.Fail(PlatformStatus.Failed, "The player's profile could not be read.", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyDictionary<string, string>>> ResolveGamerPicturesAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default)
    {
        if (!_runtime.IsInitialized || !_xbl.TryGetContext(out var context))
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Unavailable();
        }

        var xuids = xboxUserIds
            .Select(id => ulong.TryParse(id, out var parsed) ? parsed : 0UL)
            .Where(id => id != 0)
            .Distinct()
            .ToArray();

        if (xuids.Length == 0)
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Ok(
                new Dictionary<string, string>());
        }

        try
        {
            var profiles = await _runtime.Dispatcher
                .Marshal(context.Profiles.GetAsync(xuids, cancellationToken))
                .ConfigureAwait(false);

            var pictures = new Dictionary<string, string>(profiles.Count, StringComparer.Ordinal);

            foreach (var profile in profiles)
            {
                // The game picture, not the app one: it is the square avatar the shell
                // shows beside a gamertag in-game, which is what every surface asking for
                // this is drawing next to a name.
                if (profile.GameDisplayPictureUri is { Length: > 0 } uri)
                {
                    pictures[profile.XboxUserId.ToString()] = uri;
                }
            }

            return PlatformResult<IReadOnlyDictionary<string, string>>.Ok(pictures);
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Fail(
                PlatformStatus.Failed, "Player pictures could not be read.", ex.Message);
        }
    }

    /// <summary>
    /// Best-effort gamertag and gamerpic lookup. A failure here still returns the friends
    /// list - the xuid itself is used as the fallback display name - because a broken
    /// profile lookup should not hide a roster the relationship call already confirmed
    /// exists.
    /// </summary>
    private async Task<Dictionary<ulong, (string Name, string Picture)>> TryGetProfilesAsync(
        XboxLiveContext context,
        List<(ulong XboxUserId, bool IsFavorite)> relationships,
        CancellationToken cancellationToken)
    {
        try
        {
            var xuids = relationships.Select(r => r.XboxUserId).ToArray();
            var profiles = await _runtime.Dispatcher
                .Marshal(context.Profiles.GetAsync(xuids, cancellationToken))
                .ConfigureAwait(false);

            var resolved = new Dictionary<ulong, (string, string)>(profiles.Count);
            foreach (var profile in profiles)
            {
                // Matches the Godot source's fallback order exactly: gamertag first,
                // then display name, then (there, xuid; here, the caller's own fallback).
                var name = profile.Gamertag.Length > 0 ? profile.Gamertag
                    : profile.GameDisplayName.Length > 0 ? profile.GameDisplayName
                    : string.Empty;

                resolved[profile.XboxUserId] = (name, profile.GameDisplayPictureUri ?? string.Empty);
            }

            return resolved;
        }
        catch (GameRuntimeException)
        {
            return [];
        }
    }
}
