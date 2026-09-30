namespace NetRumble.Core;

/// <summary>
/// A straight (non-premultiplied) 8-bit-per-channel colour.
/// </summary>
/// <remarks>
/// <para>
/// This assembly deliberately carries no MonoGame reference, so it cannot use
/// <c>Microsoft.Xna.Framework.Color</c>. The game layer converts at the boundary.
/// </para>
/// <para>
/// Bytes rather than floats: every colour in the source palette originated as an
/// 8-bit CSS colour that Godot stored as <c>component / 255</c>, so bytes round-trip
/// the original values exactly and avoid carrying repeating decimals around.
/// </para>
/// </remarks>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly RgbaColor White = new(255, 255, 255);

    /// <summary>
    /// Builds a colour from Godot's 0..1 float components.
    /// </summary>
    /// <remarks>
    /// The weapon and pickup tables were authored in GDScript, where every tint is a
    /// <c>Color(r, g, b)</c> literal. Converting at the call site would bury each table
    /// row in casts and obscure the balance numbers, which are the point of those tables.
    /// </remarks>
    public static RgbaColor FromFloat(float r, float g, float b, float a = 1.0f) => new(
        (byte)Math.Clamp((int)MathF.Round(r * 255.0f), 0, 255),
        (byte)Math.Clamp((int)MathF.Round(g * 255.0f), 0, 255),
        (byte)Math.Clamp((int)MathF.Round(b * 255.0f), 0, 255),
        (byte)Math.Clamp((int)MathF.Round(a * 255.0f), 0, 255));

    /// <summary>Packs to 0xAABBGGRR, the layout MonoGame's <c>Color.PackedValue</c> uses.</summary>
    public uint ToRgba() => (uint)(R | (G << 8) | (B << 16) | (A << 24));
}
