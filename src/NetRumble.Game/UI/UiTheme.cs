using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.UI;

/// <summary>
/// The palette, fonts and box-drawing primitives of <c>assets/ui/netrumble.tres</c>.
/// </summary>
/// <remarks>
/// <para>
/// Godot's <c>Theme</c> is a lookup of <c>StyleBox</c> resources keyed by control type and
/// "theme type variation". MonoGame has no equivalent, so the styles collapse into named
/// colours plus <see cref="Box"/>, which draws the one shape every <c>StyleBoxFlat</c> in
/// the theme actually used: a filled rectangle with an optional border.
/// </para>
/// <para>
/// <b>Colours are converted from straight to premultiplied on the way in.</b> The theme
/// stores ordinary colours with an alpha; the frame is drawn premultiplied. Every
/// translucent constant below therefore goes through
/// <see cref="Color.FromNonPremultiplied(int,int,int,int)"/> rather than being written as a
/// literal, so the numbers stay readable against the <c>.tres</c> they came from.
/// </para>
/// </remarks>
public sealed class UiTheme
{
    /// <summary>The green used for focus, borders and progress fill. <c>(0.12, 0.83, 0.54)</c>.</summary>
    public static readonly Color Accent = new(31, 212, 138);

    /// <summary>
    /// The pale grey-green every panel and button tints with. <c>(0.74, 0.78, 0.78)</c>.
    /// </summary>
    public static readonly Color Neutral = new(189, 199, 199);

    public static readonly Color Text = Color.White;

    public static readonly Color TextDisabled = new(153, 153, 153);

    /// <summary>Button <c>font_pressed_color</c>, <c>(0.48, 0.95, 0.73)</c>.</summary>
    public static readonly Color TextPressed = new(122, 242, 186);

    public static readonly Color ButtonNormal = Color.FromNonPremultiplied(189, 199, 199, 46);

    public static readonly Color ButtonHover = Color.FromNonPremultiplied(189, 199, 199, 97);

    public static readonly Color ButtonPressed = Color.FromNonPremultiplied(31, 212, 138, 89);

    public static readonly Color ButtonDisabled = Color.FromNonPremultiplied(189, 199, 199, 18);

    public static readonly Color DialogPanel = Color.FromNonPremultiplied(10, 13, 13, 242);

    public static readonly Color DialogBorder = Color.FromNonPremultiplied(189, 199, 199, 168);

    /// <summary>Dialog title bar, <c>NRPalette/panel_dark</c> <c>(0.05, 0.22, 0.15)</c>.</summary>
    public static readonly Color DialogTitle = new(13, 56, 38);

    public static readonly Color DialogTitleError = new(115, 15, 26);

    /// <summary>
    /// Warning shares <c>panel_dark</c> with the normal title in the current theme; only
    /// the border differs (<see cref="Accent"/> vs <see cref="DialogBorderError"/>).
    /// </summary>
    public static readonly Color DialogTitleWarning = new(13, 56, 38);

    public static readonly Color DialogBorderError = new(255, 69, 69);

    /// <summary>The chat log's slab, <c>(0.02, 0.03, 0.03, 0.85)</c>.</summary>
    public static readonly Color PanelBackground = Color.FromNonPremultiplied(5, 8, 8, 217);

    /// <summary>The roster overlay's translucent slab, <c>(0.02, 0.03, 0.03, 0.55)</c>.</summary>
    public static readonly Color OverlayBackground = Color.FromNonPremultiplied(5, 8, 8, 140);

    public static readonly Color OverlayBorder = Color.FromNonPremultiplied(189, 199, 199, 89);

    /// <summary>
    /// The flat backdrop every screen fills before drawing, <c>BackgroundFill</c>'s
    /// <c>(0.02, 0.03, 0.03)</c>. Every screen in the source uses this one colour.
    /// </summary>
    public static readonly Color ScreenBackground = new(5, 8, 8);

    /// <summary>
    /// The tint the lobby's panel textures are modulated by, <c>(0.74, 0.78, 0.78, 0.66)</c>.
    /// </summary>
    public static readonly Color PanelTexture = Color.FromNonPremultiplied(189, 199, 199, 168);

    public static readonly Color ProgressBackground = Color.FromNonPremultiplied(31, 31, 36, 217);

    public static readonly Color HealthFill = new(217, 56, 56);

    public static readonly Color ShieldFill = new(64, 140, 242);

    /// <summary>
    /// Row height shared by buttons and spinners, from <c>NRMenuList._ROW_HEIGHT</c>.
    /// </summary>
    public const int RowHeight = 54;

    public UiTheme(
        Texture2D pixel,
        SpriteFont small,
        SpriteFont body,
        SpriteFont panelHeader,
        SpriteFont button,
        SpriteFont subtitle,
        SpriteFont title,
        SpriteFont display)
    {
        Pixel = pixel;
        Small = small;
        Body = body;
        PanelHeader = panelHeader;
        Button = button;
        Subtitle = subtitle;
        Title = title;
        Display = display;
    }

    /// <summary>1x1 white texture; every solid shape in the UI is this, stretched.</summary>
    public Texture2D Pixel { get; }

    /// <summary>20 px. HUD detail and dense list rows.</summary>
    public SpriteFont Small { get; }

