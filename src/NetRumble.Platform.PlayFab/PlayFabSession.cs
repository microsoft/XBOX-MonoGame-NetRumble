namespace NetRumble.Platform.PlayFab;

/// <summary>Non-secret identity information from a native PlayFab login.</summary>
public sealed record PlayFabSession
{
    public required string EntityId { get; init; }
    public required string EntityType { get; init; }
    public required string PlayFabId { get; init; }
    public bool AccountWasCreated { get; init; }
}
