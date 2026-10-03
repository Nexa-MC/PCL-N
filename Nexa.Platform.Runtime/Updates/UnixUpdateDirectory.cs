using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexa.Platform.Updates;

/// <summary>Root-owned no-follow descriptor chain. Does not accept writable ancestors or repair ACLs.</summary>
internal sealed partial class UnixUpdateDirectory : IUpdateDirectory
{
    private readonly List<SafeFileHandle> _chain;
    private readonly bool _created;
    private bool _disposed;
    public string Path { get; }
    private UnixUpdateDirectory(List<SafeFileHandle> chain, string path, bool created)
    { _chain = chain; Path = path; _created = created; }

    private static bool Mac => OperatingSystem.IsMacOS();
    private static int NoFollow => Mac ? 0x100 : 0x20000;
    private static int CloseOnExec => Mac ? 0x1000000 : 0x80000;
    private static int DirectoryFlag => Mac ? 0x100000 : 0x10000;
    private static int CreateFlag => Mac ? 0x200 : 0x40;
    private static int ExclusiveFlag => Mac ? 0x800 : 0x80;

    internal static UnixUpdateDirectory Open(string path)
    {
        if (!OperatingSystem.IsLinux() && !Mac) throw new PlatformNotSupportedException();
        if (!System.IO.Path.IsPathFullyQualified(path) || !path.StartsWith('/') || path.Contains('\0'))
            throw new ArgumentException("更新目录必须是本地绝对路径。", nameof(path));
        string full = System.IO.Path.GetFullPath(path);
        string[] names = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var chain = new List<SafeFileHandle>();
        try
        {
            chain.Add(OpenAt(Mac ? -2 : -100, "/", DirectoryFlag, 0));
            Admit(chain[^1], true);
            foreach (string name in names)
            {
                Validate(name);
                chain.Add(OpenAt(Fd(chain[^1]), name, DirectoryFlag, 0));
                Admit(chain[^1], true);
            }
            return new(chain, full, false);
        }
        catch { foreach (var fd in chain) fd.Dispose(); throw; }
    }

