using System.Diagnostics;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async Task<int> RunInterruptedNewInstallChild(string root)
    {
        string firstSource = "";
        int reported = 0;
        using var fixture = new InstallFixture(new() { VanillaJson = VanillaJson(), AssetIndexJson = AssetIndexJson() }, connectionFactory: source =>
        {
            if (source.Contains("/client/", StringComparison.Ordinal))
            { Volatile.Write(ref firstSource, source); return new ServingConnection(PayloadFor(source)); }
            return new AwaitClientReceiptConnection(root, () =>
            {
                if (Interlocked.Exchange(ref reported, 1) == 0)
                { Console.WriteLine(Volatile.Read(ref firstSource)); Console.Out.Flush(); }
            });
        });
        await fixture.Install.InstallAsync(new(root, "1.20.1", InstanceName: "resumable"));
        return 0;
    }

    private static async ValueTask NewInstallationRecoversAfterKilledDownloaderWithoutRepeatingVerifiedFile()
    {
        string root = CreateTempDirectory();
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Nexa.Services.Tests.dll"));
            start.ArgumentList.Add("--interrupted-new-install"); start.ArgumentList.Add(root);
            using var child = Process.Start(start)!;
            string? completedSource;
            try
            {
                completedSource = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                AssertTrue(completedSource?.StartsWith("https://", StringComparison.Ordinal) == true);
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            string manifest = Path.Combine(root, "versions", "resumable", "resumable.json");
            AssertFalse(File.Exists(manifest));
            string stage = Directory.GetDirectories(Path.Combine(root, ".nexa-install-jobs")).Single();
            string injected = Path.Combine(stage, "libraries", "attacker.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(injected)!);
            File.WriteAllText(injected, "untrusted leftover");
            var plan = await InstallTaskJournal.ReadAsync(root, stage, default);
            AssertTrue(plan.Command.EditFingerprint is null);
            List<string> downloaded = []; var metadata = new FakeMetadata();
            using var recovery = new InstallFixture(metadata, connectionFactory: source => { lock (downloaded) downloaded.Add(source); return new ServingConnection(PayloadFor(source)); });
            AssertTrue((await recovery.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertTrue(File.Exists(manifest)); AssertEqual(0, metadata.VanillaReads);
            AssertFalse(File.Exists(Path.Combine(root, "libraries", "attacker.jar")));
            if (downloaded.Contains(completedSource!)) throw new InvalidOperationException("Repeated: " + completedSource + " transfers: " + string.Join(",", downloaded)); AssertTrue(downloaded.Count > 0);
            AssertEqual(InstallTaskStatus.Completed, await InstallTaskJournal.ReadStatusAsync(stage, plan, default));
            int transfers = downloaded.Count;
            AssertTrue((await recovery.Install.RecoverPendingAsync(new([root]))).IsSuccess);
            AssertEqual(transfers, downloaded.Count);
            string original = File.ReadAllText(manifest);
            AssertFalse((await recovery.Install.InstallAsync(new(root, "1.20.1", InstanceName: "resumable"))).IsSuccess);
            AssertEqual(original, File.ReadAllText(manifest));
        }
        finally { Directory.Delete(root, true); }
    }

    // Concurrent transfers can start before the client finishes. Signal interruption only
    // after its durable completed-artifact receipt exists, rather than relying on file order.
    private sealed class AwaitClientReceiptConnection(string root, Action ready) : IDownloadConnection
    {
        public async ValueTask<DownloadConnectionInfo> StartAsync(long offset, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var paths = Directory.GetFiles(Path.Combine(root, ".nexa-install-jobs"), "1.20.1.jar", SearchOption.AllDirectories);
                foreach (string path in paths)
                    if (await RecoveryRecordAuthority.IsAuthorizedFileAsync(path, cancellationToken))
                    {
                        ready();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                await Task.Delay(10, cancellationToken);
            }
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
