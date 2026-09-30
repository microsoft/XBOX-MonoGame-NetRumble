using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game.UI;

/// <summary>
/// Fetches, decodes and caches other players' gamerpics (XR-046).
/// </summary>
/// <remarks>
/// <para>
/// XR-046 expects a player's own identity and the identities of the players around them to
/// be shown the way the shell shows them, which means the gamerpic and not only the
/// gamertag. The title drew no picture anywhere: the roster, the friends list and the
/// standings were text.
/// </para>
/// <para>
/// <b>Everything here is optional.</b> A surface asks for a texture and either gets one or
/// gets <see langword="null"/> and draws what it always drew. Nothing waits on a download,
/// nothing blocks a frame on one, and a platform with no social service at all - offline,
/// LAN, a desktop build with no GDK - simply never produces a texture. A missing gamerpic
/// is a cosmetic absence; a stalled lobby is not.
/// </para>
/// <para>
/// <b>Decoding happens on the game thread.</b> The bytes are fetched on the thread pool,
/// but <see cref="Texture2D.FromStream(GraphicsDevice, Stream)"/> touches the graphics
/// device, so finished downloads queue and are drained from <see cref="Update"/>. This is
/// also what keeps the cache dictionary single-threaded for readers.
/// </para>
/// </remarks>
public sealed class GamerPictureCache : IDisposable
{
    /// <summary>
    /// Long enough that a gamerpic swap during a session is picked up, short enough that
    /// the picture is not re-fetched every time a popup opens.
    /// </summary>
    private static readonly TimeSpan UriLifetime = TimeSpan.FromMinutes(30);

    private readonly GraphicsDevice _graphics;
    private readonly IPlatformProvider _platform;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uris = new(StringComparer.Ordinal);
    private readonly Queue<(string XboxUserId, byte[] Bytes)> _decodeQueue = new();
    private readonly object _gate = new();

    private DateTimeOffset _urisFetched = DateTimeOffset.MinValue;
    private bool _disposed;

    public GamerPictureCache(GraphicsDevice graphics, IPlatformProvider platform)
    {
        _graphics = graphics;
        _platform = platform;
    }

    /// <summary>
    /// The cached picture for a player, or <see langword="null"/> when there is not one
    /// <i>yet</i> - which is also the answer when there will never be one. Starts the
    /// fetch on the first miss.
    /// </summary>
    public Texture2D? Get(string xboxUserId)
    {
        if (string.IsNullOrEmpty(xboxUserId))
        {
            return null;
        }

        if (_textures.TryGetValue(xboxUserId, out var texture))
        {
            return texture;
        }

        Request([xboxUserId]);
        return null;
    }

    /// <summary>
    /// Warms the cache for a whole surface at once. Cheaper than a row at a time: the
    /// platform resolves a batch of xuids in one round trip.
    /// </summary>
    public void Request(IEnumerable<string> xboxUserIds)
    {
        var wanted = new List<string>();

        foreach (var xuid in xboxUserIds)
        {
            if (string.IsNullOrEmpty(xuid) || _textures.ContainsKey(xuid))
            {
                continue;
            }

            lock (_gate)
            {
                if (!_inFlight.Add(xuid))
                {
                    continue;
                }
            }

            wanted.Add(xuid);
        }

        if (wanted.Count > 0)
        {
            _ = FetchAsync(wanted);
        }
    }

    /// <summary>
    /// Records a gamerpic URL a caller already has, so the friends list does not pay for a
    /// second profile lookup for the pictures its own query already returned.
    /// </summary>
    public void Offer(string xboxUserId, string uri)
    {
        if (string.IsNullOrEmpty(xboxUserId) || string.IsNullOrEmpty(uri))
        {
            return;
        }

        lock (_gate)
        {
            _uris[xboxUserId] = uri;
        }
    }

