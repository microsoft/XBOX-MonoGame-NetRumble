using System.Text.Json.Serialization;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// The payload a <c>gdk-party:</c> connection string carries, base64url-encoded.
/// </summary>
/// <remarks>
/// A record rather than positional strings because an invite outlives the process
/// that made it: it is handed to the shell, may sit in a notification for minutes, and
/// comes back through a protocol activation. The wire version is part of the envelope,
/// because an invite carries a Party descriptor rather than a lobby row and therefore
/// cannot inherit the lobby's indexed protocol field.
/// </remarks>
/// <param name="Descriptor">The serialized Party network descriptor.</param>
/// <param name="Invitation">The Party invitation identifier, when one was applied.</param>
/// <param name="HostToken">
/// The opaque per-session stand-in for the host's PlayFab entity id (XR-014). This
/// envelope is base64 of plain JSON handed to the shell, so anything in it is effectively
/// public; the entity id that used to sit here is a durable account identifier and had no
/// business being published. See <see cref="PartyHostToken"/>.
/// </param>
/// <param name="ProtocolVersion">The wire version that minted the invite.</param>
internal sealed record PartyInviteEnvelope(
    string Descriptor,
    string Invitation,
    string HostToken,
    string ProtocolVersion);

/// <summary>
/// Source-generated serialization for <see cref="PartyInviteEnvelope"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not optional on console.</b> The GDKX build runs ILC with
/// <c>--feature:System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported=false</c>,
/// so the reflection-based <c>JsonSerializer</c> overloads cannot work: they are attributed
/// <c>RequiresDynamicCode</c> and would throw at runtime rather than fail to compile.
/// Reaching for them here was caught as ILC warning <c>IL3050</c>, and it would have
/// broken exactly the path that only exists for console - accepting an invite from the
/// Xbox shell.
/// </para>
/// <para>
/// The desktop build does not care either way, which is the trap: this is invisible to
/// <c>dotnet build NetRumble.slnx</c> and to the spike, and only shows up in
/// <c>tools\Build-Gdkx.ps1</c>. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PartyInviteEnvelope))]
internal sealed partial class PartyInviteJsonContext : JsonSerializerContext;
