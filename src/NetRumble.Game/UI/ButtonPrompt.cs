using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Game.Content;

namespace NetRumble.Game.UI;

/// <summary>
/// Draws a prompt line in which controller buttons appear as their icons rather than as
/// letters in brackets.
/// </summary>
/// <remarks>
/// <para>
/// Xbox certification requires button references in the UI to be the button icons, not
/// text: "[X] Ready" has to read as the X icon followed by the word. The port inherited
/// the bracket form from the Godot original, which had the same requirement and the same
/// gap.
/// </para>
/// <para>
/// A prompt is therefore written as ordinary text with <c>{X}</c>-style tokens - see
/// <see cref="UiInput.LobbyHint"/> - and rendered here by walking the string, drawing the
/// runs between tokens as text and each token as its texture. Keeping the tokens inside
/// the string rather than splitting prompts into structured parts means the keyboard
/// variants stay exactly what they were: a string with no tokens is measured and drawn
/// identically to <see cref="UiTheme.TextCentre"/>, so the desktop build is unaffected.
/// </para>
/// <para>
/// <b>The icons are placeholders.</b> A shipping title must use Microsoft's own
/// controller art from the Xbox Design Guide, which cannot be redistributed here. See
/// <c>tools/New-PlaceholderGlyphs.ps1</c>: replacing the PNGs with the official files of
/// the same names is the whole of that change, because this code addresses them by asset
/// key and sizes them from the font rather than from the art.
/// </para>
/// </remarks>
public static class ButtonPrompt
{
    /// <summary>
    /// Token to asset key. The names are the ones Xbox uses for the buttons, so a prompt
    /// string reads the way the button is labelled.
    /// </summary>
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "Controller_A",
        ["B"] = "Controller_B",
        ["X"] = "Controller_X",
        ["Y"] = "Controller_Y",
        ["LB"] = "Controller_BumperLeft",
        ["RB"] = "Controller_BumperRight",
        ["MENU"] = "Controller_Menu",
        ["VIEW"] = "Controller_View",
    };

    /// <summary>
    /// How much of the line height an icon occupies. Slightly over 1 because a disc with
    /// a letter knocked out of it reads smaller than a capital of the same height.
    /// </summary>
    private const float GlyphScale = 1.15f;

    /// <summary>Gap either side of an icon, as a fraction of the line height.</summary>
    private const float GlyphPadding = 0.12f;

    /// <summary>Width and height of the prompt as it will be drawn.</summary>
    public static Vector2 Measure(AssetRegistry assets, SpriteFont font, string text)
    {
        var width = 0f;
        var height = font.LineSpacing * 1f;

        foreach (var segment in Parse(text))
        {
            width += segment.IsGlyph
                ? GlyphWidth(assets, font, segment.Value)
                : font.MeasureString(segment.Value).X;
        }

        return new Vector2(width, MathF.Max(height, font.LineSpacing * GlyphScale));
    }

    /// <summary>Draws the prompt centred on <paramref name="position"/> in both axes.</summary>
    public static void DrawCentre(
        SpriteBatch batch,
        AssetRegistry assets,
        SpriteFont font,
        string text,
        Vector2 position,
        Color color)
    {
        var size = Measure(assets, font, text);
        DrawLeft(batch, assets, font, text, new Vector2(position.X - (size.X / 2f), position.Y), color);
    }

    /// <summary>
    /// Draws the prompt with its left edge at <paramref name="position"/>, vertically
    /// centred on it - the same convention as <see cref="UiTheme.TextLeft"/>.
    /// </summary>
    public static void DrawLeft(
        SpriteBatch batch,
        AssetRegistry assets,
        SpriteFont font,
        string text,
        Vector2 position,
        Color color)
    {
        var x = position.X;

        foreach (var segment in Parse(text))
        {
            if (!segment.IsGlyph)
            {
                var size = font.MeasureString(segment.Value);
                batch.DrawString(font, segment.Value, new Vector2(x, position.Y - (size.Y / 2f)), color);
                x += size.X;
                continue;
            }

            var texture = Texture(assets, segment.Value);

            if (texture is null)
            {
                // An unknown token, or art that failed to load, falls back to the bracket
                // form rather than vanishing: a prompt that names no button is worse than
                // an ugly one.
                var literal = $"[{segment.Value}]";
                var size = font.MeasureString(literal);
                batch.DrawString(font, literal, new Vector2(x, position.Y - (size.Y / 2f)), color);
                x += size.X;
                continue;
            }

            var extent = font.LineSpacing * GlyphScale;
            var padding = font.LineSpacing * GlyphPadding;

            // Square, so the disc stays a disc whatever the source art's aspect is.
            batch.Draw(
                texture,
                new Rectangle(
                    (int)MathF.Round(x + padding),
                    (int)MathF.Round(position.Y - (extent / 2f)),
                    (int)MathF.Round(extent),
                    (int)MathF.Round(extent)),
                color);

            x += GlyphWidth(assets, font, segment.Value);
        }
    }

    private static float GlyphWidth(AssetRegistry assets, SpriteFont font, string token)
        => Texture(assets, token) is null
            ? font.MeasureString($"[{token}]").X
            : (font.LineSpacing * GlyphScale) + (font.LineSpacing * GlyphPadding * 2f);

    private static Texture2D? Texture(AssetRegistry assets, string token)
    {
        if (!Glyphs.TryGetValue(token, out var key))
        {
            return null;
        }

        try
        {
            return assets.Texture(key);
        }
        catch (Exception)
        {
            // Missing content must not take a menu down - the bracket fallback covers it.
            return null;
        }
    }

    /// <summary>
    /// Splits the prompt into literal runs and glyph tokens.
    /// </summary>
    /// <remarks>
    /// An unclosed or empty brace is treated as literal text, so a prompt is never
    /// silently truncated by a typo; only a well-formed <c>{TOKEN}</c> becomes an icon.
    /// </remarks>
    private static IEnumerable<Segment> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf('{', index);

            if (open < 0)
            {
                yield return new Segment(text[index..], false);
                yield break;
            }

            var close = text.IndexOf('}', open + 1);

            if (close < 0 || close == open + 1)
            {
                yield return new Segment(text[index..], false);
                yield break;
            }

            if (open > index)
            {
                yield return new Segment(text[index..open], false);
            }

            yield return new Segment(text[(open + 1)..close], true);
            index = close + 1;
        }
    }

    private readonly record struct Segment(string Value, bool IsGlyph);
}
