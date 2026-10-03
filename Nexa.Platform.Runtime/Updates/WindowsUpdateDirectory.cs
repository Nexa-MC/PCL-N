using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Nexa.Platform.Updates;

/// <summary>Owns a checked Windows directory chain for a separately installed update helper.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsUpdateDirectory : IDisposable
{
    private const uint ReadControl = 0x20000, Synchronize = 0x100000, ReadAttributes = 0x80;
    private const uint DirectoryAccess = ReadControl | Synchronize | ReadAttributes | 0x21;
    private const uint DeleteAccess = 0x10000;
    private const uint MutationRights = 0x500D0156;
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private readonly object _gate = new();
    private readonly List<SafeFileHandle> _chain;
    private readonly bool _created;
    private readonly bool _published;
    private bool _disposed;

    private WindowsUpdateDirectory(List<SafeFileHandle> chain, bool created, bool published = false)
    {
        _chain = chain;
        _created = created;
        _published = published;
    }

    /// <summary>Admits existing local directories only. No permissions are repaired or changed.</summary>
    public static WindowsUpdateDirectory Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
            || path.Contains('/') || path.Contains('\0'))
            throw new ArgumentException("更新目录必须是本地绝对路径。", nameof(path));
        string full = Path.GetFullPath(path);
        string root = full[..3];
        if (GetDriveType(root) != 3) throw new NotSupportedException("更新目录必须位于本地固定磁盘。");
        string[] parts = full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts) ValidateLeaf(part);
        var chain = new List<SafeFileHandle>();
        try
        {
            SafeFileHandle volumeRoot = CreateFile(root, DirectoryAccess, 1, 0, 3, 0x02200000, 0);
            if (volumeRoot.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                volumeRoot.Dispose();
                ThrowNative("打开更新卷", error);
            }
            chain.Add(volumeRoot);
            Admit(volumeRoot, directory: true, ancestor: parts.Length > 0);
            for (int index = 0; index < parts.Length; index++)
            {
                SafeFileHandle child = OpenRelative(chain[^1], parts[index], directory: true, create: false);
                chain.Add(child);
                Admit(child, directory: true, ancestor: index < parts.Length - 1);
            }
            return new(chain, created: false);
        }
        catch
        {
            foreach (SafeFileHandle handle in chain) handle.Dispose();
            throw;
        }
    }

    /// <summary>Creates a fresh protected directory. Its lease independently retains all ancestors.</summary>
    public WindowsUpdateDirectory CreateStagingDirectory() => CreateDirectory(".nexa-update-" + Guid.NewGuid().ToString("N"), publicRead: false);

    public WindowsUpdateDirectory CreateDirectory(string name, bool publicRead)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SafeFileHandle? child = null;
            var chain = new List<SafeFileHandle>();
            try
            {
                foreach (SafeFileHandle ancestor in _chain)
                {
                    if (!DuplicateHandle(GetCurrentProcess(), ancestor, GetCurrentProcess(), out SafeFileHandle copy, 0, false, 2))
                    {
                        int error = Marshal.GetLastPInvokeError();
                        copy.Dispose();
                        ThrowNative("保留更新目录", error);
                    }
                    chain.Add(copy);
                }
                child = OpenRelative(_chain[^1], name, directory: true, create: true, publicRead: publicRead);
                Admit(child, directory: true, ancestor: false);
                chain.Add(child);
                return new(chain, created: true, published: publicRead);
            }
            catch (Exception failure)
            {
                try { if (child is not null) MarkDelete(child); }
                catch (Exception cleanup) { throw new AggregateException("无法清理未准入的更新目录。", failure, cleanup); }
                finally
                {
                    child?.Dispose();
                    foreach (SafeFileHandle handle in chain) handle.Dispose();
                }
                throw;
            }
        }
    }

    /// <summary>Opens one protected leaf by parent handle, refusing reparse points and shared hardlinks.</summary>
    public FileStream OpenReadFile(string name) => OpenReadFile(name, concurrentState: false);

    public FileStream OpenReadFile(string name, bool concurrentState)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SafeFileHandle handle = OpenRelative(_chain[^1], name, directory: false, create: false, concurrentRead: concurrentState);
            try
            {
                Admit(handle, directory: false, ancestor: false);
                return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
        }
    }

    /// <summary>Opens or creates an admitted protected state leaf without truncation, with exclusive sharing.</summary>
    public FileStream OpenExclusiveStateFile(string name)
        => OpenStateFile(name, publicRead: false, exclusive: true);

    public FileStream OpenStateFile(string name, bool publicRead, bool exclusive)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SafeFileHandle handle = OpenRelative(_chain[^1], name, directory: false, create: false, state: true, publicRead: publicRead, exclusive: exclusive);
            try
            {
                Admit(handle, directory: false, ancestor: false);
                return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
        }
    }

    public WindowsUpdateDirectory OpenDirectory(string name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var chain = new List<SafeFileHandle>();
            try
            {
                foreach (SafeFileHandle ancestor in _chain)
                {
                    if (!DuplicateHandle(GetCurrentProcess(), ancestor, GetCurrentProcess(), out SafeFileHandle copy, 0, false, 2))
                    { copy.Dispose(); ThrowNative("保留更新目录", Marshal.GetLastPInvokeError()); }
                    chain.Add(copy);
                }
                SafeFileHandle child = OpenRelative(_chain[^1], name, directory: true, create: false);
                chain.Add(child);
                Admit(child, directory: true, ancestor: false);
                return new(chain, created: false);
            }
            catch { foreach (SafeFileHandle item in chain) item.Dispose(); throw; }
        }
    }

    public FileStream CreatePayloadFile(string name, bool publicRead)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_created) throw new InvalidOperationException("仅允许在新目录内创建更新文件。");
            SafeFileHandle handle = OpenRelative(_chain[^1], name, directory: false, create: true, publicRead: publicRead);
            try { Admit(handle, directory: false, ancestor: false); return new FileStream(handle, FileAccess.ReadWrite, 65536, isAsync: false); }
            catch { handle.Dispose(); throw; }
        }
    }

    /// <summary>Creates one new leaf with explicit protected security; never overwrites an existing name.</summary>
    public WindowsUpdateFile CreateFile(string name)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_created) throw new InvalidOperationException("仅允许在新建的更新暂存目录内创建文件。");
            SafeFileHandle handle = OpenRelative(_chain[^1], name, directory: false, create: true);
            try
            {
                Admit(handle, directory: false, ancestor: false);
                return new WindowsUpdateFile(handle);
            }
            catch (Exception failure)
            {
                try { MarkDelete(handle); }
                catch (Exception cleanup) { throw new AggregateException("无法清理未准入的更新文件。", failure, cleanup); }
                finally { handle.Dispose(); }
                throw;
            }
        }
    }

    /// <summary>Deletes only this lease's freshly created, empty directory, by its existing handle.</summary>
    public void DeleteEmptyStagingDirectory()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_created || _published) throw new InvalidOperationException("只能删除私有的空暂存目录。");
            MarkDelete(_chain[^1]);
            Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            for (int index = _chain.Count - 1; index >= 0; index--) _chain[index].Dispose();
            _chain.Clear();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal static void ValidateLeaf(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".."
            || name.EndsWith(' ') || name.EndsWith('.') || name.Any(static c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("更新文件名必须是无歧义的单个目录项。", nameof(name));
        string stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')))
            throw new ArgumentException("更新文件名不能是设备名称。", nameof(name));
    }

    internal static bool IsProtected(RawSecurityDescriptor descriptor, bool ancestor)
    {
        if (!Trusted(descriptor.Owner) || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0
            || descriptor.DiscretionaryAcl is not { } acl) return false;
        uint denied = ancestor ? MutationRights & ~6u : MutationRights;
        foreach (GenericAce ace in acl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (ace is not CommonAce common || common.IsCallback) return false;
            if (common.AceQualifier == AceQualifier.AccessDenied) continue;
            if (common.AceQualifier != AceQualifier.AccessAllowed) return false;
            if (((uint)common.AccessMask & denied) != 0 && !Trusted(common.SecurityIdentifier)) return false;
        }
        return true;
    }

    private static bool Trusted(SecurityIdentifier? sid) => sid is not null &&
        (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            || sid.Value == TrustedInstallerSid);

    private static void Admit(SafeFileHandle handle, bool directory, bool ancestor)
    {
        if (!GetFileInformationByHandle(handle, out FileInformation information)) ThrowNative("读取更新对象", Marshal.GetLastPInvokeError());
        if ((information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory
            || (!directory && information.NumberOfLinks != 1) || GetFileType(handle) != 1)
            throw new UnauthorizedAccessException("更新对象包含重解析点、硬链接或不支持的类型。");
        uint error = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out nint descriptor);
        if (error != 0) ThrowNative("读取更新对象权限", unchecked((int)error));
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 256 * 1024) throw new UnauthorizedAccessException("更新对象权限描述无效。");
            byte[] bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            if (!IsProtected(new RawSecurityDescriptor(bytes, 0), ancestor))
                throw new UnauthorizedAccessException("更新目录或文件允许普通用户修改，请使用受保护的系统安装包。");
        }
        finally { LocalFree(descriptor); }
    }

    private static unsafe SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory, bool create, bool state = false,
        bool publicRead = false, bool exclusive = true, bool concurrentRead = false)
    {
        ValidateLeaf(name);
        byte[]? security = null;
        if (create || state)
        {
            var descriptor = new RawSecurityDescriptor("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + (publicRead ? "(A;OICI;GRGX;;;BU)" : ""));
            security = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(security, 0);
        }
        fixed (char* namePointer = name)
        fixed (byte* securityPointer = security)
        {
            var text = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = namePointer };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &text,
                Attributes = 0x40,
                SecurityDescriptor = securityPointer
            };
            uint access = directory ? DirectoryAccess : ReadControl | Synchronize | ReadAttributes | 1;
            if (create || state)
            {
                access |= directory ? 0u : 2u;
                // Concurrent public journals need no deletion authority. Giving their writer
                // DELETE access would prevent readers that intentionally exclude delete sharing.
                // Public directories are immutable publications, not deletion-capable
                // staging leases. DELETE would conflict with a second protected reader.
                if ((create || exclusive) && !(directory && publicRead)) access |= DeleteAccess;
            }
            uint options = 0x200020u | (directory ? 1u : 0x40u) | (create || state ? 2u : 0u);
            int status = NtCreateFile(out SafeFileHandle handle, access, &attributes, out _, 0, 0, state ? exclusive ? 0u : 1u : concurrentRead ? 3u : 1u,
                state ? 3u : create ? 2u : 1u, options, 0, 0);
            if (status < 0)
            {
                handle.Dispose();
                ThrowNative("访问受保护更新对象", unchecked((int)RtlNtStatusToDosError(status)));
            }
            return handle;
        }
    }

    internal static unsafe void MarkDelete(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, &delete, sizeof(byte))) ThrowNative("清理更新暂存对象", Marshal.GetLastPInvokeError());
    }

    private static void ThrowNative(string operation, int error)
    {
        if (error == 5) throw new UnauthorizedAccessException(operation + "需要受保护的管理员权限。", new Win32Exception(error));
        throw new IOException(operation + "失败。", new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString { public ushort Length; public ushort MaximumLength; public char* Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public uint Length; public nint RootDirectory; public UnicodeString* ObjectName; public uint Attributes;
        public void* SecurityDescriptor; public nint SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status; public nuint Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string root);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(SafeFileHandle handle);
    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeFileHandle handle, uint type, uint requested, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint descriptor);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, SafeFileHandle source, nint targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(out SafeFileHandle handle, uint access, ObjectAttributes* attributes, out IoStatusBlock status, nint allocation, uint fileAttributes, uint share, uint disposition, uint options, nint ea, uint eaLength);
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle handle, uint kind, void* data, uint length);
}

/// <summary>A new protected staging file, retained through receipt verification and object-bound cleanup.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateFile : IDisposable
{
    private readonly SafeFileHandle _handle;
    internal WindowsUpdateFile(SafeFileHandle handle)
    {
        _handle = handle;
        Stream = new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: false);
    }
    public FileStream Stream { get; }
    public void Delete()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        WindowsUpdateDirectory.MarkDelete(_handle);
        Dispose();
    }
    public void Dispose() => Stream.Dispose();
}
