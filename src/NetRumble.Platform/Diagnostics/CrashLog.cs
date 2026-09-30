using System.Globalization;
using System.Text;

namespace NetRumble.Platform.Diagnostics;

/// <summary>
/// A last-resort text log: unhandled exceptions, plus breadcrumbs from the code paths
/// that only ever run on real hardware.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of how a console failure presents itself. On a desktop dev box an
/// unhandled exception stops in the debugger with a stack; on a packaged Xbox title with
/// no debugger attached the title simply disappears back to the dashboard, and whatever
/// was on the way to <c>OutputDebugString</c> went with it. The bugs that only show up
/// with two consoles in a match are exactly the ones nobody is attached to when they
/// happen, so the evidence has to survive the process - which means a file.
/// </para>
/// <para>
/// Which file is the awkward part. A packaged title's install folder is mapped read-only
/// (<c>G:\</c>), so the usual "next to the executable" answer silently throws; the
/// writable locations are the per-title temporary drive <c>T:\</c> and the persistent
/// local storage path. Rather than encode that policy here, the caller passes candidate
/// roots in preference order and the first one that accepts a real write wins - so the
/// desktop build gets a folder under LocalAppData and the console build gets <c>T:\</c>
/// without this class knowing which platform it is on.
/// </para>
/// <para>
/// Everything here is defensive to the point of paranoia: every method swallows its own
/// failures. A diagnostic that can take the title down is worse than no diagnostic, and
/// this one runs while the process is already dying.
/// </para>
/// </remarks>
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static readonly HashSet<string> OnceKeys = new(StringComparer.Ordinal);

    private static string? _path;
    private static FileStream? _stream;
    private static bool _installed;

    /// <summary>
    /// An additional sink for every line written, used for the debugger channel.
    /// </summary>
    /// <remarks>
    /// <c>OutputDebugString</c> is P/Invoked from the game assembly rather than called
    /// here on purpose: this assembly is shared with the desktop build and is compiled
    /// without the <c>GDKX</c> define, so it has no business deciding which debug API set
    /// exists. When a debugger *is* attached, this is the channel that matters, because
    /// it arrives live instead of after the fact.
    /// </remarks>
    public static Action<string>? Echo { get; set; }

    /// <summary>
    /// Gets the file breadcrumbs are being written to, or <see langword="null"/> when no
    /// candidate root turned out to be writable.
    /// </summary>
    public static string? Path => _path;

    /// <summary>
    /// Chooses a log file and hooks the two process-wide "nobody caught this" events.
    /// </summary>
    /// <param name="candidateRoots">
    /// Directories to try in preference order. The first that can be created and written
    /// to is used.
    /// </param>
    /// <remarks>
    /// The handlers matter as much as the file. <c>Program.MainCore</c> only wraps
    /// <c>game.Run()</c>, which covers the game loop and nothing else - an exception on a
    /// thread pool thread (a Party callback continuation, a PlayFab HTTP completion, a
    /// fire-and-forget sign-in step) bypasses it entirely and reaches the NativeAOT
    /// failfast with nothing logged. Those are precisely the threads console networking
    /// runs on.
    /// </remarks>
    public static void Install(params string[] candidateRoots)
    {
        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            _stream = OpenLog(candidateRoots);
            _path = _stream?.Name;
        }

        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
            Fatal("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        // Without observing these, a faulted fire-and-forget task is collected silently
        // and the only symptom is behaviour that never happened.
        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {
            Fatal("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        Mark($"---- session start, pid {Environment.ProcessId} ----");
    }

    /// <summary>
    /// Records a breadcrumb.
    /// </summary>
    /// <remarks>
    /// Breadcrumbs are what localise a failure that leaves no exception behind at all - a
    /// native access violation inside the Party layer, or a failfast. The last line in
    /// the file is the last thing the title managed to do, so these are placed at the
    /// steps of a match start rather than sprinkled evenly. Do not call this from a
    /// per-frame path; use <see cref="MarkOnce"/> for anything inside a loop.
    /// </remarks>
    public static void Mark(string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {message}");

        try
        {
            Echo?.Invoke(line);
        }
        catch
        {
            // A broken echo must not cost us the file write below.
        }

        Write(line);
    }

    /// <summary>
    /// Records a breadcrumb the first time a given key is seen and ignores it thereafter,
    /// so a message can be placed inside a loop - "the first snapshot went out" is
    /// evidence, thirty a second is a denial-of-service on the log.
    /// </summary>
    public static void MarkOnce(string key, string message)
    {
        lock (Gate)
        {
            if (!OnceKeys.Add(key))
            {
                return;
            }
        }

        Mark(message);
    }

    /// <summary>
    /// Records an exception, with its inner exceptions and stacks, under a label saying
    /// where it was caught.
    /// </summary>
    public static void Fatal(string context, Exception? ex)
    {
        var text = new StringBuilder()
            .Append("!! ")
            .Append(context)
            .Append(": ")
            .Append(ex?.ToString() ?? "(no exception object)")
            .ToString();

        Mark(text);
    }

    private static void Write(string line)
    {
        var stream = _stream;

        if (stream is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);

            lock (Gate)
            {
                stream.Write(bytes, 0, bytes.Length);

                // Flush all the way through the OS cache rather than just out of the
                // managed buffer. The whole point of this file is to be readable after a
                // process that did not get to shut down, and buffered-but-unwritten is
                // indistinguishable from never-logged once the title is gone.
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Out of space, a revoked handle, a torn-down file system on the way out of
            // a failfast - none of it is worth an exception from a logger.
        }
    }

    /// <summary>
    /// Returns the first candidate root that accepts a write, having discarded any log
    /// left over from previous runs that has grown past <see cref="MaxBytes"/>.
    /// </summary>
    /// <summary>
    /// Opens the log in the first candidate root that accepts one, having discarded any
    /// file left over from previous runs that has grown past <see cref="MaxBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opening for real rather than checking that the directory exists is the point: that
    /// is the whole failure mode being guarded against, because the read-only install
    /// folder on a packaged console title contains the directory perfectly happily and
    /// refuses the write.
    /// </para>
    /// <para>
    /// The handle is then held open for the life of the process, which also settles the
    /// side-by-side case. Two instances on one desktop - the local two-player test - would
    /// otherwise interleave into one file and lose lines to each other's locks; here the
    /// second finds the preferred name taken and falls back to a process-id suffix, while
    /// the console, where only one instance can ever run, always lands on the predictable
    /// name.
    /// </para>
    /// </remarks>
    private static FileStream? OpenLog(string[] candidateRoots)
    {
        foreach (var root in candidateRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(root);
            }
            catch
            {
                continue;
            }

            foreach (var name in new[] { "netrumble.log", $"netrumble-{Environment.ProcessId}.log" })
            {
                try
                {
                    var candidate = System.IO.Path.Combine(root, name);

                    if (File.Exists(candidate) && new FileInfo(candidate).Length > MaxBytes)
                    {
                        File.Delete(candidate);
                    }

                    return new FileStream(
                        candidate,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read);
                }
                catch
                {
                    // Try the process-id name, then the next root. Running with no log at
                    // all is survivable.
                }
            }
        }

        return null;
    }
}
