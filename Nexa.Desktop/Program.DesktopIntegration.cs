using System.Text.Json;

namespace Nexa.Desktop;

internal static partial class Program
{
    private static Action? _restartAfterExit;
    private static DesktopDestination _setupDestination = DesktopDestination.Activate;

    internal static string ProductDisplayTitle(string version)
    {
        return "NexaCL Firefly v" + version;
    }

    internal static bool SingleInstanceEnabled(string root)
    {
        string path = Path.Combine(root, Nexa.Services.Files.FolderNames.Settings, "settings.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return true;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            return !(document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int number) && number == 1
                && document.RootElement.TryGetProperty("booleanOptions", out var options) && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("SystemSingleInstance", out var value) && value.ValueKind == JsonValueKind.False);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }
}
