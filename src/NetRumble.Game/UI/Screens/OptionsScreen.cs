using NetRumble.Game.Profile;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_options_rows.gd</c> (<c>NROptionsRows</c>), itself
/// <c>Game/Screens/OptionsMenuScreen</c> plus its per-category subclasses.
/// </summary>
/// <remarks>
/// <para>
/// The C++ sample needed a <c>Screen</c> subclass per settings category because each one
/// owned its own <c>UIElement</c> tree; the GDScript collapsed all of them into one
/// static row-builder appended into whichever list opened it (the main menu's or the
/// in-match pause panel's), because the rows differ only in which ones a page appends
/// to a shared list. This screen keeps that same shape, minus the "appended into
/// someone else's list" part: the main menu now reaches Options through a normal
/// <see cref="ScreenManager.Push{T}"/> rather than an in-place row swap, so this is a
/// screen of its own after all - but the row order and behaviour below is a straight
/// port of <c>NROptionsRows.populate()</c>.
/// </para>
/// <para>
/// Controls are not represented, matching the source: input bindings live in the input
/// map and were never rebindable, so that page had nothing to offer.
/// </para>
/// <para>
/// <b>Graphics is desktop-only.</b> The category held one row, Fullscreen, and a console
/// has no window mode to choose - the platform owns the display. On the GDKX build the
/// row and its header are compiled out, so Options opens straight into Audio rather than
/// showing a control that cannot do anything.
/// </para>
/// <para>
/// <b>Settings apply live.</b> Every row writes straight to <see cref="Profile.PlayerProfile"/>
/// and calls <see cref="Profile.PlayerProfile.MarkDirty"/>, exactly as the source did, so a
/// player scrubbing the volume sliders hears the change immediately. <see cref="OnBackPressed"/>
/// is what actually persists it to disk, mirroring <c>NROptionsRows.save()</c> being the
/// host's job, not any individual row's.
/// </para>
/// </remarks>
public sealed class OptionsScreen : MenuScreen
{
    protected override string Title => "Options";

    protected override void BuildRows(UiContext context)
    {
        var profile = context.Profile;

#if !GDKX
        // A console title has no window mode to offer: the platform owns the display and
        // the game is always full screen, so the row and its Graphics header are compiled
        // out rather than shown as a control that cannot do anything.
        List.AddHeader("Graphics");
        List.AddBoolSpinner("Fullscreen", profile.Fullscreen, value =>
        {
            profile.Fullscreen = value;
            profile.MarkDirty();

            // The source applied the window mode the instant the row changed rather
            // than waiting for Back, so the player sees the switch happen under their
            // hands instead of only on the way out.
            context.Game.ApplyDisplaySettings();
        });
#endif

        // AudioManager reads master/music/sfx back out of the profile on MarkDirty(),
        // so these three are audible mid-drag with no extra wiring here.
        List.AddHeader("Audio");
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
        List.AddPercentSpinner("Sound Effects Volume", profile.SfxVolume, value =>
        {
            profile.SfxVolume = value;
            profile.MarkDirty();
        });
        List.AddPercentSpinner("Voice Chat Volume", profile.VoiceChatVolume, value =>
        {
            profile.VoiceChatVolume = value;
            profile.MarkDirty();
        });

        List.AddHeader("Gameplay");
        List.AddBoolSpinner("Show Roster Overlay", profile.ShowRosterOverlay, value =>
        {
            profile.ShowRosterOverlay = value;
            profile.MarkDirty();
        });

        List.AddHeader("Accessibility");

        // AccessibilityOptionsScreen.cpp offers a three-way "Use Xbox Setting / On /
        // Off" only when the platform exposes a speech-to-text accessibility setting to
        // read; otherwise it falls back to the two-entry On/Off list the source always
        // uses here, because this SDK has no such setting to consult.
        List.AddChoiceSpinner(
            "Voice Chat Transcription",
            ["On", "Off"],
            profile.IsVoiceChatTranscriptionEnabled ? 0 : 1,
            index =>
            {
                profile.VoiceChatTranscriptionMode =
                    index == 0 ? TranscriptionMode.Enabled : TranscriptionMode.Disabled;
                profile.MarkDirty();
            });

        List.AddButton("Back", () => OnBackPressed(context));
    }

    /// <summary>
    /// Persists whatever the live rows changed and re-applies the window mode, then
    /// pops - mirroring <c>NROptionsRows.save()</c> being the host screen's
    /// responsibility rather than any individual row's.
    /// </summary>
    public override void OnBackPressed(UiContext context)
    {
        context.Profile.Save();
        context.Game.ApplyDisplaySettings();
        base.OnBackPressed(context);
    }
}
