using Microsoft.Xna.Framework;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_stt_overlay.gd</c> (itself <c>Game/UI/STTOverlay</c>): a
/// rolling transcript of chat lines shown over the match, each ageing out after a fixed
/// timeout.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript pre-created six <c>Label</c> slots so a burst of chat allocated nothing.
/// There is no scene tree to pre-allocate here, and a <see cref="List{T}"/> of at most six
/// short strings is not a cost worth engineering around, so this keeps only the lines
/// actually on screen.
/// </para>
/// <para>
/// <b>Deviation from the source.</b> The GDScript showed text chat unconditionally and
/// had no separate concept of a transcription toggle suppressing typed messages. This
/// port keeps that behaviour for typed lines and applies
/// <see cref="NetRumble.Game.Profile.PlayerProfile.IsVoiceChatTranscriptionEnabled"/> to
/// transcribed speech alone, which is the only line kind the setting is about. Lines are
/// recorded and aged regardless of the setting, so the strip picks up mid-conversation
/// the moment the player re-enables it.
/// </para>
/// </remarks>
public sealed class SttOverlay
{
    /// <summary><c>STTOverlay.h</c>'s <c>c_maxLines</c>.</summary>
    private const int MaxLines = 6;

    /// <summary><c>STTOverlay.h</c>'s <c>c_lineTimeout</c>, in seconds.</summary>
    private const float LineTimeout = 6.0f;

    private const float RowHeight = 34f;
    private const int RowLeft = 40;
    private const int RowTop = 620;
    private const int RowWidth = 1840;

    private readonly List<(string Text, float Age, ChatTextKind Kind)> _lines = [];

    /// <summary>
    /// Appends a line. An empty <paramref name="sender"/> renders the message alone, which
    /// is how the C++ surfaces ChatManager's "transcription unavailable" notice and how
    /// this port surfaces a chat-restriction reason with no attributable speaker.
    /// </summary>
    public void AddLine(string sender, string message, ChatTextKind kind = ChatTextKind.Typed)
    {
        var trimmed = message.Trim();

        if (trimmed.Length == 0)
        {
            return;
        }

        var text = sender.Length == 0 ? trimmed : $"{sender}: {trimmed}";
        _lines.Add((text, 0f, kind));

        // Keep only the most recent lines that fit on screen, oldest first - the same
        // trim the GDScript did with PackedStringArray.remove_at(0).
        while (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }
    }

    /// <summary>Ages every line and drops whatever has timed out.</summary>
    public void Update(UiContext context)
    {
        if (_lines.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _lines.Count; i++)
        {
            _lines[i] = (_lines[i].Text, _lines[i].Age + context.Delta, _lines[i].Kind);
        }

        // Ages only ever increase and lines are appended in order, so everything expired
        // is a prefix of the list - the GDScript's own justification for the same loop.
        while (_lines.Count > 0 && _lines[0].Age >= LineTimeout)
        {
            _lines.RemoveAt(0);
        }
    }

    public void Draw(UiContext context)
    {
        var transcription = context.Profile.IsVoiceChatTranscriptionEnabled;
        var row = 0;

        for (var i = 0; i < _lines.Count; i++)
        {
            // XR-003: only transcribed speech answers to the transcription setting. A
            // message another player typed has no other surface in the match, so hiding
            // it would mean the player was sent something they can never read.
            if (!transcription && _lines[i].Kind == ChatTextKind.VoiceTranscription)
            {
                continue;
            }

            var bounds = new Rectangle(RowLeft, (int)(RowTop + (RowHeight * row)), RowWidth, (int)RowHeight);
            UiTheme.TextLeft(
                context.Batch,
                context.Theme.Small,
                _lines[i].Text,
                new Vector2(bounds.X, bounds.Center.Y),
                UiTheme.Text);
            row++;
        }
    }
}
