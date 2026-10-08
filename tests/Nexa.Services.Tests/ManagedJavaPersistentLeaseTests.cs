using System.Buffers.Binary;
using Nexa.Services.Minecraft.Java;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ManagedJavaBoundLeaseSurvivesOwnerReleaseUntilProcessExit()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var removal = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        var processPort = new LongLivedProcessPort();
        using var child = await processPort.StartAsync(new());
        try
        {
            var use = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
            AssertTrue(use is not null);
            using (use) use!.BindProcess(child.Id);
            // Simulate a launcher that releases its file lock before the game exits.
            string uses = Path.Combine(fixture.RuntimeRoot, ".nexa-java-uses", fixture.Plan.ComponentName);
            string recordPath = Directory.GetFiles(uses).Single();
            byte[] identity = await File.ReadAllBytesAsync(recordPath);
            AssertEqual(20, identity.Length);
            AssertEqual(child.Id, BinaryPrimitives.ReadInt32LittleEndian(identity.AsSpan(8, 4)));
            AssertEqual(child.StartTime.ToUniversalTime().Ticks, BinaryPrimitives.ReadInt64LittleEndian(identity.AsSpan(12)));
            var denied = await fixture.Management.ManageAsync(removal);
            AssertFalse(denied.IsSuccess); AssertEqual(XsrErrorKind.Rejected, denied.Error!.Kind);
            AssertTrue(File.Exists(recordPath)); AssertEqual("owned java", await File.ReadAllTextAsync(fixture.Executable));
            byte[] retained = await File.ReadAllBytesAsync(recordPath);
            AssertTrue(identity.SequenceEqual(retained));

            // A real replacement reaches the same guard before it downloads or publishes.
            var replacement = fixture.Plan with
            {
                VersionName = "21.0.2",
                Files = [fixture.Plan.Files.Single() with
                { Sha1 = "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d", Size = 5, Executable = true }]
            };
            var pending = await JavaInstallJournal.CreateAsync(fixture.RuntimeRoot, fixture.Plan.ComponentName, default);
            await pending.SavePlanAsync(replacement, default);
            var metadata = new FakeInstallerMetadataProvider(JavaRuntimeInstaller.DetectPlatform().ToMojangKey(),
                replacement.Files.Single().RelativePath, replacement.Files.Single().Sha1);
            using var client = new HttpClient(new StaticHttpMessageHandler("hello"));
            using var installer = new JavaRuntimeInstaller(new JavaRuntimeDownloadPlanService(metadata), client);
            bool installationDenied = false;
            try { await installer.InstallAsync(fixture.Plan.ComponentName, fixture.RuntimeRoot); }
            catch (IOException error) { installationDenied = error.Message.Contains("使用", StringComparison.Ordinal); }
            AssertTrue(installationDenied); AssertFalse(child.HasExited); AssertTrue(File.Exists(recordPath));
            AssertEqual("owned java", await File.ReadAllTextAsync(fixture.Executable));

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(fixture.Executable, await installer.InstallAsync(fixture.Plan.ComponentName, fixture.RuntimeRoot));
            AssertFalse(File.Exists(recordPath)); AssertEqual("hello", await File.ReadAllTextAsync(fixture.Executable));
            File.SetLastWriteTimeUtc(Path.Combine(pending.Stage, "complete"), DateTime.UtcNow.AddMinutes(1));
            var current = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
            AssertTrue((await fixture.Management.ManageAsync(removal with { ExpectedManagedIdentity = current.Identity })).IsSuccess);
            AssertFalse(File.Exists(fixture.Executable));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async ValueTask ManagedJavaLeaseRejectsUnknownAndMalformedRecords()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var removal = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        var use = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
        using (use) use!.BindProcess(Environment.ProcessId);
        string uses = Path.Combine(fixture.RuntimeRoot, ".nexa-java-uses", fixture.Plan.ComponentName);
        string path = Directory.GetFiles(uses).Single();
        byte[] valid = await File.ReadAllBytesAsync(path);
        List<byte[]> malformed = [[], valid[..^1], [.. valid, 0]];
        byte[] wrongMagic = (byte[])valid.Clone(); wrongMagic[0] ^= 0xff; malformed.Add(wrongMagic);
        byte[] unknownVersion = (byte[])valid.Clone(); unknownVersion[7] = (byte)'2'; malformed.Add(unknownVersion);
        byte[] invalidPid = (byte[])valid.Clone(); BinaryPrimitives.WriteInt32LittleEndian(invalidPid.AsSpan(8, 4), 0); malformed.Add(invalidPid);
        byte[] invalidTicks = (byte[])valid.Clone(); BinaryPrimitives.WriteInt64LittleEndian(invalidTicks.AsSpan(12), -1); malformed.Add(invalidTicks);
        byte[] impossibleTicks = (byte[])valid.Clone(); BinaryPrimitives.WriteInt64LittleEndian(impossibleTicks.AsSpan(12), long.MaxValue); malformed.Add(impossibleTicks);
        foreach (byte[] record in malformed)
        {
            await File.WriteAllBytesAsync(path, record);
            var result = await fixture.Management.ManageAsync(removal);
            AssertFalse(result.IsSuccess); AssertEqual(XsrErrorKind.Rejected, result.Error!.Kind);
            byte[] retained = await File.ReadAllBytesAsync(path);
            AssertTrue(record.SequenceEqual(retained)); AssertTrue(File.Exists(fixture.Executable));
            AssertEqual(0L, fixture.Registry.Read().Revision);
        }
        // Explicit fixture recovery is required even for an orphaned empty record.
        File.Delete(path);
        var unresolved = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
        AssertTrue(unresolved is not null);
        bool bindingRejected = false;
        try { unresolved!.BindProcess(0); }
        catch (ArgumentOutOfRangeException) { bindingRejected = true; }
        finally { unresolved!.Dispose(); }
        AssertTrue(bindingRejected);
        path = Directory.GetFiles(uses).Single();
        AssertEqual(0L, new FileInfo(path).Length);
        AssertFalse((await fixture.Management.ManageAsync(removal)).IsSuccess); AssertTrue(File.Exists(path));
        File.Delete(path);
        AssertTrue((await fixture.Management.ManageAsync(removal)).IsSuccess);
    }

    private static async ValueTask ManagedJavaLeaseReclaimsOnlyProvenExitedProcessIdentities()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var removal = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        string uses = Path.Combine(fixture.RuntimeRoot, ".nexa-java-uses", fixture.Plan.ComponentName);
        // The child can finish between StartAsync returning and the durable bind.
        var processPort = new LongLivedProcessPort();
        using (var quickExit = await processPort.StartAsync(new()))
        {
            try
            {
                quickExit.Kill(entireProcessTree: true);
                await quickExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var completed = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
                AssertTrue(completed is not null); completed!.BindProcess(quickExit.Id);
            }
            finally
            {
                if (!quickExit.HasExited) quickExit.Kill(entireProcessTree: true);
                await quickExit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        AssertEqual(0, Directory.GetFiles(uses).Length);
        var use = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
        using (use) use!.BindProcess(Environment.ProcessId);
        string path = Directory.GetFiles(uses).Single();
        byte[] valid = await File.ReadAllBytesAsync(path);
        long actualStart = BinaryPrimitives.ReadInt64LittleEndian(valid.AsSpan(12));
        AssertFalse((await fixture.Management.ManageAsync(removal)).IsSuccess);
        // A recorded later birthday cannot describe a former lifetime of this PID.
        byte[] contradictory = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(contradictory.AsSpan(12), actualStart + TimeSpan.TicksPerSecond);
        await File.WriteAllBytesAsync(path, contradictory);
        AssertFalse((await fixture.Management.ManageAsync(removal)).IsSuccess);
        byte[] retained = await File.ReadAllBytesAsync(path);
        AssertTrue(contradictory.SequenceEqual(retained));
        // Linux's independently calibrated CLR clocks cannot prove reuse while a PID runs.
        // Other platforms can use the native birthday to prove the old PID lifetime ended.
        byte[] reused = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(reused.AsSpan(12), actualStart - TimeSpan.TicksPerSecond);
        await File.WriteAllBytesAsync(path, reused);
        var reusedResult = await fixture.Management.ManageAsync(removal);
        if (OperatingSystem.IsLinux())
        {
            AssertFalse(reusedResult.IsSuccess); AssertTrue(File.Exists(path)); AssertTrue(File.Exists(fixture.Executable));
            byte[] retainedReused = await File.ReadAllBytesAsync(path);
            AssertTrue(reused.SequenceEqual(retainedReused));
        }
        else
        {
            AssertTrue(reusedResult.IsSuccess); AssertFalse(File.Exists(path)); AssertFalse(File.Exists(fixture.Executable));
        }
    }
}
