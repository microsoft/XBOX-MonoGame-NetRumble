using System.Text.Json.Serialization;

namespace NetRumble.Platform.PlayFab;

/// <summary>
/// The source-generated serializer for every PlayFab request body this assembly sends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The transport used to serialize through
/// <c>JsonSerializer.Serialize(body, body.GetType(), options)</c>, which resolves the
/// contract by reflecting over the type at runtime. That overload carries
/// <c>RequiresDynamicCode</c>, and the GDKX console build runs ILC with
/// <c>IsDynamicCodeSupported=false</c>: it compiled and ran everywhere on the desktop
/// and would have thrown on console the first time the game talked to PlayFab. It was
/// visible only as an aggregated <c>IL3053</c> on this assembly, and was suppressed in
/// practice by rooting the whole assembly in <c>console/NetRumble.rd.xml</c> - a
/// mitigation that costs binary size and stops working the moment a trimmed
/// configuration is used.
/// </para>
/// <para>
/// <b>Registration is enforced by the compiler, not discovered at runtime.</b>
/// <see cref="PlayFabRestClient.PostAsync"/> takes a
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> rather than
/// looking the body's type up in this context, so a request type that nobody added here
/// fails to build. The alternative - the <c>Serialize(object, Type, JsonSerializerContext)</c>
/// overload - is equally AOT-safe but defers the same mistake to an
/// <see cref="InvalidOperationException"/> at runtime, on console, in front of a player.
/// That is the failure mode this whole change set out to remove, so it is not used.
/// </para>
/// <para>
/// Only request bodies appear below. Responses are read with <c>JsonDocument</c> and
/// <c>JsonElement</c> throughout, which is reflection-free already and deliberately so:
/// PlayFab's envelopes are sparse, partially typed and version-drifting, and reading
/// them by hand keeps a new field in a response from becoming a deserialization failure.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    // Matches what the reflection-based options did. PlayFab treats an explicit null
    // differently from an absent field on several endpoints, so this is behaviour the
    // requests depend on, not just a size optimisation.
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlayFabRestClient.EmptyRequest))]
[JsonSerializable(typeof(PlayFabAuthClient.XboxLoginRequest))]
[JsonSerializable(typeof(PlayFabAuthClient.CustomIdLoginRequest))]
[JsonSerializable(typeof(PlayFabGameSaveService.UpdateUserDataRequest))]
[JsonSerializable(typeof(PlayFabGameSaveService.GetUserDataRequest))]
[JsonSerializable(typeof(PlayFabLobbyClient.CreateLobbyRequest))]
[JsonSerializable(typeof(PlayFabLobbyClient.FindLobbiesRequest))]
[JsonSerializable(typeof(PlayFabLobbyClient.JoinLobbyRequest))]
[JsonSerializable(typeof(PlayFabLobbyClient.LeaveLobbyRequest))]
[JsonSerializable(typeof(PlayFabLobbyClient.GetLobbyRequest))]
[JsonSerializable(typeof(PlayFabLobbyClient.DeleteLobbyRequest))]
internal sealed partial class PlayFabJsonContext : JsonSerializerContext;
