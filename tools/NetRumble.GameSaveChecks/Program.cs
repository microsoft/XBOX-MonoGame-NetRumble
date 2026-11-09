using System.Text.Json;
using NetRumble.Game.Profile;
using NetRumble.Platform;

var failures = 0;
void Check(string label, bool passed)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {label}");
    if (!passed) { failures++; }
}

var root = Path.Combine(Path.GetTempPath(), $"netrumble-game-saves-{Guid.NewGuid():N}");
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
    Check("malformed settings fail without changing the profile",
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

    var original = new AchievementStats { Kills = 25, WeaponsFired = 1 };
    original.SetConfirmedPercent("10", 25);
    saves.Cloud[AchievementStats.SaveKey] = """{"kills":50,"weaponsFired":2}"""u8.ToArray();
    await original.SyncCloudAsync(saves);
    Check("initial progress loading preserves local progress",
        original.Kills == 50 && original.WeaponsFired == 3);
    Check("loading progress preserves confirmed achievement reports", original.GetConfirmedPercent("10") == 25);
    using (var cloud = JsonDocument.Parse(saves.Cloud[AchievementStats.SaveKey]))
    {
        Check("the loaded progress is persisted",
            cloud.RootElement.GetProperty("kills").GetInt32() == 50
            && cloud.RootElement.GetProperty("weaponsFired").GetInt32() == 3);
    }
    saves.Cloud[AchievementStats.SaveKey] = """{"kills":10}"""u8.ToArray();
    await original.SyncCloudAsync(saves);
    Check("older cloud progress cannot reduce local progress", original.Kills == 50);
    saves.Cloud[AchievementStats.SaveKey] = "invalid json"u8.ToArray();
    var writes = saves.CloudWrites;
    await original.SyncCloudAsync(saves);
    Check("malformed progress is not overwritten or used to reset local progress",
        saves.CloudWrites == writes && original.Kills == 50);
    saves.LoadFailure = PlatformResult.Fail(PlatformStatus.NetworkFailure, "test network failure");
    await original.SyncCloudAsync(saves);
    Check("failed progress reads do not trigger writes", saves.CloudWrites == writes && original.Kills == 50);
    saves.LoadFailure = null;

    saves.LocalWriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = original.SaveAsync(saves);
    original.MergeFrom(new AchievementStats { Kills = 100 });
    saves.LocalWriteGate.SetResult();
    await pending;
    using (var cloud = JsonDocument.Parse(saves.Cloud[AchievementStats.SaveKey]))
    {
        Check("a delayed local write cannot push a stale progress snapshot",
            cloud.RootElement.GetProperty("kills").GetInt32() == 100);
    }
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
