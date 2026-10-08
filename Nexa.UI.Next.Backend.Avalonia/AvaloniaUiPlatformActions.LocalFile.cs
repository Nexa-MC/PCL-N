using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions
{
    internal Action<ProcessStartInfo>? LocalFileInvocation { get; init; }

    /// <summary>Opens a user-selected regular local file through its operating-system association.</summary>
    public void OpenLocalFile(string path)
    {
        if (_owner is null) throw new InvalidOperationException("The native window is not ready.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 32768 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Only absolute local file paths without control characters are supported.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The local file does not exist.", fullPath);
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 || !IsRegularLocalFile(fullPath))
            throw new ArgumentException("Only regular local files are supported.", nameof(path));
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows()) start = new(fullPath) { UseShellExecute = true };
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            start = new(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(fullPath);
        }
        else throw new PlatformNotSupportedException("Local file associations are not supported on this platform.");
        if (LocalFileInvocation is { } invoke) invoke(start);
        else Process.Start(start)?.Dispose();
    }

    private static bool IsRegularLocalFile(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        if (OperatingSystem.IsLinux())
        {
            const int currentDirectory = -100;
            const int doNotFollowLinks = 0x100;
            const uint fileTypeMask = 0x1;
            if (ReadLinuxFileType(currentDirectory, path, doNotFollowLinks, fileTypeMask, out var info) != 0)
                throw new IOException("Unable to observe the local file type.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            return (info.Mask & fileTypeMask) != 0 && (info.Mode & 0xf000) == 0x8000;
        }
        if (OperatingSystem.IsMacOS())
        {
            MacFileType info;
            int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? ReadMacFileType64(path, out info) : ReadMacFileType(path, out info);
            if (result != 0)
                throw new IOException("Unable to observe the local file type.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            return (info.Mode & 0xf000) == 0x8000;
        }
        return false;
    }

    // Linux statx has a fixed 256-byte ABI; only its mask and file mode are consumed.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileType
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
    }

    // Darwin's inode64 stat has a fixed 144-byte ABI on both supported CPU architectures.
    // Intel keeps the legacy plain lstat symbol; Arm64 exposes inode64 as plain lstat.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacFileType
    {
        [FieldOffset(4)] internal ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ReadLinuxFileType(int directory, string path, int flags, uint mask, out LinuxFileType info);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ReadMacFileType64(string path, out MacFileType info);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ReadMacFileType(string path, out MacFileType info);
}
