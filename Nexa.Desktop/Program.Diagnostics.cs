using System.Runtime.InteropServices;
using Nexa.Core;
using Nexa.Services.Foundation;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Tasks;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.Desktop;

internal static partial class Program
{
    private static async Task<bool> ExportDiagnosticsAsync(FoundationHost host, AvaloniaUiPlatformActions actions,
        string version, CancellationToken token)
    {
        string? directory = await actions.PickExportDirectoryAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (directory is null) return false;
        var store = host.StateStore;
        var selected = ((MinecraftLibrarySnapshot?)store.ReadAppliedValue(store.Resolve(MinecraftLibraryService.StateKey), token))?.SelectedInstance;
        // Correlate JVM/Mod facts with the selected instance, never with an unrelated last session.
        var session = store.ReadCollection<MinecraftProcessSnapshot>(store.Resolve(MinecraftProcessStateComposition.SessionsKey), token).Items
            .Where(item => selected is not null && PathIdentity.Comparer.Equals(item.InstanceDirectory, selected.DirectoryPath))
            .OrderBy(item => item.StartedAt).LastOrDefault();
        var context = store.ReadCollection<JvmRunContext>(store.Resolve(JvmHostStateContract.ContextsKey), token).Items
            .LastOrDefault(item => item.SessionId == session?.SessionId);
        var sample = store.ReadCollection<JvmRunSample>(store.Resolve(JvmHostStateContract.SamplesKey), token).Items
            .Where(item => item.SessionId == session?.SessionId).OrderBy(item => item.Sequence).LastOrDefault();
        var failure = store.ReadCollection<MinecraftProcessFailure>(store.Resolve(MinecraftProcessStateComposition.FailuresKey), token).Items
            .Where(item => selected is not null && PathIdentity.Comparer.Equals(item.InstanceDirectory, selected.DirectoryPath))
            .OrderBy(item => item.Report.Timestamp).LastOrDefault();
        LaunchPreflightPrompt? preflight = store.TryResolve(LaunchPreflightContract.StateKey, out var preflightId)
            ? store.ReadAppliedValue(preflightId, token) as LaunchPreflightPrompt : null;
        var snapshot = new DiagnosticBundleSnapshot(version,
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "unknown",
            RuntimeInformation.ProcessArchitecture.ToString())
        {
            JavaMajor = sample?.JavaMajor,
            MinecraftVersion = context?.GameVersion,
            Loader = context?.Loader,
            CrashClassification = failure?.Report.Code.ToString(),
            PreflightIssueCodes = preflight?.Report.Issues.Select(issue => issue.Code).ToArray() ?? [],
            Mods = context?.Inventory.Mods.Select(mod => new DiagnosticModIdentity(mod.Id, mod.Version, mod.Enabled)).ToArray() ?? [],
            InventoryComplete = context?.Inventory.Complete,
            FailedTasks = store.ReadCollection<TaskCenterEntry>(store.Resolve(TaskCenterStateContract.EntriesKey), token).Items.Count(task => task.State == TaskCenterEntryState.Failed),
            Logs = host.Logging.GetSnapshot()
        };
        string destination = Path.Combine(directory, $"Nexa-Diagnostic-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        await DiagnosticBundle.WriteAsync(destination, snapshot, token).ConfigureAwait(false);
        return true;
    }
}
