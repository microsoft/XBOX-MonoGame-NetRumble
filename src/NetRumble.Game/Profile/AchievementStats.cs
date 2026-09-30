using System.Numerics;
using System.Text.Json;
using NetRumble.Core;
using NetRumble.Platform;

namespace NetRumble.Game.Profile;

/// <summary>
/// The cumulative progress behind the five progressive achievements, persisted through
/// <see cref="IGameSaveService"/>: local tier always, PlayFab cloud tier once signed in.
/// </summary>
/// <remarks>
/// <para>
/// Five of this title's ten achievements (see <c>docs/achievements2017.xml</c> in the
/// Godot repo) count across matches and across sessions - every weapon fired, every buff
/// collected, every mode completed, 250 asteroids, 100 kills. Xbox Services is the system
/// of record for what has been <i>awarded</i>, but it has no way to answer "how many
/// asteroids has this player broken", so the counters have to live somewhere. That is
/// precisely what a save system is for, and it is why these stats - not the settings -
/// are the load-bearing use of Game Save in this port.
/// </para>
/// <para>
/// <b>Merge, do not overwrite.</b> Unlike settings, where the cloud copy simply wins,
/// two devices can both make progress offline. Every field here is monotonic (a counter
/// or a set of flags), so the merge is the maximum of the counters and the union of the
/// bit sets. That can over-count if the same match is somehow recorded twice, and cannot
/// lose progress - the right way round for an achievement counter.
/// </para>
/// <para>
/// <b>Size.</b> Five integers, well inside the 750 bytes a PlayFab user-data value allows
/// once Base64-encoded.
/// </para>
/// </remarks>
public sealed class AchievementStats
{
    /// <summary>
    /// The save key. Separate from <see cref="CloudSettingsSync.SaveKey"/> because the two
    /// have different merge rules - settings take the cloud copy wholesale, stats merge.
    /// </summary>
    public const string SaveKey = "stats";

    /// <summary>
    /// The number of distinct weapons "Arms Dealer" requires.
    /// </summary>
    /// <remarks>
    /// Derived from the enum rather than written out as a mask, so adding a weapon moves
    /// the target with it instead of leaving the achievement unlockable without the new
    /// entry. The former <c>AllWeaponsMask</c> was a literal <c>0b1111</c> and would have
    /// silently kept completing at four of twenty.
    /// </remarks>
    public static int WeaponCount => Enum.GetValues<WeaponType>().Length;

    /// <summary>
    /// The number of distinct buffs "Fully Buffed" requires.
    /// </summary>
    /// <remarks>
    /// Buffs, not pickups. Only a <see cref="PickupKind.Buff"/> pickup counts: weapon and
    /// restore drops are the majority of the table, and requiring all thirty-two would
    /// make the achievement a grind against a weighted random drop rather than a goal.
    /// </remarks>
    public static int BuffCount => Enum.GetValues<BuffType>().Length;

    /// <summary>
    /// The three modes "Full House" was scored against, as a bit set.
    /// </summary>
    /// <remarks>
    /// The game has one mode now - the picker and the <c>GameModeType</c> enum are gone -
    /// but the achievement is defined in the title's real service configuration as
    /// completing a match in <i>every</i> mode, and that definition is not this build's to
    /// change. So the mask stays at three and <see cref="AddModeCompleted"/> can only ever
    /// set the first bit: "Full House" reports one third and never unlocks. Deliberate,
    /// and preferred over redefining it to one mode, which would unlock a hundred
    /// gamerscore on the first finished match for players who never saw the other two.
    /// </remarks>
    public const int AllModesMask = 0b111;

    /// <summary>
    /// Bit positions into <see cref="BinaryUnlocked"/>, one per one-shot (non-progressive)
    /// achievement id (XR-055). Kept here rather than duplicating
    /// <see cref="AchievementTracker"/>'s id strings, since this class has no dependency
    /// on that one and the bit only needs to be stable, not human-readable.
    /// </summary>
    public const int FirstMatchFinishedBit = 1 << 0;

    /// <summary>See <see cref="FirstMatchFinishedBit"/>.</summary>
    public const int FirstDeathBit = 1 << 1;

    /// <summary>See <see cref="FirstMatchFinishedBit"/>.</summary>
    public const int FirstKillBit = 1 << 2;

