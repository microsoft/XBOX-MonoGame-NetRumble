namespace NetRumble.Core.Tuning;

/// <summary>
/// The selectable ship tints and their display names, ported from
/// <c>scripts/gameplay/tuning/player_palette.gd</c>.
/// </summary>
/// <remarks>
/// <see cref="Colors"/> and <see cref="Names"/> are parallel arrays; keep them the same
/// length.
/// </remarks>
public sealed class PlayerPalette
{
    public RgbaColor[] Colors { get; set; } =
    [
        new(255, 0, 0),       // Red
        new(255, 165, 0),     // Orange
        new(255, 255, 0),     // Yellow
        new(173, 255, 47),    // Green Yellow
        new(0, 128, 0),       // Green
        new(0, 0, 255),       // Blue
        new(138, 43, 226),    // Blue Violet
        new(75, 0, 130),      // Indigo
    ];

    public string[] Names { get; set; } =
    [
        "Red",
        "Orange",
        "Yellow",
        "Green Yellow",
        "Green",
        "Blue",
        "Blue Violet",
        "Indigo",
    ];

    /// <summary>Number of selectable ship colours.</summary>
    public int Size => Colors.Length;

    /// <summary>
    /// Colour for an id, wrapping out-of-range ids so a stale saved colour id can never
    /// crash the game.
    /// </summary>
    /// <remarks>Uses Godot <c>posmod</c> semantics, so negative ids wrap rather than throw.</remarks>
    public RgbaColor ColorAt(int index)
        => Colors.Length == 0 ? RgbaColor.White : Colors[PosMod(index, Colors.Length)];

    /// <summary>Display name for an id, wrapping as <see cref="ColorAt"/> does.</summary>
    public string NameAt(int index)
        => Names.Length == 0 ? string.Empty : Names[PosMod(index, Names.Length)];

    private static int PosMod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
}
