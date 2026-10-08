using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Platform;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask OrdinaryGamesPreventOverlayChangesWithoutBlockingLauncherExit()
    {
        string root = CreateTempDirectory();
        try
        {
            string game = Path.Combine(root, "game"), mods = Path.Combine(game, "mods"); Directory.CreateDirectory(mods);
            File.WriteAllText(Path.Combine(mods, "ordinary.jar"), "ordinary");
            await using var processes = new MinecraftProcessService(new LongLivedProcessPort());
            var executor = new MinecraftLaunchExecutor(new HookFixtureHost(processes, []));
            var session = await executor.ExecuteAsync(HookPlan(game), "ordinary-fixture");
            AssertFalse(executor.HasPendingFinalization);
            await executor.WaitForFinalizationAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await using (var otherReader = await MinecraftGameDirectoryUseLease.AcquireAsync(game))
                AssertTrue(File.Exists(Path.Combine(mods, "ordinary.jar")));
            bool protectedGame = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(true)); }
            catch (IOException) { protectedGame = true; }
            AssertTrue(protectedGame); AssertEqual("ordinary", File.ReadAllText(Path.Combine(mods, "ordinary.jar")));
            AssertFalse(Directory.Exists(Path.Combine(game, ".nexacl-launch-overlay")));
            session.Cancel(); await session.WaitForExitAsync();
            MinecraftLaunchOverlayLease? recovered = null;
            long started = Stopwatch.GetTimestamp();
            while (recovered is null)
            {
                try { recovered = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(true)); }
                catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5)) { await Task.Delay(10); }
            }
            await using (recovered)
            {
                AssertFalse(File.Exists(Path.Combine(mods, "ordinary.jar")));
                bool protectedOverlay = false;
                try { await using var reader = await MinecraftGameDirectoryUseLease.AcquireAsync(game); }
                catch (IOException) { protectedOverlay = true; }
                AssertTrue(protectedOverlay);
            }
            AssertEqual("ordinary", File.ReadAllText(Path.Combine(mods, "ordinary.jar")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask PersistentGameReceiptsProtectAfterLeaseLossAndRejectUncertainStartup()
    {
        string root = CreateTempDirectory();
        try
        {
            using var current = Process.GetCurrentProcess();
            var reader = await MinecraftGameDirectoryUseLease.AcquireAsync(root);
            await reader.BindProcessAsync(current);
            await reader.DisposeAsync(); // Models the launcher losing its FD while the game survives.
            string directory = Path.Combine(root, ".nexacl-game-uses");
            string receiptPath = Directory.GetFiles(directory).Single();
            bool protectedReceipt = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(root, new(true)); }
            catch (IOException) { protectedReceipt = true; }
            AssertTrue(protectedReceipt); AssertFalse(Directory.Exists(Path.Combine(root, "mods")));
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!.AsObject();
            receipt["processStartTicks"] = current.StartTime.ToUniversalTime().Ticks - 1; // PID reuse cannot keep a stale receipt active.
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
            await using (var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(root, new(true)))
                AssertEqual(0, Directory.GetFiles(directory).Length);
            receipt["phase"] = "preparing"; receipt["processId"] = int.MaxValue; receipt["processStartTicks"] = 1L;
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
            bool protectedUncertain = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(root, new(true)); }
            catch (IOException error) { protectedUncertain = error.Message.Contains("unbound", StringComparison.Ordinal); }
            AssertTrue(protectedUncertain); AssertTrue(File.Exists(receiptPath));
            AssertFalse(Directory.Exists(Path.Combine(root, "mods")));
            receipt["phase"] = "running";
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
            var processes = new LaunchProcessIdentityFixture(new(PlatformProcessIdentityState.Unknown));
            bool unknownDenied = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(root, new(true), processIdentity: processes); }
            catch (IOException) { unknownDenied = true; }
            AssertTrue(unknownDenied); AssertTrue(File.Exists(receiptPath));
            processes.Observation = new(PlatformProcessIdentityState.Exited);
            await using (var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(root, new(true), processIdentity: processes))
                AssertEqual(0, Directory.GetFiles(directory).Length);
            File.Delete(receiptPath);
            await File.WriteAllTextAsync(receiptPath, new string('x', 4097));
            bool bounded = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(root, new(true)); }
            catch (InvalidDataException) { bounded = true; }
            AssertTrue(bounded); AssertFalse(Directory.Exists(Path.Combine(root, "mods")));
        }
        finally { Directory.Delete(root, true); }
    }
}
