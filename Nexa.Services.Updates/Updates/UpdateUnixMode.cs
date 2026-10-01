namespace Nexa.Services.Updates;

/// <summary>Archive metadata cannot grant special or shared write permissions.</summary>
internal static class UpdateUnixMode
{
    // Preserve only existing permission bits within octal 0755; never add permissions.
    internal static int? Normalize(int? mode) => mode is null or < 0 ? null : mode.Value & 0x1ED;

    internal static void Apply(string path, int? mode)
    {
        if (Normalize(mode) is int permissions && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, (UnixFileMode)permissions);
    }
}
