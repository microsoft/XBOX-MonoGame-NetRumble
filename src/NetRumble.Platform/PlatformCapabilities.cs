namespace NetRumble.Platform;

/// <summary>
/// Capabilities a provider implements. Lets the game hide or disable UI instead of
/// calling a service and handling a predictable failure.
/// </summary>
[Flags]
public enum PlatformCapabilities
{
    None = 0,
    Identity = 1 << 0,
    Party = 1 << 1,
    Matchmaking = 1 << 2,
    VoiceChat = 1 << 3,
    SpeechToText = 1 << 4,
    Achievements = 1 << 5,
    CloudSave = 1 << 7,
    Presence = 1 << 8,
    Invites = 1 << 9,
    Privileges = 1 << 10,
    Privacy = 1 << 11,
    Moderation = 1 << 12,
    Social = 1 << 13,
    GameInput = 1 << 14,
    Haptics = 1 << 15,
    VirtualKeyboard = 1 << 16,

    /// <summary>What a fully wired GDK + PlayFab provider reports.</summary>
    All = Identity | Party | Matchmaking | VoiceChat | SpeechToText | Achievements
        | CloudSave | Presence | Invites | Privileges | Privacy
        | Moderation | Social | GameInput | Haptics | VirtualKeyboard,
}
