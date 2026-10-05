using Nexa.Services.Logging;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.Desktop;

internal static partial class Program
{
    private static async Task<bool> ExportLogsAsync(FileLogSink sink, AvaloniaUiPlatformActions actions, CancellationToken token)
    {
        string? directory = await actions.PickExportDirectoryAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (directory is null) return false;
        string destination = Path.Combine(directory, $"Nexa-Log-Facts-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        await sink.ExportAsync(destination, token).ConfigureAwait(false);
        return true;
    }
}
