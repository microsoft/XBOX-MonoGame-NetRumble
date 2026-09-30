using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Core;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_roster_overlay.gd</c> (itself
/// <c>Game/UI/Gameplay/GameplayRosterOverlay</c>): the Discord-style in-match player list -
/// name, score and mic-state icon - anchored top-left over the HUD.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript rebuilt its row <c>Control</c> tree only when the roster or a chat
/// indicator changed, because instantiating nodes is not free. This port has no node
/// tree - it is measured and drawn straight from <see cref="Draw"/> every frame - so
/// there is nothing to cache and no rebuild step to trigger; a match roster is at most
/// eight players, which is cheap to lay out from scratch every frame.
/// </para>
/// <para>
/// The column layout is ported faithfully, though: the score column sits just past the
/// longest name rather than at a fixed offset, so the panel hugs its content the way the
/// original's <c>_apply_columns</c> did once it stopped assuming a fixed-width name.
/// </para>
/// </remarks>
public sealed class RosterOverlay
{
    /// <summary>Godot's <c>MAX_PLAYERS</c> was 8, and briefly 16 here while Big Team
    /// Battle existed. A match is four players, so four rows is the whole roster.</summary>
    private const int MaxPlayers = 4;

    private const float RowHeight = 36f;
    private const float IconSize = 24f;
    private const float IconNameGap = 8f;

    /// <summary>
    /// The absolute width the C++ original's fixed score column implied. Kept as the
    /// ceiling the roster may never exceed, per the GDScript's own comment on
    /// <c>MAX_ROW_WIDTH</c>.
    /// </summary>
    private const float MaxRowWidth = 280f;

    private const float NameScoreGap = 16f;
    private const float MinScoreWidth = 24f;
    private const float MaxScoreWidth = 64f;

    private const float PanelPaddingX = 12f;
    private const float PanelPaddingY = 6f;

    private const float NameX = IconSize + IconNameGap;

    /// <summary>
    /// Draws the roster at <paramref name="anchor"/>, top-left, sized to fit
    /// <paramref name="players"/>. Does nothing when the overlay is turned off in
    /// settings or the roster is empty, matching the GDScript's <c>show_roster_overlay</c>
    /// gate and its <c>self_modulate.a = 0</c> for an empty match.
    /// </summary>
    public void Draw(UiContext context, Point anchor, IReadOnlyList<PlayerState> players)
    {
        if (!context.Profile.ShowRosterOverlay || players.Count == 0)
        {
            return;
        }

        var shown = Math.Min(players.Count, MaxPlayers);

        var nameWidth = 0f;
        var scoreWidth = MinScoreWidth;

        for (var i = 0; i < shown; i++)
        {
            nameWidth = MathF.Max(nameWidth, context.Theme.Small.MeasureString(players[i].DisplayName).X);
            scoreWidth = MathF.Max(scoreWidth, context.Theme.Small.MeasureString(players[i].Score.ToString()).X);
        }

        scoreWidth = MathF.Min(scoreWidth, MaxScoreWidth);
        nameWidth = Math.Clamp(nameWidth, 0f, MaxRowWidth - NameX - NameScoreGap - scoreWidth);

        var rowWidth = NameX + nameWidth + NameScoreGap + scoreWidth;
        var panel = new Rectangle(
            anchor.X,
            anchor.Y,
            (int)MathF.Ceiling(rowWidth + (PanelPaddingX * 2f)),
            (int)MathF.Ceiling((RowHeight * shown) + (PanelPaddingY * 2f)));

        context.Theme.Box(context.Batch, panel, UiTheme.OverlayBackground, UiTheme.OverlayBorder);

        for (var i = 0; i < shown; i++)
        {
            var player = players[i];
            var rowTop = panel.Y + PanelPaddingY + (RowHeight * i);
            var midY = rowTop + (RowHeight / 2f);

            var indicator = context.Platform.Party.GetChatIndicator(player.PeerId);

            if (indicator != ChatIndicator.None)
            {
                var iconKey = indicator switch
                {
                    ChatIndicator.Muted => "Microphone_Muted",
                    ChatIndicator.Talking => "Microphone_Talking",
                    _ => "Microphone_Available",
                };

                // The muted icon reads as a warning; every other state reuses the theme's
                // periwinkle tint, matching the GDScript's palette lookup with the same
                // fallback colour.
                var tint = indicator == ChatIndicator.Muted
                    ? UiTheme.DialogBorderError
                    : Color.FromNonPremultiplied(189, 199, 199, 168);

                var iconBounds = new Rectangle(
                    (int)(panel.X + PanelPaddingX),
                    (int)(rowTop + ((RowHeight - IconSize) / 2f)),
                    (int)IconSize,
                    (int)IconSize);

                context.Batch.Draw(context.Assets.Texture(iconKey), iconBounds, tint);
            }

            var nameLeft = panel.X + PanelPaddingX + NameX;
            UiTheme.TextLeft(
                context.Batch,
                context.Theme.Small,
                Truncate(context.Theme.Small, player.DisplayName, nameWidth),
                new Vector2(nameLeft, midY),
                UiTheme.Text);

            var scoreRight = panel.X + PanelPaddingX + rowWidth;
            UiTheme.TextRight(
                context.Batch,
                context.Theme.Small,
                player.Score.ToString(),
                new Vector2(scoreRight, midY),
                UiTheme.Accent);
        }
    }

    /// <summary>
    /// Ellipsises a name wider than its column, replacing Godot's
    /// <c>OVERRUN_TRIM_ELLIPSIS</c>. SpriteFont has no built-in equivalent, so this trims
    /// one character at a time - cheap enough for an eight-row roster.
    /// </summary>
    private static string Truncate(SpriteFont font, string text, float maxWidth)
    {
        if (font.MeasureString(text).X <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "...";

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + ellipsis;

            if (font.MeasureString(candidate).X <= maxWidth)
            {
                return candidate;
            }
        }

        return ellipsis;
    }
}
