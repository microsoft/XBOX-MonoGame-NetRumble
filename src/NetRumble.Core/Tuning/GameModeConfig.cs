namespace NetRumble.Core.Tuning;

/// <summary>
/// The match rules, ported from <c>scripts/gameplay/tuning/game_mode_config.gd</c>.
/// </summary>
/// <remarks>
/// Once a selectable game mode, now the rules of the only one there is. The class is kept
/// rather than folded into <see cref="TuningLibrary"/> as loose constants because the
/// rules travel together - score, clock and roster size are one coherent set - and the
/// <c>--match-seconds=</c> developer switch mutates the clock in place.
/// </remarks>
public sealed class GameModeConfig
{
    public string DisplayName { get; set; } = "Deathmatch";
    public string Description { get; set; } = string.Empty;

    // Rules

    public int PlayerCount { get; set; } = 4;
    public int TargetScore { get; set; } = 5;

    /// <summary>Match length in seconds.</summary>
    public float TimeLimit { get; set; } = 600.0f;
}
