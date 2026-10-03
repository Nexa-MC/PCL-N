using System.IO.Compression;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceModpackExportRoundTripsSelectionAndRetainsExistingTargets()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root); Directory.CreateDirectory(Path.Combine(instance, "mods")); Directory.CreateDirectory(Path.Combine(instance, "config")); Directory.CreateDirectory(Path.Combine(instance, "saves", "World"));
            await File.WriteAllBytesAsync(Path.Combine(instance, "mods", "a.jar.disabled"), ResourceJar("a"));
            await File.WriteAllTextAsync(Path.Combine(instance, "config", "selected.cfg"), "config");
            await File.WriteAllTextAsync(Path.Combine(instance, "config", "accounts.json"), "private");
            await File.WriteAllTextAsync(Path.Combine(instance, "config", "test.log"), "log");
            await File.WriteAllTextAsync(Path.Combine(instance, "saves", "World", "level.dat"), "world");
            var preview = await InstanceModpackExportService.PreviewAsync(new(instance));
            AssertEqual(1, preview.Categories.Single(c => c.Id == "config").FileCount);
            var builder = new XsrStateStoreBuilder(); TaskCenterStateContract.DeclareState(builder); DownloadService.DeclareState(builder); MinecraftProcessStateComposition.DeclareState(builder);
            var store = builder.Build(); var tasks = new TaskCenterService(store); var exporter = new InstanceModpackExportService(tasks, store);
            string destination = Path.Combine(root, "export.mrpack"); var command = new InstanceModpackExportCommand(instance, destination, "示例整合包", "1.2.3", ["mods", "config"]);
            var result = await exporter.ExportAsync(command); AssertTrue(result.IsSuccess, result.Error?.Message ?? "");
            var pack = await MinecraftModpackArchive.InspectAsync(destination); AssertEqual("示例整合包", pack.Name); AssertEqual("1.2.3", pack.Version); AssertEqual("1.21.1", pack.Game);
            using (var archive = ZipFile.OpenRead(destination))
            {
                AssertTrue(archive.GetEntry("overrides/mods/a.jar.disabled") is not null);
                AssertTrue(archive.GetEntry("overrides/config/selected.cfg") is not null);
                AssertFalse(archive.Entries.Any(e => e.FullName.Contains("accounts", StringComparison.Ordinal) || e.FullName.Contains("saves", StringComparison.Ordinal) || e.FullName.EndsWith(".log", StringComparison.Ordinal)));
            }
            byte[] original = await File.ReadAllBytesAsync(destination);
            AssertFalse((await exporter.ExportAsync(command)).IsSuccess); AssertTrue((await File.ReadAllBytesAsync(destination)).SequenceEqual(original));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            AssertFalse((await exporter.ExportAsync(command with { DestinationPath = Path.Combine(root, "canceled.mrpack") }, canceled.Token)).IsSuccess);
            AssertFalse(File.Exists(Path.Combine(root, "canceled.mrpack")));
            AssertFalse((await exporter.ExportAsync(command with { DestinationPath = Path.Combine(root, "bad.mrpack"), Categories = ["../logs"] })).IsSuccess);
            AssertFalse(Directory.EnumerateFiles(root, ".nexa-export-*").Any());
            AssertEqual(0, store.Read<TaskCenterSummary>(store.Resolve(TaskCenterStateContract.SummaryKey)).Value!.ActiveCount);
        }
        finally { Directory.Delete(root, true); }
    }
}
