using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Core;
using Nexa.Platform;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Minecraft.Java;

/// <summary>Ownership is established by a completed install plan, never by a discovered path.</summary>
internal sealed class JavaRuntimeManagedStore(IReadOnlyList<string> roots)
{
    internal async Task<IReadOnlyList<JavaRuntimeManagedEntry>> ReadAsync(CancellationToken token)
    {
        List<JavaRuntimeManagedEntry> entries = [];
        foreach (string root in roots.Distinct(PathIdentity.Comparer))
        {
            foreach (var plan in await PlansAsync(root, token).ConfigureAwait(false))
            {
                string? executable = Executable(plan);
                if (executable is null || !File.Exists(executable)) continue;
                var entry = new JavaRuntimeManagedEntry(executable, plan.TargetDirectory, Identity(plan));
                if (!entries.Any(item => PathIdentity.Comparer.Equals(item.Executable, executable))) entries.Add(entry);
            }
        }
        return entries.AsReadOnly();
    }

    internal async Task<(string Root, JavaRuntimeDownloadPlan Plan)> ResolveAsync(string executable, string identity, CancellationToken token)
    {
        foreach (string root in roots)
        {
            var plan = (await PlansAsync(root, token).ConfigureAwait(false)).FirstOrDefault(item => PathIdentity.Comparer.Equals(Executable(item), executable));
            if (plan is not null && string.Equals(Identity(plan), identity, StringComparison.Ordinal))
                return (PathIdentity.Normalize(root), plan);
        }
        throw new InvalidDataException("托管 Java 已变化，请刷新后重试。");
    }

    internal static async Task<IReadOnlyList<JavaRuntimeDownloadPlan>> PlansAsync(string root, CancellationToken token, bool includePending = false)
    {
        root = PathIdentity.Normalize(root);
        string jobs = Path.Combine(root, JavaInstallJournal.DirectoryName);
        RecoveryBlobStore.CheckLinks(jobs);
        if (!Directory.Exists(jobs)) return [];
        List<JavaRuntimeDownloadPlan> plans = [];
        var stages = Directory.EnumerateDirectories(jobs).Take(4097).ToArray();
        if (stages.Length > 4096) throw new InvalidDataException("Java 安装记录过多。");
        foreach (string stage in stages.OrderByDescending(CompletionStamp))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            var journal = await JavaInstallJournal.OpenAsync(root, stage, token).ConfigureAwait(false);
            if ((!includePending && !journal.Completed) || journal.Canceled) continue;
            if (await journal.ReadPlanAsync(token).ConfigureAwait(false) is { } plan) plans.Add(plan);
        }
        return plans;
    }

    internal static async Task CheckNoPendingInstallAsync(string root, string component, CancellationToken token)
    {
        string jobs = Path.Combine(root, JavaInstallJournal.DirectoryName);
        RecoveryBlobStore.CheckLinks(jobs);
        if (!Directory.Exists(jobs)) return;
        foreach (string stage in Directory.EnumerateDirectories(jobs).Take(4096))
        {
            if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) continue;
            var journal = await JavaInstallJournal.OpenAsync(root, stage, token).ConfigureAwait(false);
            if (journal.Intent.Component == component && !journal.Completed && !journal.Canceled)
                throw new IOException("此 Java 有待处理的安装任务，请先完成或取消安装。");
        }
    }

    private static DateTime CompletionStamp(string stage)
    {
        string complete = Path.Combine(stage, "complete");
        RecoveryBlobStore.CheckLinks(complete);
        return File.GetLastWriteTimeUtc(complete);
    }

    private static string Identity(JavaRuntimeDownloadPlan plan) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(plan, JavaInstallJsonContext.Default.JavaRuntimeDownloadPlan)));

    private static string? Executable(JavaRuntimeDownloadPlan plan) => plan.Files
        .Select(file => file.TargetPath).FirstOrDefault(path => Path.GetFileName(path) == (OperatingSystem.IsWindows() ? "java.exe" : "java")
            && Path.GetFileName(Path.GetDirectoryName(path)) == "bin");

    internal static FileStream AcquireRoot(string root)
    {
        string path = Path.Combine(root, ".nexa-java.lock");
        RecoveryBlobStore.CheckLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static void CheckUnused(string root, string component)
    {
        string uses = Path.Combine(root, ".nexa-java-uses", component);
        RecoveryBlobStore.CheckLinks(uses);
        if (!Directory.Exists(uses)) return;
        var paths = Directory.EnumerateFiles(uses).Take(4097).ToArray();
        if (paths.Length > 4096) throw new IOException("Java 使用记录过多。");
        foreach (string path in paths)
        {
            if (Path.GetExtension(path) != ".lease" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                throw new IOException("Java 使用记录包含未知文件。");
            RecoveryBlobStore.CheckLinks(path);
            using (var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var identity = JavaRuntimeLeaseIdentity.Read(lease);
                if (!identity.HasDefinitelyExited())
                    throw new IOException("此 Java 的进程仍在使用，或无法确认其已经退出；已保留使用记录。");
            }
            RecoveryBlobStore.CheckLinks(path);
            File.Delete(path);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Cryptographic Do Not Use", "CA5350:DoNotUseWeakCryptographicAlgorithms",
        Justification = "Mojang manifests publish legacy SHA-1 artifact checksums. This verifies installed-file content against the install plan, not publisher authenticity.")]
    internal static async Task VerifyTreeAsync(JavaRuntimeDownloadPlan plan, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(plan.TargetDirectory);
        if (!Directory.Exists(plan.TargetDirectory)) throw new IOException("托管 Java 目录已不存在。");
        var expected = plan.Files.ToDictionary(file => file.TargetPath, PathIdentity.Comparer);
        Stack<string> pending = new(); pending.Push(plan.TargetDirectory);
        int visited = 0;
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 40000) throw new IOException("Java 目录项目过多。");
                RecoveryBlobStore.CheckLinks(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                if (!expected.ContainsKey(path)) throw new IOException("Java 目录包含后续文件，已保留用户内容。");
            }
        }
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(file.TargetPath);
            await using var input = new FileStream(file.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length != file.Size || !string.Equals(Convert.ToHexString(await SHA1.HashDataAsync(input, token).ConfigureAwait(false)), file.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Java 文件已改变，已保留后续修改。");
        }
    }

    internal static void DeleteTree(string path)
    {
        RecoveryBlobStore.CheckLinks(path);
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            RecoveryBlobStore.CheckLinks(entry);
            if (Directory.Exists(entry)) DeleteTree(entry); else File.Delete(entry);
        }
        Directory.Delete(path);
    }
}

