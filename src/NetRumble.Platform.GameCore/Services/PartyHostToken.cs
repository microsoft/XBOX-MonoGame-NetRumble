using System.Security.Cryptography;
using System.Text;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// An opaque, per-session stand-in for the host's PlayFab entity id (XR-014).
/// </summary>
/// <remarks>
/// <para>
/// Clients have to be able to tell the host's Party endpoint from every other endpoint in
/// the network: it is the check that stops any player already in the session sending five
/// bytes and being promoted to authority over another client. That check used to be a
/// string comparison against the host's raw entity id, which meant the entity id had to be
/// published - into public lobby data, into the invite envelope, and into the base64
/// <c>gdk-party:</c> connection string the shell hands around. An entity id is a durable
/// account identifier in the publisher's service, so that is account-scoped data travelling
/// well beyond the session that needs it.
/// </para>
/// <para>
/// A token is a fresh random salt and an HMAC of the entity id under it. It proves nothing
/// about who the host is to anyone who does not already know the entity id, and two
/// sessions hosted by the same account produce unrelated tokens, so it cannot be used to
/// correlate a player across matches. A client verifies a candidate endpoint by recomputing
/// the HMAC over the entity id <i>Party itself authenticated</i> - so the check is exactly
/// as strong as the comparison it replaces, without publishing the value.
/// </para>
/// <para>
/// Guessing is not a concern: an entity id is sixteen random bytes rendered as hex, so
/// there is nothing to brute-force a digest against.
/// </para>
/// </remarks>
internal static class PartyHostToken
{
    private const int SaltBytes = 16;
    private const int DigestBytes = 16;

    /// <summary>Mints a token for a host's own entity id.</summary>
    public static string Create(string entityId)
    {
        if (string.IsNullOrEmpty(entityId))
        {
            return string.Empty;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var token = new byte[SaltBytes + DigestBytes];

        salt.CopyTo(token.AsSpan());
        Digest(salt, entityId).AsSpan(0, DigestBytes).CopyTo(token.AsSpan(SaltBytes));

        return Encode(token);
    }

    /// <summary>
    /// True when <paramref name="entityId"/> is the entity the token was minted for.
    /// </summary>
    /// <remarks>
    /// A malformed or empty token matches nothing. Failing closed matters here: this is an
    /// authority check, and "could not tell" must never read as "yes".
    /// </remarks>
    public static bool Matches(string token, string? entityId)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(entityId))
        {
            return false;
        }

        if (!TryDecode(token, out var bytes))
        {
            return false;
        }

        var salt = bytes.AsSpan(0, SaltBytes).ToArray();

        return CryptographicOperations.FixedTimeEquals(
            bytes.AsSpan(SaltBytes, DigestBytes),
            Digest(salt, entityId).AsSpan(0, DigestBytes));
    }

    /// <summary>
    /// True when the value is shaped like a token rather than a raw entity id, so a payload
    /// produced by a build that published entity ids can still be recognised for what it is.
    /// </summary>
    public static bool IsToken(string value) => TryDecode(value, out _);

    private static byte[] Digest(byte[] salt, string entityId)
        => HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(entityId));

    private static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];

        var encoded = value.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - (encoded.Length % 4)) % 4), '=');

        try
        {
            var decoded = Convert.FromBase64String(encoded);

            if (decoded.Length != SaltBytes + DigestBytes)
            {
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
