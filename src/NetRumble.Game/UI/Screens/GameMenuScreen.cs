using Microsoft.Xna.Framework;
using NetRumble.Game.Profile;
using NetRumble.Game.UI.Elements;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/screens/game_menu_screen.gd</c> (itself
/// <c>Game/Screens/GameMenuScreen</c>): the in-match pause popup. Offers Resume, Options
/// and Leave Match; back and the toggle-game-menu action both resume, exactly as
/// <c>ui_back_action</c> and <c>toggle_game_menu</c> did in the source.
/// </summary>
/// <remarks>
/// <para>
/// <b>Options is not a screen.</b> The GDScript swapped <c>NRMenuList</c>'s rows in place
/// rather than pushing a second panel, sharing <c>NROptionsRows</c> with the main menu.
/// That shared row builder has not been ported yet by the sibling agent responsible for
/// the options screen, so the rows below are this screen's own - the same settings, laid
/// out the same way, just not shared code. Once <c>NROptionsRows</c>' MonoGame
/// counterpart exists this should be pointed at it instead of duplicating the fields.
/// </para>
/// <para>
/// Settings apply live through <see cref="PlayerProfile"/>'s property setters, so leaving
/// the options rows is what commits them to disk - <see cref="ShowMenuRowsAfterOptions"/>
/// calls <see cref="PlayerProfile.Save"/> on the way back, matching the GDScript's
/// <c>_on_options_back</c> comment.
/// </para>
/// <para>
/// <b>Panel size must change before <see cref="MenuScreen.Rebuild"/> lays the list out,
/// not after.</b> <c>Rebuild</c> reads <see cref="PanelSize"/> to compute the list's
/// bounds and only then calls <see cref="BuildRows"/>; flipping <c>_inOptions</c> inside
/// <c>BuildRows</c> itself would change the panel's reported size one rebuild too late,
/// leaving the rows laid out against the previous, wrong-sized panel. Every transition
/// therefore goes through <see cref="ShowMenuRows"/>/<see cref="ShowOptionsRows"/>, which
/// set the size first and call <c>Rebuild</c> themselves.
/// </para>
/// </remarks>
public sealed class GameMenuScreen : MenuScreen
{
    private const string MenuTitle = "Menu";
    private const string OptionsTitle = "Options";

    private static readonly Point MenuPanelSize = new(620, 400);
    private static readonly Point OptionsPanelSize = new(780, 950);

    private bool _inOptions;
    private string _title = MenuTitle;
    private Point _panelSize = MenuPanelSize;

    public GameMenuScreen() => IsPopup = true;

    protected override string Title => _title;

    protected override Point PanelSize => _panelSize;

    /// <summary>Populates whichever row set <see cref="_inOptions"/> currently selects.</summary>
    protected override void BuildRows(UiContext context)
    {
        if (_inOptions)
        {
            PopulateOptionsRows(context);
        }
        else
        {
            PopulateMenuRows(context);
        }
    }

    private void ShowMenuRows(UiContext context)
    {
        _inOptions = false;
        _title = MenuTitle;
        _panelSize = MenuPanelSize;
        Rebuild(context);
    }

    private void ShowOptionsRows(UiContext context)
    {
        _inOptions = true;
        _title = OptionsTitle;
        _panelSize = OptionsPanelSize;
        Rebuild(context);
    }

    private void PopulateMenuRows(UiContext context)
    {
        List.AddButton("Resume", () => Manager.Pop());

        // XR-047. The in-match roster overlay names every player but takes no input, so
        // this is the route to their gamercards. Omitted when nobody in the match has
        // one, which is every practice match.
        var profiles = context.Screens.Find<GameplayScreen>()?.ProfileEntries ?? [];
        if (profiles.Count > 0)
        {
            List.AddButton("Players", () => context.Screens.Push(new ProfileList("Players", profiles)));
        }

        List.AddButton("Controls", () => context.Screens.Push(new ControlsScreen()));
        List.AddButton("Options", () => ShowOptionsRows(context));
        List.AddButton("Leave Match", () => OnLeave(context));
    }

    private void PopulateOptionsRows(UiContext context)
    {
        var profile = context.Profile;

        List.AddPercentSpinner("Master Volume", profile.MasterVolume, value =>
        {
            profile.MasterVolume = value;
            profile.MarkDirty();
        });

        List.AddPercentSpinner("Music Volume", profile.MusicVolume, value =>
        {
            profile.MusicVolume = value;
            profile.MarkDirty();
        });

        List.AddPercentSpinner("SFX Volume", profile.SfxVolume, value =>
        {
            profile.SfxVolume = value;
            profile.MarkDirty();
        });

        List.AddPercentSpinner("Voice Chat Volume", profile.VoiceChatVolume, value =>
        {
            profile.VoiceChatVolume = value;
            profile.MarkDirty();
        });

        List.AddBoolSpinner("Show Roster", profile.ShowRosterOverlay, value =>
        {
            profile.ShowRosterOverlay = value;
            profile.MarkDirty();
        });

        List.AddChoiceSpinner(
            "Voice Transcription",
            ["Platform Default", "On", "Off"],
            (int)profile.VoiceChatTranscriptionMode,
            index =>
            {
                profile.VoiceChatTranscriptionMode = (TranscriptionMode)index;
                profile.MarkDirty();
            });

        List.AddButton("Back", () => ShowMenuRowsAfterOptions(context));
    }

    /// <summary>
    /// Settings apply live, so leaving the rows is what commits them - matching the
    /// GDScript's own comment on <c>_on_options_back</c>.
    /// </summary>
    private void ShowMenuRowsAfterOptions(UiContext context)
    {
        context.Profile.Save();
        ShowMenuRows(context);
    }

    private static void OnLeave(UiContext context)
    {
        context.Screens.ShowDialog(
            "Leave Match",
            "Leave the current match?",
            DialogSeverity.Warning,
            showCancel: true,
            onDismissed: confirmed =>
            {
                if (!confirmed)
                {
                    return;
                }

                _ = LeaveAndReturnAsync(context);
            });
    }

    private static async Task LeaveAndReturnAsync(UiContext context)
    {
        await context.Platform.Party.LeaveAsync();
        context.Activity.Clear();
        await context.Activity.FlushAsync();
        context.Screens.ReplaceAll(new MainMenuScreen());
    }

    /// <summary>
    /// Both back and the pause button resume the match from the top-level rows, but only
    /// back out of the options rows one step at a time - the GDScript's
    /// <c>on_back_pressed</c> override and its <c>_unhandled_input</c> handler agreed on
    /// exactly this split.
    /// </summary>
    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (action is MenuAction.Back or MenuAction.GameMenu)
        {
            if (_inOptions)
            {
                ShowMenuRowsAfterOptions(context);
            }
            else
            {
                Manager.Pop();
            }

            return true;
        }

        return base.HandleAction(context, action);
    }
}
