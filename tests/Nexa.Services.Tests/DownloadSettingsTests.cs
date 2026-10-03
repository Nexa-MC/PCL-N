using Nexa.Services.Downloads;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask DownloadSettingsCaptureBoundedWorkers()
    {
        var port = new InMemorySettingsPort();
        var (_, settings) = PolicyFixture(port);
        AssertEqual(new MinecraftDownloadPolicy(), MinecraftDownloadPolicy.Read(settings));
        AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2"))).IsSuccess);
        AssertTrue(settings.Set(new("network.file-retry", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false"))).IsSuccess);
        var captured = MinecraftDownloadPolicy.Read(settings);
        AssertEqual(new MinecraftDownloadPolicy(2, false), captured);
        var (_, reopened) = PolicyFixture(port);
        AssertEqual(captured, MinecraftDownloadPolicy.Read(reopened));
        foreach (string invalid in new[] { "0", "65", "abc" })
            AssertFalse(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, invalid))).IsSuccess);
        AssertEqual(captured, MinecraftDownloadPolicy.Read(settings));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, peak = 0, completed = 0;
        Task batch = FileBatchProgress.RunAsync(9, async (_, token) =>
        {
            int current = Interlocked.Increment(ref active);
            InterlockedExtensionsUpdateMaximum(ref peak, current);
            if (current == 2) entered.TrySetResult();
            try { await release.Task.WaitAsync(token); Interlocked.Increment(ref completed); }
            finally { Interlocked.Decrement(ref active); }
        }, CancellationToken.None, captured.Concurrency);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
            AssertEqual(2, active);
            AssertEqual(1, MinecraftDownloadPolicy.Read(settings).Concurrency);
        }
        finally { release.TrySetResult(); await batch; }
        AssertEqual(2, peak); AssertEqual(9, completed);
        AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual(8, MinecraftDownloadPolicy.Read(settings).Concurrency);
        foreach (var pair in new[] { ("0", 1), ("4", 5), ("255", 64), ("63", 8) })
        {
            port.Save(new Dictionary<string, string> { ["ToolDownloadThread"] = pair.Item1 });
            var (_, migrated) = PolicyFixture(port);
            AssertEqual(pair.Item2, MinecraftDownloadPolicy.Read(migrated).Concurrency);
        }
    }

    private static void InterlockedExtensionsUpdateMaximum(ref int value, int candidate)
    {
        int previous;
        do { previous = Volatile.Read(ref value); if (previous >= candidate) return; }
        while (Interlocked.CompareExchange(ref value, candidate, previous) != previous);
    }

    private static async ValueTask DownloadRetrySettingPreservesIntegrityAndCommitLast()
    {
        foreach (bool retry in new[] { false, true })
        {
            var (_, settings) = PolicyFixture();
            AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
            AssertTrue(settings.Set(new("network.file-retry", SettingsLayer.Global, new(SettingsOverrideMode.Custom, retry ? "true" : "false"))).IsSuccess);
            int attempts = 0;
            var metadata = new FakeMetadata { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() };
            using var fixture = new InstallFixture(metadata, settingsPolicy: settings, connectionFactory: source =>
            {
                if (source.Contains("client/1.20.1.jar", StringComparison.Ordinal)) Interlocked.Increment(ref attempts);
                return new ServingConnection("BAD"u8.ToArray());
            });
            string root = CreateTempDirectory();
            try
            {
                var result = await fixture.Install.InstallAsync(new(root, "1.20.1", null, null));
                AssertFalse(result.IsSuccess);
                AssertEqual(retry ? 2 : 1, attempts);
                AssertFalse(File.Exists(Path.Combine(root, "versions", "1.20.1", "1.20.1.json")));
                AssertEqual(0, fixture.InstalledRoots.Count);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async ValueTask LaunchCompletionUsesDownloadRetryPolicy()
    {
        foreach (bool retry in new[] { false, true })
        {
            var (_, settings) = PolicyFixture();
            AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
            AssertTrue(settings.Set(new("network.file-retry", SettingsLayer.Global, new(SettingsOverrideMode.Custom, retry ? "true" : "false"))).IsSuccess);
            using var fixture = new CompletionFixture();
            string directory = Path.Combine(fixture.Root, "versions", "1.20.1");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(fixture.Root, "assets", "indexes"));
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "assets", "indexes", "5.json"), AssetIndexJson().ToJsonString());
            var instance = new MinecraftInstanceDescriptor("1.20.1", directory, "1.20.1",
                new MinecraftVersionDescriptor("1.20.1", "1.20.1", Path.Combine(directory, "1.20.1.json"), null, null, null, null,
                    new MinecraftVersionClassification("1.20.1", "release", MinecraftVersionCategory.Release, null)),
                new MinecraftInstanceMetadata());
            int attempts = 0;
            using var completion = new MinecraftLaunchFileCompletion(fixture.Downloads, connectionFactory: source =>
            {
                if (source.Contains("client/1.20.1.jar", StringComparison.Ordinal)) Interlocked.Increment(ref attempts);
                return new ServingConnection("BAD"u8.ToArray());
            }, settingsPolicy: settings);
            bool failed = false;
            try
            {
                await completion.CompleteAsync(fixture.Root, instance, new(VanillaJson(), []),
                    new(MinecraftLibraryOperatingSystem.Win32, "10.0.26100", true, false), "offline", null, CancellationToken.None);
            }
            catch (InvalidOperationException) { failed = true; }
            AssertTrue(failed);
            AssertEqual(retry ? 2 : 1, attempts);
            AssertFalse(File.Exists(Path.Combine(directory, "1.20.1.jar")));
        }
    }
}
