namespace NetRumble.Core.Net;

/// <summary>
/// Why a match ended. Replaces the bare reason strings the GDScript put in the
/// <c>match_completed</c> payload dictionary.
/// </summary>
public enum MatchEndReason
{
    /// <summary>The mode's time limit elapsed.</summary>
    TimeLimit,

    /// <summary>A player reached the mode's target score.</summary>
    ScoreLimit,

    /// <summary>Everyone but one player left, so there was nobody left to play against.</summary>
    LastPlayerStanding,

    /// <summary>
    /// The host left, so the match lost the peer that was running it. Distinct from
    /// <see cref="LastPlayerStanding"/> because clients reach it without the authority
    /// having said anything: there may well be other players still connected, but with
    /// nobody left to simulate the world the match is over for all of them.
    /// </summary>
    HostLeft,
}

/// <summary>One player's final placing, ported from the standings dictionaries.</summary>
/// <param name="PeerId">Network peer the entry belongs to.</param>
/// <param name="DisplayName">Name shown on the results screen.</param>
/// <param name="Score">Final score.</param>
/// <param name="Placement">One-based finishing position, best first.</param>
public readonly record struct MatchStanding(
    int PeerId,
    string DisplayName,
    int Score,
    int Placement);

/// <summary>
/// The completed-match payload. In the GDScript this was an untyped <c>Dictionary</c>
/// passed through <c>broadcast_match_completed</c>; making it a record means the results
/// screen and the stats write can no longer disagree about key names.
/// </summary>
/// <param name="Reason">Why the match ended.</param>
/// <param name="GameMode">Display name of the mode that was played.</param>
/// <param name="Elapsed">Match duration in seconds.</param>
/// <param name="Standings">Every player, already ordered best-placed first.</param>
public sealed record MatchResult(
    MatchEndReason Reason,
    string GameMode,
    float Elapsed,
    IReadOnlyList<MatchStanding> Standings)
{
    /// <summary>
    /// Finds the standing for a peer, or <c>null</c> if that peer did not finish the
    /// match. Used for the per-player stats write.
    /// </summary>
    public MatchStanding? StandingFor(int peerId)
    {
        foreach (var standing in Standings)
        {
            if (standing.PeerId == peerId)
            {
                return standing;
            }
        }

        return null;
    }
}