    /// <summary>Turns finished downloads into textures. Call once a frame.</summary>
    public void Update()
    {
        while (true)
        {
            (string XboxUserId, byte[] Bytes) pending;

            lock (_gate)
            {
                if (_decodeQueue.Count == 0)
                {
                    return;
                }

                pending = _decodeQueue.Dequeue();
            }

            try
            {
                using var stream = new MemoryStream(pending.Bytes);
                _textures[pending.XboxUserId] = Texture2D.FromStream(_graphics, stream);
            }
            catch (Exception ex)
            {
                // A gamerpic that will not decode is cached as "no picture" rather than
                // retried: whatever came back is not an image this build can read, and
                // asking again produces the same bytes.
                _textures[pending.XboxUserId] = null;
                CrashLog.MarkOnce("gamerpic-decode", $"gamerpic: decode failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Drops every picture. Called when the signed-in user changes: gamerpics are read
    /// through that user's privacy relationships, so another account's answers are not
    /// this one's to reuse (XR-052).
    /// </summary>
    public void Clear()
    {
        foreach (var texture in _textures.Values)
        {
            texture?.Dispose();
        }

        _textures.Clear();

        lock (_gate)
        {
            _uris.Clear();
            _inFlight.Clear();
            _decodeQueue.Clear();
            _urisFetched = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        _http.Dispose();
    }

    private async Task FetchAsync(List<string> xboxUserIds)
    {
        try
        {
            var unresolved = new List<string>();

            lock (_gate)
            {
                var stale = DateTimeOffset.UtcNow - _urisFetched > UriLifetime;

                foreach (var xuid in xboxUserIds)
                {
                    if (stale || !_uris.ContainsKey(xuid))
                    {
                        unresolved.Add(xuid);
                    }
                }
            }

            if (unresolved.Count > 0)
            {
                var resolved = await _platform.Social
                    .ResolveGamerPicturesAsync(unresolved)
                    .ConfigureAwait(false);

                if (resolved.Succeeded && resolved.Value is { } pictures)
                {
                    lock (_gate)
                    {
                        foreach (var (xuid, uri) in pictures)
                        {
                            _uris[xuid] = uri;
                        }

                        _urisFetched = DateTimeOffset.UtcNow;
                    }
                }
            }

            foreach (var xuid in xboxUserIds)
            {
                string? uri;

                lock (_gate)
                {
                    _uris.TryGetValue(xuid, out uri);
                }

                if (string.IsNullOrEmpty(uri))
                {
                    Finish(xuid, bytes: null);
                    continue;
                }

                try
                {
                    var bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
                    Finish(xuid, bytes);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // No picture this session. Not retried: the surfaces that ask are
                    // redrawn every frame, and a retry loop behind a draw call is how a
                    // cosmetic feature turns into a request storm.
                    Finish(xuid, bytes: null);
                    CrashLog.MarkOnce("gamerpic-fetch", $"gamerpic: fetch failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Fatal("gamerpic: fetch", ex);

            foreach (var xuid in xboxUserIds)
            {
                Finish(xuid, bytes: null);
            }
        }
    }

    private void Finish(string xboxUserId, byte[]? bytes)
    {
        lock (_gate)
        {
            _inFlight.Remove(xboxUserId);

            if (bytes is { Length: > 0 })
            {
                _decodeQueue.Enqueue((xboxUserId, bytes));
                return;
            }
        }

        // "There is no picture" is a real answer and is cached as one, so the surface does
        // not ask again on every draw.
        _textures[xboxUserId] = null;
    }

    /// <summary>
    /// Draws a gamerpic into a square, or nothing when there is not one. Returns whether
    /// anything was drawn, so a caller can lay its row out either way.
    /// </summary>
    public bool Draw(UiContext context, string xboxUserId, Rectangle bounds)
    {
        var texture = Get(xboxUserId);

        if (texture is null)
        {
            return false;
        }

        context.Batch.Draw(texture, bounds, Color.White);
        return true;
    }
}
