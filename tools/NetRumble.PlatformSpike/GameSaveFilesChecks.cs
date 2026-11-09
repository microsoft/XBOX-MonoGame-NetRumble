using System.Reflection;
using System.Runtime.CompilerServices;
using GDK.Net.Users;
using NetRumble.Platform;
using NetRumble.Platform.GameCore;
using NetRumble.Platform.GameCore.Services;

namespace NetRumble.PlatformSpike;

internal static class GameSaveFilesChecks
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task<int> Run()
    {
        var failures = 0;
        void Check(string label, bool passed)
        {
            Console.WriteLine($"    {(passed ? "PASS" : "FAIL")} {label}");
            if (!passed) { failures++; }
        }
        Console.WriteLine("XGameSaveFiles adapter checks (cached test folders, no native sign-in)");
        var root = Path.Combine(Path.GetTempPath(), $"netrumble-savefiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var titleId in new[] { PlayFabConfiguration.PlaceholderTitleId, "A1B2C" })
            {
                var configuration = new PlayFabConfiguration { TitleId = titleId };
                await using var provider = new GameCorePlatformProvider(root, configuration);
                var label = configuration.IsPlaceholder ? "unconfigured" : "configured";
                Check($"GameCore uses XGameSaveFiles with {label} PlayFab",
                    provider.GameSaves is GameCoreGameSaveService);
                Check($"GameCore saves require connected storage with {label} PlayFab",
                    (await provider.GameSaves.SaveCloudAsync("settings", new byte[] { 1 })).Status
                        == PlatformStatus.Unavailable);
            }
            using var fixture = new Fixture(root);
            var saves = fixture.Saves;
            var missing = await saves.LoadCloudAsync("settings");
            Check("first run returns an empty successful read", missing.Succeeded && missing.Value!.Length == 0);
            var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            Check("save writes through the file backend", (await saves.SaveCloudAsync("settings", data)).Succeeded);
            var path = Path.Combine(fixture.Folder, "NetRumbleSave", "settings");
            Check("container and blob names retain the existing cloud layout",
                File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(data));
            Check("read returns the exact saved bytes",
                (await saves.LoadCloudAsync("settings")).Value!.SequenceEqual(data));
            var legacy = Path.Combine(fixture.Folder, "NetRumbleSave", "achievement_stats");
            await File.WriteAllBytesAsync(legacy, data);
            Check("existing mapped blobs are read without a new extension or format",
                (await saves.LoadCloudAsync("achievement_stats")).Value!.SequenceEqual(data));

            var limit = new byte[GameCoreGameSaveService.MaxCloudBytes];
            Check("256 KiB boundary remains writable", (await saves.SaveCloudAsync("boundary", limit)).Succeeded);
            Check("256 KiB boundary remains readable",
                (await saves.LoadCloudAsync("boundary")).Value!.Length == limit.Length);
            Check("oversized writes fail explicitly",
                (await saves.SaveCloudAsync("boundary", new byte[limit.Length + 1])).Failed);
            Check("rejected writes preserve the existing file", File.ReadAllBytes(
                Path.Combine(fixture.Folder, "NetRumbleSave", "boundary")).Length == limit.Length);
            await File.WriteAllBytesAsync(Path.Combine(fixture.Folder, "NetRumbleSave", "oversized"),
                new byte[limit.Length + 1]);
            Check("oversized reads fail rather than allocating unbounded save data",
                (await saves.LoadCloudAsync("oversized")).Failed);
            Check("zero-byte saves remain supported", (await saves.SaveCloudAsync("empty", ReadOnlyMemory<byte>.Empty)).Succeeded
                && (await saves.LoadCloudAsync("empty")).Value!.Length == 0);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Check("canceled writes remain canceled",
                    (await saves.SaveCloudAsync("settings", new byte[] { 9 }, canceled.Token)).WasCanceled);
                Check("canceled writes preserve the live save", File.ReadAllBytes(path).SequenceEqual(data));
                Check("canceled reads remain canceled",
                    (await saves.LoadCloudAsync("settings", canceled.Token)).WasCanceled);
            }
            foreach (var key in new[] { "", "..", "../escape", @"..\escape", @"C:\escape", "a:b",
                "CON", "nul.dat", "settings.", "settings ", new string('a', 66) })
            {
                Check($"invalid file key '{key}' is rejected by reads and writes",
                    (await saves.LoadCloudAsync(key)).Failed
                    && (await saves.SaveCloudAsync(key, data)).Failed);
            }
            Check("65-character keys are supported",
                (await saves.SaveCloudAsync(new string('a', 65), data)).Succeeded);
            Directory.CreateDirectory(Path.Combine(fixture.Folder, "NetRumbleSave", "blocked"));
            Check("file I/O failures are not reported as missing saves",
                (await saves.LoadCloudAsync("blocked")).Failed);
            Check("failed replacement reports failure",
                (await saves.SaveCloudAsync("blocked", data)).Failed);
            await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(i => saves.SaveCloudAsync("concurrent", Enumerable.Repeat((byte)i, 4096).ToArray())));
            var concurrent = (await saves.LoadCloudAsync("concurrent")).Value!;
            Check("concurrent writes leave one complete file",
                concurrent.Length == 4096 && concurrent.All(b => b == concurrent[0]));
            Check("temporary files are cleaned after success and failure",
                Directory.GetFiles(Path.Combine(fixture.Folder, "NetRumbleSave"), "nr-*.tmp").Length == 0);

            var gate = (SemaphoreSlim)Get(saves, "_cloudGate")!;
            await gate.WaitAsync();
            var queued = saves.SaveCloudAsync("settings", new byte[] { 7 });
            fixture.Bind(Path.Combine(root, "account-b"));
            gate.Release();
            Check("queued writes cannot follow an account switch", (await queued).WasCanceled);
            Check("an account switch leaves both accounts' data untouched",
                File.ReadAllBytes(path).SequenceEqual(data)
                && !Directory.Exists(Path.Combine(fixture.Folder, "NetRumbleSave")));
            Check("the new account has an independent save folder",
                (await saves.SaveCloudAsync("settings", new byte[] { 8 })).Succeeded
                && (await saves.LoadCloudAsync("settings")).Value!.SequenceEqual(new byte[] { 8 }));

            Set(fixture.Identity, "_user", null);
            typeof(GameCoreGameSaveService).GetMethod("OnLifecycleChanged", PrivateInstance)!
                .Invoke(saves, [PlatformLifecycleEvent.Resumed]);
            Check("resume invalidates the previously synchronized folder", Get(saves, "_folder") is null);
            Check("signed-out reads cannot reuse a cached account folder",
                (await saves.LoadCloudAsync("settings")).Status == PlatformStatus.Unavailable);
            await gate.WaitAsync();
            var shutdown = saves.ShutdownAsync();
            Check("shutdown waits for the save-operation gate", !shutdown.IsCompleted);
            gate.Release();
            await shutdown;
            Check("shutdown refuses further writes", (await saves.SaveCloudAsync("settings", data)).WasCanceled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("    SKIP authenticated folder acquisition, system conflict UI and cross-device roaming.");
        return failures == 0 ? 0 : 1;
    }

    private static object? Get(object target, string name)
        => target.GetType().GetField(name, PrivateInstance)!.GetValue(target);

    private static void Set(object target, string name, object? value)
        => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);

    private sealed class Fixture : IDisposable
    {
        internal GameCoreIdentityService Identity { get; }
        internal GameCoreGameSaveService Saves { get; }
        internal string Folder { get; private set; } = "";

        internal Fixture(string root)
        {
            var runtime = new GameCoreRuntime();
            typeof(GameCoreRuntime).GetProperty(nameof(runtime.IsInitialized))!.SetValue(runtime, true);
            Identity = new GameCoreIdentityService(runtime);
            Saves = new GameCoreGameSaveService(runtime, Identity, root, () => "local-account");
            Bind(Path.Combine(root, "account-a"));
        }

        internal void Bind(string folder)
        {
            typeof(GameCoreGameSaveService).GetMethod("InvalidateFolder", PrivateInstance)!.Invoke(Saves, null);
            // Reference-only identities and cached folders avoid all native handle access.
            var user = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
            Set(Identity, "_user", user);
            var contextType = typeof(GameCoreGameSaveService).GetNestedType("SaveFolder", BindingFlags.NonPublic)!;
            var context = Activator.CreateInstance(contextType, [user, (long)Get(Saves, "_revision")!, folder]);
            Set(Saves, "_folder", context);
            Folder = folder;
        }

        public void Dispose() => Saves.Dispose();
    }
}
