using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game;

/// <summary>
/// Saves the game's own back buffer to a PNG at requested times.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the port had never been looked at. Every check up to this point proved
/// the game ran; none of them proved it drew anything, and a black window satisfies all
/// of them.
/// </para>
/// <para>
/// <b>It reads the back buffer, not the screen.</b> The obvious way to get a picture of a
/// game is to capture the desktop, and it is the wrong way: on a shared machine that
/// captures whatever else happens to be on it, including other people's windows. This
/// reads only what this process rendered, so it cannot see anything but the game, and it
/// works with the window occluded or behind another one.
/// </para>
/// <para>
/// <b>Debug developer switch</b>, opt-in via <c>--screenshot-at=</c> and absent
/// otherwise, like <see cref="Autopilot"/>.
/// </para>
/// </remarks>
internal sealed class FrameCapture
{
    private const string AtPrefix = "--screenshot-at=";
    private const string DirPrefix = "--screenshot-dir=";
    private const string TagPrefix = "--screenshot-tag=";

    private readonly Queue<float> _due;
    private readonly string _directory;
    private readonly string _tag;

    private FrameCapture(IEnumerable<float> times, string directory, string tag)
    {
        _due = new Queue<float>(times.Order());
        _directory = directory;
        _tag = tag;
    }

    /// <summary>
    /// Builds a capture plan from the command line, or null when none was requested.
    /// </summary>
    /// <remarks>
    /// <c>--screenshot-at=8,12,16</c> gives seconds of wall-clock game time.
    /// <c>--screenshot-dir=</c> and <c>--screenshot-tag=</c> name the output; the tag
    /// keeps two instances writing to one directory from overwriting each other.
    /// </remarks>
    public static FrameCapture? FromCommandLine(IReadOnlyList<string> args)
    {
        var times = new List<float>();
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        var tag = "frame";

        foreach (var arg in args)
        {
            if (arg.StartsWith(AtPrefix, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in arg[AtPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (float.TryParse(part, CultureInfo.InvariantCulture, out var seconds))
                    {
                        times.Add(seconds);
                    }
                }
            }
            else if (arg.StartsWith(DirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                directory = arg[DirPrefix.Length..];
            }
            else if (arg.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                tag = arg[TagPrefix.Length..];
            }
        }

        return times.Count == 0 ? null : new FrameCapture(times, directory, tag);
    }

    /// <summary>
    /// Saves a frame if one is due. Call at the very end of <c>Draw</c>, once the frame is
    /// complete but before it is presented.
    /// </summary>
    public void Update(GraphicsDevice device, GameTime gameTime)
    {
        if (_due.Count == 0)
        {
            return;
        }

        var now = (float)gameTime.TotalGameTime.TotalSeconds;

        if (now < _due.Peek())
        {
            return;
        }

        var at = _due.Dequeue();

        try
        {
            Directory.CreateDirectory(_directory);

            var width = device.PresentationParameters.BackBufferWidth;
            var height = device.PresentationParameters.BackBufferHeight;
            var pixels = new Color[width * height];
            device.GetBackBufferData(pixels);

            using var texture = new Texture2D(device, width, height);
            texture.SetData(pixels);

            var path = Path.Combine(
                _directory,
                $"{_tag}-{at.ToString("00", CultureInfo.InvariantCulture)}s.png");

            using var stream = File.Create(path);
            texture.SaveAsPng(stream, width, height);

            Console.WriteLine($"[capture] {path} ({width}x{height})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A screenshot is a diagnostic. Losing one must not take the game down with it.
            Console.WriteLine($"[capture] failed at {at:F0}s: {ex.Message}");
        }
    }
}
