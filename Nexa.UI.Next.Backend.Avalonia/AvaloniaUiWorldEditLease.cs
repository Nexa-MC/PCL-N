using System.Runtime.InteropServices;
using System.Text;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>
/// macOS bridge for Java's POSIX advisory world session lock. Services inject this port;
/// the descriptor and exclusive write lock stay alive for the complete world transaction.
/// </summary>
public static partial class AvaloniaUiWorldEditLease
{
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const int ReadOnly = 0, ReadWrite = 2, NoFollow = 0x100, DirectoryOnly = 0x100000, CloseOnExec = 0x1000000;
    private const int SetLock = 8;
    private const short WriteLock = 3, Unlock = 2, SeekSet = 0;
    private const int TryLockRegion = 2, UnlockRegion = 0;
    private static readonly object Gate = new();
    // POSIX locks are process-wide; another descriptor in this launcher must not accidentally
    // release the existing owner's lock. Case folding conservatively rejects filesystem aliases.
    private static readonly HashSet<string> ActivePaths = new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable Acquire(string sessionLockPath)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("此世界锁端口仅适用于 macOS。");
        if (!Path.IsPathFullyQualified(sessionLockPath)) throw new ArgumentException("世界会话锁必须使用绝对路径。", nameof(sessionLockPath));
        string path = Path.GetFullPath(sessionLockPath);
        if (Path.GetFileName(path) != "session.lock" || Encoding.UTF8.GetByteCount(path) > 4096)
            throw new IOException("世界会话锁路径无效。");
        lock (Gate) if (!ActivePaths.Add(path)) throw new IOException("启动器已有世界操作持有此会话锁。");
        int descriptor = -1;
        try
        {
            descriptor = OpenWithoutLinks(path);
            var region = new DarwinFileLock { Start = 0, Length = 1, Type = WriteLock, Whence = SeekSet };
            // F_SETLK is nonblocking. Java's whole-file lock overlaps [0,1), including old
            // and new Minecraft formats; no marker bytes are changed in the existing file.
            if (SetRegionLock(descriptor, ref region) != 0) throw NativeFailure();
            var lease = new Lease(path, descriptor); descriptor = -1; return lease;
        }
        catch
        {
            if (descriptor >= 0) NativeClose(descriptor);
            lock (Gate) ActivePaths.Remove(path);
            throw;
        }
    }

    private static int OpenWithoutLinks(string path)
    {
        int directory = NativeOpen("/", ReadOnly | DirectoryOnly | CloseOnExec);
        if (directory < 0) throw NativeFailure();
        try
        {
            string parent = Path.GetDirectoryName(path)!;
            // openat anchors each subsequent component to an already-open directory. O_NOFOLLOW
            // refuses linked ancestors as well as a symlink at session.lock itself.
            foreach (string component in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                int child = NativeOpenAt(directory, component, ReadOnly | DirectoryOnly | NoFollow | CloseOnExec);
                if (child < 0) throw NativeFailure();
                NativeClose(directory); directory = child;
            }
            int file = NativeOpenAt(directory, "session.lock", ReadWrite | NoFollow | CloseOnExec);
            if (file < 0) throw NativeFailure();
            return file;
        }
        finally { NativeClose(directory); }
    }

    private static IOException NativeFailure() => new($"无法独占世界会话锁（系统错误 {Marshal.GetLastPInvokeError()}）。");

    private static int SetRegionLock(int descriptor, ref DarwinFileLock region)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return NativeSetLock(descriptor, SetLock, ref region);
        // Apple's ARM64 variadic ABI passes fcntl's third argument on the stack. A fixed
        // P/Invoke signature cannot model that vararg. lockf has a fixed signature and uses
        // the same POSIX fcntl record lock; the fresh descriptor's offset stays at zero.
        if (NativeSeek(descriptor, 0, SeekSet) != 0) return -1;
        return NativeLockRegion(descriptor, region.Type == Unlock ? UnlockRegion : TryLockRegion, 1);
    }

    private sealed class Lease(string path, int descriptor) : IDisposable
    {
        private int _descriptor = descriptor;
        public void Dispose()
        {
            int owned = Interlocked.Exchange(ref _descriptor, -1); if (owned < 0) return;
            try
            {
                var region = new DarwinFileLock { Start = 0, Length = 1, Type = Unlock, Whence = SeekSet };
                SetRegionLock(owned, ref region);
            }
            finally
            {
                // Closing also releases the POSIX lock if explicit unlock failed. Never retry
                // close on EINTR: a descriptor may already have been reused by another thread.
                NativeClose(owned); lock (Gate) ActivePaths.Remove(path);
            }
        }
    }

    // Darwin struct flock: off_t start/length, pid_t pid, short type/whence.
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinFileLock
    {
        internal long Start, Length;
        internal int Pid;
        internal short Type, Whence;
    }

    [LibraryImport(SystemLibrary, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int NativeOpen(string path, int flags);
    [LibraryImport(SystemLibrary, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int NativeOpenAt(int directory, string path, int flags);
    [LibraryImport(SystemLibrary, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int NativeSetLock(int descriptor, int command, ref DarwinFileLock region);
    [LibraryImport(SystemLibrary, EntryPoint = "close", SetLastError = true)]
    private static partial int NativeClose(int descriptor);
    [LibraryImport(SystemLibrary, EntryPoint = "lockf", SetLastError = true)]
    private static partial int NativeLockRegion(int descriptor, int operation, long length);
    [LibraryImport(SystemLibrary, EntryPoint = "lseek", SetLastError = true)]
    private static partial long NativeSeek(int descriptor, long offset, int origin);
}
