using System.Runtime.Versioning;

namespace Nexa.Platform.Updates;

/// <summary>No permission repair, path-based replacement or adoption of a user-owned tree.</summary>
public static class ProtectedUpdateDirectory
{
    public static IUpdateDirectory Open(string path) => OperatingSystem.IsWindows()
        ? new WindowsLease(WindowsUpdateDirectory.Open(path), System.IO.Path.GetFullPath(path))
        : UnixUpdateDirectory.Open(path);

    [SupportedOSPlatform("windows")]
    private sealed class WindowsLease(WindowsUpdateDirectory directory, string path) : IUpdateDirectory
    {
        public string Path { get; } = path;
        public IUpdateDirectory OpenDirectory(string name) => new WindowsLease(directory.OpenDirectory(name), System.IO.Path.Combine(Path, name));
        public IUpdateDirectory CreateDirectory(string name, bool publicRead) => new WindowsLease(directory.CreateDirectory(name, publicRead), System.IO.Path.Combine(Path, name));
        public FileStream OpenRead(string name) => directory.OpenReadFile(name, concurrentState: true);
        public FileStream OpenState(string name, bool publicRead = false, bool exclusive = true) => directory.OpenStateFile(name, publicRead, exclusive);
        public FileStream CreateFile(string name, bool publicRead = false, bool executable = false) => directory.CreatePayloadFile(name, publicRead);
        // Individual Windows writes use write-through handles and Flush(true).
        public void Flush() { }
        public void Dispose() => directory.Dispose();
    }
}