    /// <summary>See <see cref="FirstMatchFinishedBit"/>.</summary>
    public const int MatchWonBit = 1 << 3;

    /// <summary>See <see cref="FirstMatchFinishedBit"/>.</summary>
    public const int UntouchableWinBit = 1 << 4;

    /// <summary>Weapons this player has fired at least once, as a <see cref="WeaponType"/> bit set.</summary>
    public int WeaponsFired { get; set; }

    /// <summary>Buffs this player has had at least once, as a <see cref="BuffType"/> bit set.</summary>
    public int BuffsCollected { get; set; }

    /// <summary>
    /// Game modes completed against human opponents, as a bit set over
    /// <see cref="AllModesMask"/>. Only bit 0 - Deathmatch - can be set now.
    /// </summary>
    public int ModesCompleted { get; set; }

    /// <summary>
    /// The one-shot achievements earned so far, as the <c>*Bit</c> constants above. Exists
    /// so a binary unlock survives the process exiting before the service call that
    /// reports it succeeds (XR-055) - <see cref="AchievementTracker"/> re-sends every bit
    /// set here every time it gets a chance to talk to the service, not just the moment
    /// the underlying gameplay event happens, since that event may never recur.
    /// </summary>
    public int BinaryUnlocked { get; set; }

    /// <summary>Enemy ships destroyed, all-time.</summary>
    public int Kills { get; set; }

    /// <summary>Asteroids destroyed by the local player's own shots, all-time.</summary>
    public int AsteroidsDestroyed { get; set; }

    /// <summary>
    /// The highest percentage the achievement service has <i>confirmed</i> receiving for
    /// each progressive achievement, keyed by achievement id (XR-055).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The durable half of progressive reporting, and the counterpart to
    /// <see cref="BinaryUnlocked"/>. The counters above already survive a restart, but
    /// knowing that this player has broken 250 asteroids is not the same as knowing the
    /// service was told: a report that failed while the service was briefly unavailable,
    /// or in the seconds before a suspend, used to leave no trace at all, and the in-memory
    /// dedupe entry that would have caused a retry died with the process.
    /// </para>
    /// <para>
    /// Written only after a successful send, so a value here is a fact about the service
    /// rather than an intention. Anything below the percentage the counters currently
    /// imply is a report still owed, which is what lets
    /// <see cref="AchievementTracker"/> pick the work back up on a later sign-in, resume
    /// or reconnect.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, int> ConfirmedPercent => _confirmedPercent;

    private readonly Dictionary<string, int> _confirmedPercent = new(StringComparer.Ordinal);

    /// <summary>True when something has changed that has not been persisted.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>Records a weapon being fired. Returns true if this was a new one.</summary>
    public bool AddWeaponFired(WeaponType weapon)
    {
        if (!Enum.IsDefined(weapon))
        {
            return false;
        }

        var bit = 1 << (int)weapon;

        if ((WeaponsFired & bit) != 0)
        {
            return false;
        }

        WeaponsFired |= bit;
        IsDirty = true;
        return true;
    }

    /// <summary>Records a buff being granted. Returns true if this was a new one.</summary>
    public bool AddBuffCollected(BuffType buff)
    {
        if (!Enum.IsDefined(buff))
        {
            return false;
        }

        var bit = 1 << (int)buff;

        if ((BuffsCollected & bit) != 0)
        {
            return false;
        }

        BuffsCollected |= bit;
        IsDirty = true;
        return true;
    }

    /// <summary>
    /// Records the mode being completed. Returns true the first time only.
    /// </summary>
    /// <remarks>
    /// Deathmatch is bit 0 and the only bit this can set; see <see cref="AllModesMask"/>
    /// for why the other two remain in the mask regardless.
    /// </remarks>
    public bool AddModeCompleted()
    {
        const int DeathmatchBit = 1 << 0;

        if ((ModesCompleted & DeathmatchBit) != 0)
        {
            return false;
        }

        ModesCompleted |= DeathmatchBit;
        IsDirty = true;
        return true;
    }

    /// <summary>Records a one-shot achievement earned. Returns true if this was new.</summary>
    public bool AddBinaryUnlocked(int bit)
    {
        if ((BinaryUnlocked & bit) != 0)
        {
            return false;
        }

        BinaryUnlocked |= bit;
        IsDirty = true;
        return true;
    }