internal readonly record struct JavaRuntimeLeaseIdentity(int ProcessId, long StartTimeUtcTicks)
{
    internal static IPlatformProcessIdentity Processes { get; } = PlatformProcessIdentityFactory.Create();
    private const int RecordLength = 20;
    private static ReadOnlySpan<byte> Magic => "NXJAVA01"u8;

    internal static JavaRuntimeLeaseIdentity Read(FileStream lease)
    {
        if (lease.Length != RecordLength)
            throw new IOException("Java 使用记录未完成或已损坏，请确认游戏退出后显式恢复。");
        Span<byte> record = stackalloc byte[RecordLength];
        lease.Position = 0;
        lease.ReadExactly(record);
        int processId = BinaryPrimitives.ReadInt32LittleEndian(record[8..12]);
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(record[12..]);
        if (!record[..8].SequenceEqual(Magic) || processId <= 0 || ticks <= 0 || ticks > DateTime.UtcNow.Ticks)
            throw new IOException("Java 使用记录身份无效，请确认游戏退出后显式恢复。");
        return new(processId, ticks);
    }

    internal void Write(FileStream lease)
    {
        Span<byte> record = stackalloc byte[RecordLength];
        Magic.CopyTo(record);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..12], ProcessId);
        BinaryPrimitives.WriteInt64LittleEndian(record[12..], StartTimeUtcTicks);
        lease.Position = 0;
        lease.SetLength(RecordLength);
        lease.Write(record);
        lease.Flush(flushToDisk: true);
    }

    internal bool HasDefinitelyExited()
    {
        var observation = Processes.Observe(ProcessId);
        // Only a later birth proves reuse. Unknown or an earlier birth cannot
        // establish that the recorded child terminated.
        return observation.State == PlatformProcessIdentityState.Exited
            || (observation.State == PlatformProcessIdentityState.Running
                && observation.StartTimeUtcTicks is { } actualStart && actualStart > StartTimeUtcTicks);
    }
}

/// <summary>Held for the actual process lifetime; independent sessions can share one runtime.</summary>
public sealed class JavaRuntimeUseLease : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _path;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _bindingStarted;
    private bool _knownExited;
    private JavaRuntimeLeaseIdentity? _identity;
    private JavaRuntimeUseLease(FileStream lease, string path) { _lease = lease; _path = path; }

    public static async ValueTask<JavaRuntimeUseLease?> AcquireAsync(string executable, CancellationToken token = default)
    {
        executable = Path.GetFullPath(executable);
        for (string? directory = Path.GetDirectoryName(executable); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string jobs = Path.Combine(directory, JavaInstallJournal.DirectoryName);
            if (!Directory.Exists(jobs)) continue;
            using var rootLease = JavaRuntimeManagedStore.AcquireRoot(directory);
            var plans = await JavaRuntimeManagedStore.PlansAsync(directory, token, includePending: true).ConfigureAwait(false);
            var plan = plans.FirstOrDefault(item => item.Files.Any(file => PathIdentity.Comparer.Equals(file.TargetPath, executable)));
            if (plan is null) return null;
            string uses = Path.Combine(directory, ".nexa-java-uses", plan.ComponentName);
            RecoveryBlobStore.CheckLinks(uses); Directory.CreateDirectory(uses); RecoveryBlobStore.CheckLinks(uses);
            string path = Path.Combine(uses, Guid.NewGuid().ToString("N") + ".lease");
            token.ThrowIfCancellationRequested();
            return new(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), path);
        }
        return null;
    }

    /// <summary>Durably binds the lease to the actual child before ownership is transferred.</summary>
    public void BindProcess(int processId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bindingStarted) throw new InvalidOperationException("Java 使用记录已经绑定或正在等待恢复。");
            // Once the caller has created a child, every failed binding must retain
            // the record; an unlocked partial record cannot prove that child exited.
            _bindingStarted = true;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
            var observation = JavaRuntimeLeaseIdentity.Processes.Observe(processId);
            if (observation.State == PlatformProcessIdentityState.Exited) { _knownExited = true; return; }
            if (observation.State != PlatformProcessIdentityState.Running
                || observation.StartTimeUtcTicks is not { } ticks || ticks <= 0 || ticks > DateTime.UtcNow.Ticks)
                throw new IOException("无法读取 Java 进程身份，已保留使用记录供显式恢复。");
            _identity = new(processId, ticks);
            _identity.Value.Write(_lease);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            bool remove = !_bindingStarted || _knownExited || (_identity?.HasDefinitelyExited() ?? false);
            _lease.Dispose();
            if (!remove) return;
            try { RecoveryBlobStore.CheckLinks(_path); File.Delete(_path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }
}
