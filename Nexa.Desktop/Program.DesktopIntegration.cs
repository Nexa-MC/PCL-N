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
            using JsonDocument? document = ReadBootstrapSettings(path);
            return !(document is not null && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int number) && number == 1
                && document.RootElement.TryGetProperty("booleanOptions", out var options) && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("SystemSingleInstance", out var value) && value.ValueKind == JsonValueKind.False);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }

    internal static bool HardwareAccelerationDisabled(string root)
    {
        string path = Path.Combine(root, Nexa.Services.Files.FolderNames.Settings, "settings.json");
        try
        {
            using JsonDocument? document = ReadBootstrapSettings(path);
            return document is not null && document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out int number) && number == 1
                && document.RootElement.TryGetProperty("booleanOptions", out var options) && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("SystemDisableHardwareAcceleration", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static JsonDocument? ReadBootstrapSettings(string path)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes) return null;
        using MemoryStream snapshot = new((int)Math.Min(stream.Length, 64 * 1024));
        byte[] buffer = new byte[8192];
        int remaining = maximumBytes;
        while (true)
        {
            int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1));
            if (read == 0) break;
            if (read > remaining) return null;
            snapshot.Write(buffer, 0, read);
            remaining -= read;
        }
        return JsonDocument.Parse(snapshot.GetBuffer().AsMemory(0, (int)snapshot.Length));
    }
}