    /// <summary>Records one kill.</summary>
    public void AddKill()
    {
        Kills++;
        IsDirty = true;
    }

    /// <summary>Records one asteroid destroyed.</summary>
    public void AddAsteroidDestroyed()
    {
        AsteroidsDestroyed++;
        IsDirty = true;
    }

    /// <summary>
    /// Records a percentage the service confirmed receiving. Monotonic: a lower value is
    /// ignored, so an out-of-order confirmation cannot walk the record backwards and make
    /// the title re-report progress the service already has.
    /// </summary>
    public bool SetConfirmedPercent(string achievementId, int percent)
    {
        if (_confirmedPercent.TryGetValue(achievementId, out var confirmed) && confirmed >= percent)
        {
            return false;
        }

        _confirmedPercent[achievementId] = percent;
        IsDirty = true;
        return true;
    }

    /// <summary>The percentage the service last confirmed for an achievement, or zero.</summary>
    public int GetConfirmedPercent(string achievementId)
        => _confirmedPercent.TryGetValue(achievementId, out var percent) ? percent : 0;

    /// <summary>
    /// Folds another copy of these stats into this one, taking the best of each field.
    /// See the class remarks for why the merge is monotonic.
    /// </summary>
    public void MergeFrom(AchievementStats other)
    {
        var before = (WeaponsFired, BuffsCollected, ModesCompleted, BinaryUnlocked, Kills, AsteroidsDestroyed);

        WeaponsFired |= other.WeaponsFired;
        BuffsCollected |= other.BuffsCollected;
        ModesCompleted |= other.ModesCompleted;
        BinaryUnlocked |= other.BinaryUnlocked;
        Kills = Math.Max(Kills, other.Kills);
        AsteroidsDestroyed = Math.Max(AsteroidsDestroyed, other.AsteroidsDestroyed);

        // Monotonic like everything else here: another device having got a report through
        // is a fact this device should not undo, and a report this device has confirmed
        // does not stop being confirmed because the other copy predates it.
        var confirmedChanged = false;

        foreach (var (achievementId, percent) in other._confirmedPercent)
        {
            if (!_confirmedPercent.TryGetValue(achievementId, out var mine) || mine < percent)
            {
                _confirmedPercent[achievementId] = percent;
                confirmedChanged = true;
            }
        }

        if (confirmedChanged
            || before != (WeaponsFired, BuffsCollected, ModesCompleted, BinaryUnlocked, Kills, AsteroidsDestroyed))
        {
            IsDirty = true;
        }
    }

    /// <summary>Loads the local copy. A missing or unreadable record is an empty one.</summary>
    public static async Task<AchievementStats> LoadLocalAsync(
        IGameSaveService saves,
        CancellationToken cancellationToken = default)
    {
        var result = await saves.LoadLocalAsync(SaveKey, cancellationToken);

        return result.Failed || result.Value is not { Length: > 0 } bytes
            ? new AchievementStats()
            : TryParse(bytes) ?? new AchievementStats();
    }

    /// <summary>
    /// Merges the cloud copy in and pushes the result back, so both tiers carry the same
    /// totals. Best-effort: a provider with no cloud tier, or nobody signed in, is a no-op.
    /// </summary>
    public async Task SyncCloudAsync(IGameSaveService saves, CancellationToken cancellationToken = default)
    {
        var result = await saves.LoadCloudAsync(SaveKey, cancellationToken);

        if (result.Failed)
        {
            // Offline, signed out, or the provider has no cloud tier. The local copy is
            // still authoritative for this device; nothing is lost.
            return;
        }

        if (result.Value is { Length: > 0 } bytes && TryParse(bytes) is { } cloud)
        {
            MergeFrom(cloud);
        }

        // Unconditional: even when the cloud had nothing to add, this is the write that
        // seeds a brand new account from whatever was earned offline.
        await SaveAsync(saves, cancellationToken);
    }

