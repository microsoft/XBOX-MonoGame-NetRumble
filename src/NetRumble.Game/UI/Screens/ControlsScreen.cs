using Microsoft.Xna.Framework;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// The control scheme, listed action by action for both a keyboard and a controller.
/// </summary>
/// <remarks>
/// <para>
/// Added rather than ported. The source had no controls page because its bindings were
/// never rebindable and were assumed to be known, but bug-bash players could not work
/// out how to fire: shooting is on the right stick, which nothing else in the game uses
/// and which no other screen names. <see cref="GameplayScreen"/> now shows a transient
/// reminder when a match starts, and this screen is the copy of it a player can go and
/// read at any time from either the main menu or the pause menu.
/// </para>
/// <para>
/// Read-only on purpose: the bindings live in <see cref="UiInput"/> and
/// <see cref="Gameplay.LocalInput"/>, and there is no rebinding to offer. The rows come
/// straight from <see cref="UiInput.ControlScheme"/> so the screen cannot drift away
/// from the bindings it documents.
/// </para>
/// </remarks>
public sealed class ControlsScreen : MenuScreen
{
    protected override string Title => "Controls";

    protected override Point PanelSize => new(960, 620);

    protected override void BuildRows(UiContext context)
    {
        List.BoxedRows = false;
        List.AddHeader("Keyboard / Controller");

        foreach (var (action, keyboard, gamepad) in UiInput.ControlScheme)
        {
            List.AddNote($"{action}:  {keyboard}  /  {gamepad}");
        }

        List.BoxedRows = true;
        List.AddButton("Back", () => OnBackPressed(context));
    }
}
