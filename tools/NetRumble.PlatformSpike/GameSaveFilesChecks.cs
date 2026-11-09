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
            Task<PlatformResult> Save(string key, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
                => fixture.Complete(saves.SaveCloudAsync(key, bytes, token));
            Task<PlatformResult<byte[]>> Load(string key, CancellationToken token = default)
                => fixture.Complete(saves.LoadCloudAsync(key, token));
            var missing = await Load("settings");
            Check("first run returns an empty successful read", missing.Succeeded && missing.Value!.Length == 0);
            var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            Check("save writes through the file backend", (await Save("settings", data)).Succeeded);
            var path = Path.Combine(fixture.Folder, "NetRumbleSave", "settings");
            Check("container and blob names retain the existing cloud layout",
                File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(data));
            Check("read returns the exact saved bytes",
                (await Load("settings")).Value!.SequenceEqual(data));
            var legacy = Path.Combine(fixture.Folder, "NetRumbleSave", "achievement_stats");
            await File.WriteAllBytesAsync(legacy, data);
            Check("existing mapped blobs are read without a new extension or format",
                (await Load("achievement_stats")).Value!.SequenceEqual(data));

            var limit = new byte[GameCoreGameSaveService.MaxCloudBytes];
            Check("256 KiB boundary remains writable", (await Save("boundary", limit)).Succeeded);
            Check("256 KiB boundary remains readable",
                (await Load("boundary")).Value!.Length == limit.Length);
            Check("oversized writes fail explicitly",
                (await Save("boundary", new byte[limit.Length + 1])).Failed);
            Check("rejected writes preserve the existing file", File.ReadAllBytes(
                Path.Combine(fixture.Folder, "NetRumbleSave", "boundary")).Length == limit.Length);
            await File.WriteAllBytesAsync(Path.Combine(fixture.Folder, "NetRumbleSave", "oversized"),
                new byte[limit.Length + 1]);
            Check("oversized reads fail rather than allocating unbounded save data",
                (await Load("oversized")).Failed);
            Check("zero-byte saves remain supported", (await Save("empty", ReadOnlyMemory<byte>.Empty)).Succeeded
                && (await Load("empty")).Value!.Length == 0);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                Check("canceled writes remain canceled",
                    (await Save("settings", new byte[] { 9 }, canceled.Token)).WasCanceled);
                Check("canceled writes preserve the live save", File.ReadAllBytes(path).SequenceEqual(data));
                Check("canceled reads remain canceled",
                    (await Load("settings", canceled.Token)).WasCanceled);
            }
            foreach (var key in new[] { "", "..", "../escape", @"..\escape", @"C:\escape", "a:b",
                "CON", "nul.dat", "settings.", "settings ", new string('a', 66) })
            {
                Check($"invalid file key '{key}' is rejected by reads and writes",
                    (await Load(key)).Failed
                    && (await Save(key, data)).Failed);
            }
            Check("65-character keys are supported",
                (await Save(new string('a', 65), data)).Succeeded);
            Directory.CreateDirectory(Path.Combine(fixture.Folder, "NetRumbleSave", "blocked"));
            Check("file I/O failures are not reported as missing saves",
                (await Load("blocked")).Failed);
            Check("failed replacement reports failure",
                (await Save("blocked", data)).Failed);
            await fixture.Complete(Task.WhenAll(Enumerable.Range(0, 8)
                .Select(i => saves.SaveCloudAsync("concurrent", Enumerable.Repeat((byte)i, 4096).ToArray()))));
            var concurrent = (await Load("concurrent")).Value!;
            Check("concurrent writes leave one complete file",
                concurrent.Length == 4096 && concurrent.All(b => b == concurrent[0]));
            Check("temporary files are cleaned after success and failure",
                Directory.GetFiles(Path.Combine(fixture.Folder, "NetRumbleSave"), "nr-*.tmp").Length == 0);

            var gate = (SemaphoreSlim)Get(saves, "_cloudGate")!;
            await gate.WaitAsync();
            var queued = saves.SaveCloudAsync("settings", new byte[] { 7 });
            fixture.Bind(Path.Combine(root, "account-b"));
            gate.Release();
            Check("queued writes cannot follow an account switch", (await fixture.Complete(queued)).WasCanceled);
            Check("an account switch leaves both accounts' data untouched",
                File.ReadAllBytes(path).SequenceEqual(data)
                && !Directory.Exists(Path.Combine(fixture.Folder, "NetRumbleSave")));
            Check("the new account has an independent save folder",
                (await Save("settings", new byte[] { 8 })).Succeeded
                && (await Load("settings")).Value!.SequenceEqual(new byte[] { 8 }));

            var pumpWrite = saves.SaveCloudAsync("pump", data);
            await gate.WaitAsync();
            gate.Release();
            Check("file writes do not publish completion without a pump",
                File.Exists(Path.Combine(fixture.Folder, "NetRumbleSave", "pump")) && !pumpWrite.IsCompleted);
            Check("pumping publishes the completed write", (await fixture.Complete(pumpWrite)).Succeeded);
            var pumpRead = saves.LoadCloudAsync("pump");
            await gate.WaitAsync();
            gate.Release();
            Check("file reads do not publish completion without a pump", !pumpRead.IsCompleted);
            Check("pumping publishes the completed read", (await fixture.Complete(pumpRead)).Value!.SequenceEqual(data));

            await gate.WaitAsync();
            var suspendedWrite = saves.SaveCloudAsync("settings", new byte[] { 99 });
            fixture.Lifecycle(PlatformLifecycleEvent.Suspending);
            Check("suspension invalidates the synchronized folder", Get(saves, "_folder") is null);
            Check("new suspended-session writes are refused", (await Save("settings", data)).Failed);
            Check("new suspended-session reads are refused", (await Load("settings")).Failed);
            fixture.Resume();
            gate.Release();
            Check("a pre-suspend queued write cannot commit after resume",
                (await fixture.Complete(suspendedWrite)).WasCanceled
                && (await Load("settings")).Value!.SequenceEqual(new byte[] { 8 }));
            Check("resumed-session writes wait for title reconciliation", (await Save("settings", data)).Failed);
            var resumeRevision = Get(saves, "_revision");
            typeof(GameCoreGameSaveService).GetMethod("OnUserChanged", PrivateInstance)!
                .Invoke(saves, [new PlatformUser { LocalId = "local-account", DisplayName = "Refreshed player" }]);
            Check("same-account profile refresh cannot release or invalidate the resume barrier",
                saves.RequiresReconciliation && Equals(resumeRevision, Get(saves, "_revision")));
            var failedReconcile = await fixture.Complete(saves.ReconcileAsync(
                _ => Task.FromResult(PlatformResult.Fail(PlatformStatus.NetworkFailure, "test failure"))));
            Check("failed reconciliation preserves the write barrier",
                failedReconcile.Failed && (await Save("settings", data)).Failed);
            var reconciled = false;
            var callbackStarted = false;
            var reconciliation = saves.ReconcileAsync(async token =>
            {
                callbackStarted = true;
                var settings = await saves.LoadCloudAsync("settings", token);
                var stats = await saves.LoadCloudAsync("achievement_stats", token);
                Check("reconciliation can read synchronized settings and stats",
                    settings.Succeeded && stats.Succeeded);
                Check("cloud writes stay blocked throughout the title callback",
                    (await saves.SaveCloudAsync("settings", data, token)).Failed);
                reconciled = true;
                return PlatformResult.Ok();
            });
            Check("the title reconciliation callback starts only from the runtime pump", !callbackStarted);
            Check("successful reconciliation releases writes only after the callback",
                (await fixture.Complete(reconciliation)).Succeeded && reconciled
                && (await Save("settings", data)).Succeeded);
            fixture.Lifecycle(PlatformLifecycleEvent.Suspending);
            fixture.Resume();
            var superseded = saves.ReconcileAsync(_ =>
            {
                fixture.Bind(Path.Combine(root, "account-c"));
                return Task.FromResult(PlatformResult.Ok());
            });
            Check("an account change cancels reconciliation rather than enabling new-account writes",
                (await fixture.Complete(superseded)).WasCanceled
                && (await Save("settings", data)).Failed);

            Set(fixture.Identity, "_user", null);
            typeof(GameCoreGameSaveService).GetMethod("OnLifecycleChanged", PrivateInstance)!
                .Invoke(saves, [PlatformLifecycleEvent.Resumed]);
            Check("resume invalidates the previously synchronized folder", Get(saves, "_folder") is null);
            Check("signed-out reads cannot reuse a cached account folder",
                (await Load("settings")).Status == PlatformStatus.Unavailable);
            var unpumped = saves.LoadCloudAsync("settings");
            await gate.WaitAsync();
            var shutdown = saves.ShutdownAsync();
            Check("shutdown waits for the save-operation gate", !shutdown.IsCompleted);
            gate.Release();
            await shutdown;
            Check("shutdown drains workers without awaiting unpumped public completions", !unpumped.IsCompleted);
            await fixture.Complete(unpumped);
            Check("shutdown refuses further writes", (await Save("settings", data)).WasCanceled);
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
        internal GameCoreRuntime Runtime { get; }
        internal GameCoreGameSaveService Saves { get; }
        internal string Folder { get; private set; } = "";

        internal Fixture(string root)
        {
            Runtime = new GameCoreRuntime();
            typeof(GameCoreRuntime).GetProperty(nameof(Runtime.IsInitialized))!.SetValue(Runtime, true);
            Identity = new GameCoreIdentityService(Runtime);
            Saves = new GameCoreGameSaveService(Runtime, Identity, root, () => "local-account");
            Bind(Path.Combine(root, "account-a"));
        }

        internal void Bind(string folder)
        {
            typeof(GameCoreGameSaveService).GetMethod("InvalidateFolder", PrivateInstance)!.Invoke(Saves, null);
            // Reference-only identities and cached folders avoid all native handle access.
            var user = (User)RuntimeHelpers.GetUninitializedObject(typeof(User));
            Set(Identity, "_user", user);
            Set(Saves, "_saveUser", user);
            var contextType = typeof(GameCoreGameSaveService).GetNestedType("SaveFolder", BindingFlags.NonPublic)!;
            var context = Activator.CreateInstance(contextType, [user, (long)Get(Saves, "_revision")!, folder]);
            Set(Saves, "_folder", context);
            Folder = folder;
        }

        internal void Lifecycle(PlatformLifecycleEvent change)
            => typeof(GameCoreGameSaveService).GetMethod("OnLifecycleChanged", PrivateInstance)!
                .Invoke(Saves, [change]);

        internal void Resume()
        {
            Set(Identity, "_user", null);
            Lifecycle(PlatformLifecycleEvent.Resumed);
            Bind(Folder);
        }

        internal async Task<T> Complete<T>(Task<T> work)
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (!work.IsCompleted)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new TimeoutException("Save-file check timed out while pumping.");
                }
                Runtime.Dispatcher.Drain();
                await Task.Delay(1);
            }
            return await work;
        }

        public void Dispose() => Saves.Dispose();
    }
}