    /// <summary>Writes both tiers. The cloud write is best-effort and never reported.</summary>
    public async Task SaveAsync(IGameSaveService saves, CancellationToken cancellationToken = default)
    {
        var bytes = Serialise();
        IsDirty = false;

        await saves.SaveLocalAsync(SaveKey, bytes, cancellationToken);
        await saves.SaveCloudAsync(SaveKey, bytes, cancellationToken);
    }

    /// <summary>
    /// Writes the local tier and waits for it to land, for the two callers that have no
    /// later frame to be continued on: a suspend, and the signed-in user being removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blocking on purpose. <see cref="SaveAsync"/> is the right call everywhere else,
    /// but a continuation only runs if the process is still running when it is scheduled,
    /// and neither of these callers can promise that - the platform freezes a suspending
    /// title the instant the handler returns, and terminates one whose user has signed
    /// out. An unobserved write is the same as no write.
    /// </para>
    /// <para>
    /// <b>The local tier only.</b> The cloud tier is a remote round trip that cannot land
    /// inside either deadline, and the credentials it would need are the departing
    /// account's. Nothing is lost by skipping it: the local copy is authoritative for this
    /// device and <see cref="MergeFrom"/> is monotonic, so the next sign-in folds it back
    /// into the cloud copy rather than being overwritten by it.
    /// </para>
    /// <para>
    /// Safe to block on because the local tier is plain file IO that never touches the
    /// game thread - <c>LocalFileGameSaveService</c> awaits with
    /// <c>ConfigureAwait(false)</c> throughout, so there is no context to deadlock
    /// against.
    /// </para>
    /// </remarks>
    public void SaveLocalNow(IGameSaveService saves)
    {
        var bytes = Serialise();
        IsDirty = false;

        saves.SaveLocalAsync(SaveKey, bytes).GetAwaiter().GetResult();
    }

    /// <summary>Number of set bits in <paramref name="value"/> constrained to <paramref name="mask"/>.</summary>
    public static int CountBits(int value, int mask) => BitOperations.PopCount((uint)(value & mask));

    /// <summary>
    /// Hand-written rather than <c>JsonSerializer</c>: the GDKX build publishes Native
    /// AOT, where the reflection-based serialiser is unavailable, and five integers do
    /// not justify a source-generated context.
    /// </summary>
    private byte[] Serialise()
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("weaponsFired", WeaponsFired);
            writer.WriteNumber("buffsCollected", BuffsCollected);
            writer.WriteNumber("modesCompleted", ModesCompleted);
            writer.WriteNumber("binaryUnlocked", BinaryUnlocked);
            writer.WriteNumber("kills", Kills);
            writer.WriteNumber("asteroidsDestroyed", AsteroidsDestroyed);

            writer.WriteStartObject("confirmedPercent");

            foreach (var (achievementId, percent) in _confirmedPercent)
            {
                writer.WriteNumber(achievementId, percent);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static AchievementStats? TryParse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new AchievementStats
            {
                WeaponsFired = ReadInt(root, "weaponsFired"),
                BuffsCollected = ReadInt(root, "buffsCollected"),
                ModesCompleted = ReadInt(root, "modesCompleted"),
                BinaryUnlocked = ReadInt(root, "binaryUnlocked"),
                Kills = ReadInt(root, "kills"),
                AsteroidsDestroyed = ReadInt(root, "asteroidsDestroyed"),
                IsDirty = false,
            }.WithConfirmedPercent(root);
        }
        catch (JsonException)
        {
            // Written by another route, or truncated. Treated as no progress rather than
            // taken down: the counters rebuild from play, and the next save overwrites it.
            return null;
        }
    }

    /// <summary>
    /// Reads the confirmed-percentage map written by <see cref="Serialise"/>. Absent in
    /// records written before XR-055 durability existed, which parse as "nothing confirmed"
    /// and cause one harmless idempotent re-report per achievement.
    /// </summary>
    private AchievementStats WithConfirmedPercent(JsonElement root)
    {
        if (!root.TryGetProperty("confirmedPercent", out var map)
            || map.ValueKind != JsonValueKind.Object)
        {
            return this;
        }

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number
                && entry.Value.TryGetInt32(out var percent)
                && percent is > 0 and <= 100)
            {
                _confirmedPercent[entry.Name] = percent;
            }
        }

        IsDirty = false;
        return this;
    }

    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed > 0
                ? parsed
                : 0;
}