    public IUpdateDirectory OpenDirectory(string name) => Child(name, create: false, publicRead: false);
    public IUpdateDirectory CreateDirectory(string name, bool publicRead) => Child(name, create: true, publicRead);
    private UnixUpdateDirectory Child(string name, bool create, bool publicRead)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(name);
        var chain = new List<SafeFileHandle>();
        try
        {
            foreach (var fd in _chain)
            {
                // F_DUPFD_CLOEXEC atomically excludes inheritance by child processes.
                int copy = Fcntl(Fd(fd), Mac ? 67 : 1030, 0);
                if (copy < 0) Fail("保留更新目录");
                chain.Add(new SafeFileHandle(copy, true));
            }
            if (create)
            {
                if (GetEffectiveUid() != 0) throw new UnauthorizedAccessException("自动更新需要系统管理员授权。");
                if (MkdirAt(Fd(_chain[^1]), name, publicRead ? 0x1edu : 0x1c0u) != 0) Fail("创建更新目录");
                Flush();
            }
            chain.Add(OpenAt(Fd(_chain[^1]), name, DirectoryFlag, 0));
            if (create && Fchmod(Fd(chain[^1]), publicRead ? 0x1edu : 0x1c0u) != 0) Fail("设置新更新目录权限");
            Admit(chain[^1], true);
            return new UnixUpdateDirectory(chain, System.IO.Path.Combine(Path, name), create);
        }
        catch { foreach (var fd in chain) fd.Dispose(); throw; }
    }

    public FileStream OpenRead(string name) => File(name, create: false, state: false, publicRead: false, executable: false, exclusive: false);
    public FileStream OpenState(string name, bool publicRead = false, bool exclusive = true)
        => File(name, create: false, state: true, publicRead, executable: false, exclusive);
    public FileStream CreateFile(string name, bool publicRead = false, bool executable = false)
        => File(name, create: true, state: false, publicRead, executable, exclusive: false);

    private FileStream File(string name, bool create, bool state, bool publicRead, bool executable, bool exclusive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(name);
        if (create && !_created) throw new InvalidOperationException("仅允许在新目录内创建更新文件。");
        if ((create || state) && GetEffectiveUid() != 0) throw new UnauthorizedAccessException("自动更新需要系统管理员授权。");
        int flags = (create || state ? 2 : 0) | (create ? CreateFlag | ExclusiveFlag : state ? CreateFlag : 0);
        uint mode = publicRead ? executable ? 0x1edu : 0x1a4u : 0x180u;
        bool newlyCreated = create;
        SafeFileHandle handle;
        if (state)
        {
            int fd = OpenAtNative(Fd(_chain[^1]), name, flags | ExclusiveFlag | NoFollow | CloseOnExec, mode);
            if (fd >= 0) { handle = new SafeFileHandle(fd, true); newlyCreated = true; }
            else if (Marshal.GetLastPInvokeError() == 17) handle = OpenAt(Fd(_chain[^1]), name, 2, 0);
            else { Fail("创建更新状态"); throw new InvalidOperationException(); }
        }
        else handle = OpenAt(Fd(_chain[^1]), name, flags, mode);
        try
        {
            if (newlyCreated && Fchmod(Fd(handle), mode) != 0) Fail("设置新更新文件权限");
            Admit(handle, false);
            if (state && exclusive && Flock(Fd(handle), 2 | 4) != 0) Fail("独占更新事务");
            var stream = new FileStream(handle, create || state ? FileAccess.ReadWrite : FileAccess.Read, 65536, isAsync: false);
            if (create || state) Flush();
            return stream;
        }
        catch { handle.Dispose(); throw; }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Fsync(Fd(_chain[^1])) != 0) Fail("持久化更新目录");
    }

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.Any(c => c < 32 || "/\\:".Contains(c)))
            throw new ArgumentException("更新名称必须是单个目录项。", nameof(name));
    }

    private static SafeFileHandle OpenAt(int parent, string name, int flags, uint mode)
    {
        int fd = OpenAtNative(parent, name, flags | NoFollow | CloseOnExec | (Mac ? 4 : 0x800), mode);
        if (fd < 0) Fail("打开受保护更新对象");
        return new SafeFileHandle(fd, true);
    }

    private static unsafe void Admit(SafeFileHandle fd, bool directory)
    {
        byte[] stat = new byte[256];
        uint owner, links, mode;
        fixed (byte* bytes = stat)
        {
            if (Mac)
            {
                int result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? FstatMac64(Fd(fd), bytes) : FstatMac(Fd(fd), bytes);
                if (result != 0) Fail("读取更新对象属性");
                mode = BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(4));
                links = BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(6));
                owner = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(16));
            }
            else
            {
                if (Statx(Fd(fd), "", 0x1100, 0x7ff, bytes) != 0) Fail("读取更新对象属性");
                uint mask = BinaryPrimitives.ReadUInt32LittleEndian(stat);
                if ((mask & 0x1b) != 0x1b) throw new UnauthorizedAccessException("文件系统未提供必要的对象属性。");
                links = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(16));
                owner = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(20));
                mode = BinaryPrimitives.ReadUInt16LittleEndian(stat.AsSpan(28));
            }
        }
        if (owner != 0 || (mode & 0x12) != 0 || (mode & 0xf000) != (directory ? 0x4000 : 0x8000) || (!directory && links != 1))
            throw new UnauthorizedAccessException("更新对象不是受保护的系统目录或文件，请使用系统安装包。");
        CheckAcl(Fd(fd), directory);
    }

    private static void CheckAcl(int fd, bool directory)
    {
        if (!Mac)
        {
            foreach (string name in directory ? new[] { "system.posix_acl_access", "system.posix_acl_default" } : ["system.posix_acl_access"])
            {
                nint length = GetXattr(fd, name, 0, 0);
                if (length >= 0) throw new UnauthorizedAccessException("更新对象含有未准入的扩展权限。");
                if (Marshal.GetLastPInvokeError() != 61) Fail("验证更新扩展权限");
            }
            return;
        }
        nint acl = AclGetFd(fd, 0x100);
        if (acl == 0) Fail("读取更新 ACL");
        try
        {
            int kind = 0, count = 0, result;
            while ((result = AclGetEntry(acl, kind, out nint entry)) == 0)
            {
                kind = -1;
                if (++count > 128 || AclGetTag(entry, out int tag) != 0 || tag != 2)
                    throw new UnauthorizedAccessException("更新对象含有未准入的 ACL 授权。");
            }
            // Darwin returns -1 at the end; errno EINVAL is also the documented end signal.
            if (result != -1 || Marshal.GetLastPInvokeError() != 22) throw new UnauthorizedAccessException("更新 ACL 结构异常。");
        }
        finally { _ = AclFree(acl); }
    }

    private static void Fail(string action) => throw new IOException(action + "失败。", new Win32Exception(Marshal.GetLastPInvokeError()));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _chain.Count - 1; i >= 0; i--) _chain[i].Dispose();
        _chain.Clear();
    }

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int OpenAtNative(int fd, string path, int flags, uint mode);
    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int MkdirAt(int fd, string name, uint mode);
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static partial int Fcntl(int fd, int command, int argument);
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)] private static partial int Flock(int fd, int operation);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)] private static partial int Fsync(int fd);
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)] private static partial int Fchmod(int fd, uint mode);
    [LibraryImport("libc", EntryPoint = "geteuid")] internal static partial uint GetEffectiveUid();
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static unsafe partial int Statx(int fd, string path, int flags, uint mask, byte* stat);
    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static unsafe partial int FstatMac64(int fd, byte* stat);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)] private static unsafe partial int FstatMac(int fd, byte* stat);
    [LibraryImport("libc", EntryPoint = "fgetxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial nint GetXattr(int fd, string name, nint value, nuint size);
    [LibraryImport("libc", EntryPoint = "acl_get_fd_np", SetLastError = true)] private static partial nint AclGetFd(int fd, int type);
    [LibraryImport("libc", EntryPoint = "acl_get_entry", SetLastError = true)] private static partial int AclGetEntry(nint acl, int kind, out nint entry);
    [LibraryImport("libc", EntryPoint = "acl_get_tag_type", SetLastError = true)] private static partial int AclGetTag(nint entry, out int tag);
    [LibraryImport("libc", EntryPoint = "acl_free")] private static partial int AclFree(nint acl);
}
