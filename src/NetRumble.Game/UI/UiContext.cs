using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Game.Audio;
using NetRumble.Game.Content;
using NetRumble.Game.Profile;
using NetRumble.Platform;

namespace NetRumble.Game.UI;

/// <summary>
/// Everything a widget or screen needs for one frame, passed down the draw and update
/// calls instead of being reached for through globals.
/// </summary>
/// <remarks>
/// The Godot original used autoloads - <c>ScreenManager</c>, <c>AudioManager</c>,
/// <c>Assets</c>, <c>NetManager</c>, <c>PlayerProfile</c> - which are singletons resolved by
/// name at any depth. Passing a context instead keeps the UI testable and, more usefully,
/// makes it explicit which services a given screen actually touches.
/// </remarks>
public sealed class UiContext
{
    public required SpriteBatch Batch { get; init; }

    public required UiTheme Theme { get; init; }

    public required AssetRegistry Assets { get; init; }

    public required AudioManager Audio { get; init; }

    public required ScreenManager Screens { get; init; }

    public required IPlatformProvider Platform { get; init; }

    /// <summary>
    /// The one owner of every Multiplayer Activity mutation and Recently Played With
    /// report (XR-064, XR-067). Screens declare intent through this; none of them call
    /// <see cref="IActivityService"/> directly, because unserialised activity writes can
    /// land out of order and resurrect a dead advertisement.
    /// </summary>
    public required ActivityCoordinator Activity { get; init; }

    /// <summary>
    /// Other players' gamerpics (XR-046). Optional everywhere it is used: a surface draws
    /// a picture if one has arrived and its plain text row if not.
    /// </summary>
    public required GamerPictureCache Pictures { get; init; }

    public required UiInput Input { get; init; }

    /// <summary>The local player's identity and persisted settings.</summary>
    public required PlayerProfile Profile { get; init; }

    /// <summary>
    /// Achievement reporting and the all-time stats behind the progressive ones.
    /// </summary>
    public required AchievementTracker Achievements { get; init; }

    /// <summary>The cloud tier for settings, over PlayFab Game Save.</summary>
    public required CloudSettingsSync CloudSettings { get; init; }

    /// <summary>Finished matches, for the Extras screen's Match History view.</summary>
    public required MatchHistoryStore MatchHistory { get; init; }

    /// <summary>
    /// A 1x1 white texture. Every solid rectangle in the front end is this stretched and
    /// tinted, which keeps the whole UI inside a single <see cref="SpriteBatch"/> without a
    /// dedicated shape renderer.
    /// </summary>
    public required Texture2D Pixel { get; init; }

    /// <summary>The root game object, for the handful of screens that quit or resize.</summary>
    public required NetRumbleGame Game { get; init; }

    /// <summary>Shared RNG, so screens do not each seed their own.</summary>
    public required Random Random { get; init; }

    /// <summary>
    /// The developer identity override from <c>--pf-user=</c> or <c>PF_CUSTOM_ID</c>, or
    /// null on a normal run. Every call to
    /// <see cref="NetRumble.Platform.IIdentityService.SignInAsync"/> must forward this, or
    /// the instance signs in anonymously as "Player" and the PlayFab custom-id path
    /// refuses to run for want of an id.
    /// </summary>
    public string? DeveloperCustomId { get; init; }

    /// <summary>
    /// The sign-in options every screen should use, so the developer override is carried
    /// consistently rather than remembered at each call site.
    /// </summary>
    public SignInOptions InteractiveSignIn =>
        SignInOptions.Interactive with { DeveloperCustomId = DeveloperCustomId };

    /// <summary>
    /// The full UI area, in design-resolution units. Screens lay out against this rather
    /// than the backbuffer, matching how Godot authored every control against the
    /// 1920x1080 viewport and let the stretch mode scale it.
    /// </summary>
    public Rectangle Screen { get; set; }
        = new(0, 0, NetRumbleGame.DesignWidth, NetRumbleGame.DesignHeight);

    /// <summary>Seconds since the previous frame.</summary>
    public float Delta { get; set; }

    /// <summary>Total elapsed seconds, for animation phases.</summary>
    public float Time { get; set; }
}
