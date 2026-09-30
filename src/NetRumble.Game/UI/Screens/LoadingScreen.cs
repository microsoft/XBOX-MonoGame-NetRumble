using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/screens/loading_screen.gd</c> - itself <c>Game/Screens/LoadingScreen</c>:
/// five concentric rings spinning at alternating rates behind a static caption.
/// </summary>
/// <remarks>
/// <b>The trailing-dots caption animation is dropped.</b> <c>LoadingScreen::Update</c>
/// also cycled "Loading", "Loading.", "Loading..", but the GDScript's own comment says
/// the rotating rings already carry that "work is in progress" signal, so it never
/// ported that half either. Reproducing dots the rings already imply would be adding
/// behaviour the source deliberately left out, not porting it.
/// </remarks>
public sealed class LoadingScreen : Screen
{
    /// <summary>
    /// One entry per ring: its radius, initial rotation and angular velocity (rad/s),
    /// lifted straight from <c>loading_screen.tscn</c> and <c>RING_SPEEDS</c>.
    /// </summary>
    /// <remarks>
    /// The speeds alternate direction and grow by <c>speed * speed</c> each step
    /// (0.1 -> 0.11 -> 0.1221 -> 0.137008 -> 0.155779), which is
    /// <c>LoadingScreen::Update</c>'s own progression, not an arbitrary embellishment -
    /// do not "simplify" this into five equal, same-direction rings.
    /// </remarks>
    private static readonly (float Radius, float InitialRotation, float Speed, Color Tint)[] Rings =
    [
        (88.178f, 0.05f, 0.1f, Color.FromNonPremultiplied(5, 51, 33, 168)),
        (112.867f, 0.075f, -0.11f, Color.FromNonPremultiplied(10, 92, 59, 168)),
        (144.47f, 0.1f, 0.1221f, Color.FromNonPremultiplied(18, 133, 87, 168)),
        (184.922f, 0.125f, -0.137008f, Color.FromNonPremultiplied(23, 173, 112, 168)),
        (236.7f, 0.15f, 0.155779f, Color.FromNonPremultiplied(31, 212, 138, 168)),
    ];

    private readonly float[] _rotations = new float[Rings.Length];

    public LoadingScreen(string message)
    {
        Message = message;
        AllowBack = false;

        for (var i = 0; i < Rings.Length; i++)
        {
            _rotations[i] = Rings[i].InitialRotation;
        }
    }

    /// <summary>The caption. Public so a caller can retarget an already-pushed screen -
    /// the lobby swaps this between "Creating match" and "Joining match" rather than
    /// pushing a fresh screen for each, which would otherwise flash the panel.</summary>
    public string Message { get; set; }

    public override void Update(UiContext context)
    {
        for (var i = 0; i < Rings.Length; i++)
        {
            _rotations[i] += Rings[i].Speed * context.Delta;
        }
    }

    public override void Draw(UiContext context)
    {
        // The screen's own backdrop, matching loading_screen.tscn's BackgroundFill.
        // This is pushed (not IsPopup), so nothing beneath is visible anyway, but
        // filling explicitly keeps the screen self-contained if that ever changes.
        context.Theme.Fill(context.Batch, context.Screen, UiTheme.ScreenBackground);

        var ring = context.Assets.Texture("Loading_Ring");
        var origin = new Vector2(ring.Width / 2f, ring.Height / 2f);

        // RingsRoot and MessageLabel are both anchored to the screen centre in the
        // source, so the caption sits inside the rings rather than below them.
        var centre = new Vector2(context.Screen.Center.X, context.Screen.Center.Y);

        for (var i = 0; i < Rings.Length; i++)
        {
            var scale = (Rings[i].Radius * 2f) / ring.Width;
            context.Batch.Draw(
                ring, centre, null, Rings[i].Tint, _rotations[i], origin, scale, SpriteEffects.None, 0f);
        }

        UiTheme.TextCentre(context.Batch, context.Theme.Display, Message, centre, UiTheme.Text);
    }
}