    /// <summary>26 px, the theme's <c>default_font_size</c>.</summary>
    public SpriteFont Body { get; }

    /// <summary>29 px, <c>LobbyPanelHeader</c> - the lobby's section captions.</summary>
    public SpriteFont PanelHeader { get; }

    /// <summary>32 px, the theme's <c>Button/font_sizes/font_size</c>.</summary>
    public SpriteFont Button { get; }

    /// <summary>38 px, <c>DialogBoxTitleLabel</c>.</summary>
    public SpriteFont Subtitle { get; }

    /// <summary>51 px, <c>DialogTitleLabel</c> and screen titles.</summary>
    public SpriteFont Title { get; }

    /// <summary>
    /// 64 px, the loading screen's <c>MessageLabel</c> font-size override - the one
    /// place the source steps outside the theme's declared sizes.
    /// </summary>
    public SpriteFont Display { get; }

    /// <summary>Fills a rectangle.</summary>
    public void Fill(SpriteBatch batch, Rectangle bounds, Color color)
        => batch.Draw(Pixel, bounds, color);

    /// <summary>Strokes a rectangle inward, the way a <c>StyleBoxFlat</c> border draws.</summary>
    public void Stroke(SpriteBatch batch, Rectangle bounds, Color color, int width = 2)
    {
        if (width <= 0 || bounds.Height <= width * 2)
        {
            return;
        }

        batch.Draw(Pixel, new Rectangle(bounds.X, bounds.Y, bounds.Width, width), color);
        batch.Draw(Pixel, new Rectangle(bounds.X, bounds.Bottom - width, bounds.Width, width), color);
        batch.Draw(Pixel, new Rectangle(bounds.X, bounds.Y + width, width, bounds.Height - (width * 2)), color);
        batch.Draw(
            Pixel,
            new Rectangle(bounds.Right - width, bounds.Y + width, width, bounds.Height - (width * 2)),
            color);
    }

    /// <summary>A filled rectangle with an optional border - the whole of <c>StyleBoxFlat</c>.</summary>
    public void Box(SpriteBatch batch, Rectangle bounds, Color fill, Color? border = null, int borderWidth = 2)
    {
        Fill(batch, bounds, fill);

        if (border is { } stroke)
        {
            Stroke(batch, bounds, stroke, borderWidth);
        }
    }

    /// <summary>
    /// Draws a texture scaled to fit <paramref name="bounds"/> with its aspect ratio
    /// preserved and centred inside it - Godot's <c>TextureRect</c> with
    /// <c>expand_mode = 1</c> and <c>stretch_mode = 5</c>
    /// (<c>STRETCH_KEEP_ASPECT_CENTERED</c>), which every logo and panel in the source
    /// uses. Stretching to the raw rect instead distorts the logos, because their
    /// authored rects are not the same aspect as the source art.
    /// </summary>
    public static void TextureFit(SpriteBatch batch, Texture2D texture, Rectangle bounds, Color tint)
    {
        var scale = MathF.Min(bounds.Width / (float)texture.Width, bounds.Height / (float)texture.Height);
        var width = (int)MathF.Round(texture.Width * scale);
        var height = (int)MathF.Round(texture.Height * scale);

        batch.Draw(
            texture,
            new Rectangle(
                bounds.X + ((bounds.Width - width) / 2),
                bounds.Y + ((bounds.Height - height) / 2),
                width,
                height),
            tint);
    }

    /// <summary>Draws text with its left edge at <paramref name="position"/>, vertically centred.</summary>
    public static void TextLeft(SpriteBatch batch, SpriteFont font, string text, Vector2 position, Color color)
    {
        var size = font.MeasureString(text);
        batch.DrawString(font, text, new Vector2(position.X, position.Y - (size.Y / 2f)), color);
    }

    /// <summary>Draws text centred on <paramref name="position"/> in both axes.</summary>
    public static void TextCentre(SpriteBatch batch, SpriteFont font, string text, Vector2 position, Color color)
    {
        var size = font.MeasureString(text);
        batch.DrawString(font, text, position - (size / 2f), color);
    }

    /// <summary>Draws text with its right edge at <paramref name="position"/>.</summary>
    public static void TextRight(SpriteBatch batch, SpriteFont font, string text, Vector2 position, Color color)
    {
        var size = font.MeasureString(text);
        batch.DrawString(font, text, new Vector2(position.X - size.X, position.Y - (size.Y / 2f)), color);
    }

    /// <summary>
    /// Greedily word-wraps <paramref name="text"/> to <paramref name="maxWidth"/>.
    /// </summary>
    /// <remarks>
    /// Replaces Godot's <c>autowrap_mode</c>. Explicit newlines in the source string are
    /// honoured, because several dialog messages use them to separate a summary from a
    /// detail line.
    /// </remarks>
    public static List<string> Wrap(SpriteFont font, string text, float maxWidth)
    {
        var lines = new List<string>();

        foreach (var paragraph in text.Split('\n'))
        {
            var current = string.Empty;

            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;

                if (font.MeasureString(candidate).X <= maxWidth || current.Length == 0)
                {
                    current = candidate;
                    continue;
                }

                lines.Add(current);
                current = word;
            }

            lines.Add(current);
        }

        return lines;
    }
}
