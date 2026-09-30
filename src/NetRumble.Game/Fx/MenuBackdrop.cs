using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Game.UI;

namespace NetRumble.Game.Fx;

/// <summary>
/// The flat fill plus drifting starfield that <c>acquire_user_screen.tscn</c> and
/// <c>lobby_screen.tscn</c> both put behind their contents, as a
/// <c>BackgroundFill</c> <c>ColorRect</c> with a <c>StarfieldBackground</c> instanced
/// over it.
/// </summary>
/// <remarks>
/// <para>
/// This has to run in the pre-UI world pass rather than inside the UI batch:
/// <see cref="Starfield.Draw"/> opens its own <see cref="SpriteBatch"/> with point
/// sampling and no letterbox translation, because a star must stay exactly one design
/// pixel at any window size. Filling the backdrop from the UI batch instead would paint
/// over the stars, since the UI batch runs afterwards.
/// </para>
/// <para>
/// The camera is fixed at the origin. In the source these menus instance the same
/// <c>StarfieldBackground</c> the world uses but never move a camera through it, so only
/// the slow parallax drift is visible - which is the whole of the effect on a menu.
/// </para>
/// </remarks>
public sealed class MenuBackdrop
{
    private readonly Starfield _starfield = new();

    public void Update(float totalSeconds) => _starfield.Update(totalSeconds);

    public void Draw(UiContext context, float renderScale, int viewportWidth, int viewportHeight)
    {
        // The device clear is the world's near-black blue; these screens want the menu
        // backdrop colour, so it is painted over the whole viewport first.
        context.Batch.Begin(blendState: BlendState.AlphaBlend, samplerState: SamplerState.PointClamp);
        context.Batch.Draw(
            context.Pixel, new Rectangle(0, 0, viewportWidth, viewportHeight), UiTheme.ScreenBackground);
        context.Batch.End();

        _starfield.Draw(
            context.Batch, context.Pixel, Vector2.Zero, renderScale, viewportWidth, viewportHeight);
    }
}
