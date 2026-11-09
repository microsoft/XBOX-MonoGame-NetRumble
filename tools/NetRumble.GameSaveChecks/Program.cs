using System.Text.Json;
using System.Reflection;
using NetRumble.Game;
using NetRumble.Game.Profile;
using NetRumble.Platform;
using NetRumble.Platform.Offline;

var failures = 0;
void Check(string label, bool passed)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {label}");
    if (!passed) { failures++; }
}

var root = Path.Combine(Path.GetTempPath(), $"netrumble-reconciliation-{Guid.NewGuid():N}");
try
{
    var saves = new TestSaves();
    var firstRunRoot = Path.Combine(root, "first-run");
    var firstRunProfile = new PlayerProfile(root: firstRunRoot);
    var firstRunSettings = new CloudSettingsSync(saves, firstRunProfile);
    firstRunProfile.AttachCloudSave(firstRunSettings);
    saves.Cloud[CloudSettingsSync.SaveKey] = """{"masterVolume":0.8}"""u8.ToArray();
    firstRunProfile.Rehome("second-device-player");
    Check("first-run rehome creates a local settings file", Directory.GetFiles(firstRunRoot, "*.cfg").Length == 1);
    Check("first-run defaults never push before the initial cloud pull", saves.CloudWrites == 0);
    Check("the first cloud pull preserves the other device's settings",
        (await firstRunSettings.PullAsync()).Succeeded && firstRunProfile.MasterVolume == 0.8f);

    var profile = new PlayerProfile("test-player", root) { MasterVolume = 0.2f };
    var settings = new CloudSettingsSync(saves, profile);
    profile.AttachCloudSave(settings);
    saves.Cloud[CloudSettingsSync.SaveKey] = """{"masterVolume":0.8}"""u8.ToArray();
    Check("downloaded settings replace stale in-memory settings",
        (await settings.PullAsync()).Succeeded && profile.MasterVolume == 0.8f);
    Check("applying cloud settings does not push them back", saves.CloudWrites == 0);
    saves.Cloud.Remove(CloudSettingsSync.SaveKey);
    var absent = await settings.PullAsync();
    Check("absent settings are a successful no-op", absent.Succeeded && !absent.Value);
    saves.Cloud[CloudSettingsSync.SaveKey] = "invalid json"u8.ToArray();
    Check("malformed settings fail reconciliation without changing the profile",
        (await settings.PullAsync()).Failed && profile.MasterVolume == 0.8f);
    saves.LoadFailure = PlatformResult.Fail(PlatformStatus.NetworkFailure, "test network failure");
    Check("failed settings reads remain explicit failures", (await settings.PullAsync()).Failed);
    saves.LoadFailure = null;
    saves.Cloud[CloudSettingsSync.SaveKey] = """{"masterVolume":0.1}"""u8.ToArray();
    using (var canceled = new CancellationTokenSource())
    {
        saves.OnCloudRead = canceled.Cancel;
        var rejected = false;
        try { await settings.PullAsync(canceled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Check("a session canceled after reading cannot apply old-account settings",
            rejected && profile.MasterVolume == 0.8f);
        saves.OnCloudRead = null;
    }

    await using var platform = new OfflinePlatformProvider(root);
    var achievements = new AchievementTracker(platform, saves);
    var original = achievements.Stats;
    original.Kills = 25;
    original.WeaponsFired = 1;
    original.SetConfirmedPercent("10", 25);
    saves.Cloud[AchievementStats.SaveKey] = """{"kills":50,"weaponsFired":2}"""u8.ToArray();
    Check("resume merges downloaded progress without replacing the live stats",
        (await achievements.ReconcileCloudAsync()).Succeeded
        && ReferenceEquals(original, achievements.Stats)
        && original.Kills == 50 && original.WeaponsFired == 3);
    Check("resume preserves confirmed achievement reports", original.GetConfirmedPercent("10") == 25);
    await achievements.FlushAsync(force: true);
    using (var cloud = JsonDocument.Parse(saves.Cloud[AchievementStats.SaveKey]))
    {
        Check("the merged progress is written back after reconciliation",
            cloud.RootElement.GetProperty("kills").GetInt32() == 50
            && cloud.RootElement.GetProperty("weaponsFired").GetInt32() == 3);
    }
    saves.Cloud[AchievementStats.SaveKey] = """{"kills":10}"""u8.ToArray();
    Check("older cloud progress cannot reduce local progress",
        (await achievements.ReconcileCloudAsync()).Succeeded && original.Kills == 50);
    saves.Cloud[AchievementStats.SaveKey] = "invalid json"u8.ToArray();
    Check("malformed progress fails reconciliation without resetting local progress",
        (await achievements.ReconcileCloudAsync()).Failed && original.Kills == 50);
    saves.LoadFailure = PlatformResult.Fail(PlatformStatus.NetworkFailure, "test network failure");
    Check("failed progress reads remain explicit failures",
        (await achievements.ReconcileCloudAsync()).Failed && original.Kills == 50);
    saves.LoadFailure = null;

    saves.LocalWriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = original.SaveAsync(saves);
    original.MergeFrom(new AchievementStats { Kills = 100 });
    saves.LocalWriteGate.SetResult();
    await pending;
    using (var cloud = JsonDocument.Parse(saves.Cloud[AchievementStats.SaveKey]))
    {
        Check("a delayed local write cannot push a pre-reconciliation cloud snapshot",
            cloud.RootElement.GetProperty("kills").GetInt32() == 100);
    }

    Task RetryAfter(Task active, Func<bool> shouldRetry, Func<Task> retry)
        => (Task)typeof(LifecycleCoordinator).GetMethod("RetryReconciliationAfterAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [active, shouldRetry, retry])!;
    var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var retries = 0;
    var needsReconciliation = true;
    Task Retry()
    {
        retries++;
        needsReconciliation = false;
        return Task.CompletedTask;
    }
    var queued = RetryAfter(active.Task, () => needsReconciliation, Retry);
    var duplicate = RetryAfter(queued, () => needsReconciliation, Retry);
    Check("connectivity recovery waits behind active reconciliation", retries == 0 && !queued.IsCompleted);
    active.SetResult();
    await duplicate;
    Check("connectivity recovery is retained and deduplicated after the active attempt", retries == 1);

    active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var online = true;
    queued = RetryAfter(active.Task, () => online, Retry);
    online = false;
    active.SetResult();
    await queued;
    Check("queued retries recheck connectivity before starting", retries == 1);

    active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var generation = 1;
    var capturedGeneration = generation;
    queued = RetryAfter(active.Task, () => generation == capturedGeneration, Retry);
    generation++;
    active.SetResult();
    await queued;
    Check("a newer resume supersedes retries queued for the previous session", retries == 1);
}
finally
{
    Directory.Delete(root, recursive: true);
}
return failures == 0 ? 0 : 1;

sealed class TestSaves : IGameSaveService
{
    internal Dictionary<string, byte[]> Cloud { get; } = new();
    internal int CloudWrites { get; private set; }
    internal PlatformResult? LoadFailure { get; set; }
    internal Action? OnCloudRead { get; set; }
    internal TaskCompletionSource? LocalWriteGate { get; set; }

    public async Task<PlatformResult> SaveLocalAsync(
        string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (LocalWriteGate is { } gate) { await gate.Task.WaitAsync(cancellationToken); }
        return PlatformResult.Ok();
    }

    public Task<PlatformResult<byte[]>> LoadLocalAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<byte[]>.Ok([]));

    public Task<PlatformResult> SaveCloudAsync(
        string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloudWrites++;
        Cloud[key] = data.ToArray();
        return Task.FromResult(PlatformResult.Ok());
    }

    public Task<PlatformResult<byte[]>> LoadCloudAsync(string key, CancellationToken cancellationToken = default)
    {
        OnCloudRead?.Invoke();
        return Task.FromResult(LoadFailure is { } failure
            ? PlatformResult<byte[]>.Fail(failure.Status, failure.Message ?? "test failure")
            : PlatformResult<byte[]>.Ok(Cloud.TryGetValue(key, out var bytes) ? bytes : []));
    }
}
