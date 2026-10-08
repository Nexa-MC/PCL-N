using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nexa.Services.Logging;

namespace Nexa.Services.Minecraft.Java;

/// <summary>
/// Installs a planned Mojang runtime with resumable, hash-verified file replacement.
/// The installer owns no global state and can therefore be hosted by a command handler.
/// </summary>
public sealed partial class JavaRuntimeInstaller : IConfirmedJavaRuntimeInstaller, IDisposable
{
    private readonly JavaRuntimeDownloadPlanService _planService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly LogService? _log;
    public event Action? RuntimesChanged;
    public Nexa.Services.Downloads.DownloadBandwidthLimiter? BandwidthLimiter { get; init; }

    public JavaRuntimeInstaller(IJavaRuntimeMetadataProvider metadataProvider, LogService? log = null, Nexa.Services.Tasks.TaskCenterService? tasks = null)
        : this(
            new JavaRuntimeDownloadPlanService(metadataProvider),
            CreateDefaultClient(log),
            ownsHttpClient: true, log, tasks)
    {
    }

    public JavaRuntimeInstaller(JavaRuntimeDownloadPlanService planService, HttpClient httpClient, bool ownsHttpClient = false, LogService? log = null, Nexa.Services.Tasks.TaskCenterService? tasks = null)
    {
        _planService = planService ?? throw new ArgumentNullException(nameof(planService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _log = log;
        _tasks = tasks;
    }

    private static HttpClient CreateDefaultClient(LogService? log)
    {
        var client = Nexa.Services.Downloads.PooledHttpClient.Create(log: log);
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    private async Task<string> InstallCoreAsync(
        string requestedComponent,
        string runtimeRootDirectory,
        IProgress<JavaRuntimeInstallProgress>? progress,
        JavaExecution execution,
        string? expectedPlanFingerprint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedComponent);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRootDirectory);

        using LogOperation? operation = _log?.BeginOperation("Java", "InstallRuntime", $"component={requestedComponent}");
        string? currentFile = null;
        var bandwidth = BandwidthLimiter?.Capture();
        FileStream? rootLease = null;
        try
        {
            runtimeRootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRootDirectory));
            Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(runtimeRootDirectory);
            Directory.CreateDirectory(runtimeRootDirectory);
            string lockPath = Path.Combine(runtimeRootDirectory, ".nexa-java.lock");
            Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(lockPath);
            rootLease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journal = await FindPendingAsync(runtimeRootDirectory, requestedComponent, cancellationToken).ConfigureAwait(false);
            JavaRuntimeDownloadPlan? reviewedNewPlan = null;
            if (journal is null && expectedPlanFingerprint is not null)
            {
                // A rejected manual preview must not leave an intent that startup could later acquire.
                reviewedNewPlan = await _planService.CreatePlanAsync(requestedComponent, DetectPlatform(), runtimeRootDirectory, cancellationToken).ConfigureAwait(false);
                ValidateReviewedPlan(reviewedNewPlan, expectedPlanFingerprint);
            }
            journal ??= await JavaInstallJournal.CreateAsync(runtimeRootDirectory, requestedComponent, cancellationToken).ConfigureAwait(false);
            execution.Ready.TrySetResult();
            if (journal.CancelRequested)
            {
                JavaRuntimeManagedStore.CheckUnused(runtimeRootDirectory, journal.Intent.Component);
                await journal.CancelAsync(MatchesAsync).ConfigureAwait(false);
                throw new IOException("Java 安装已撤回，请重试。");
            }
            operation?.Stage("resolve_runtime_metadata");
            JavaRuntimeDownloadPlan? plan = await journal.ReadPlanAsync(cancellationToken).ConfigureAwait(false);
            if (plan is null)
            {
                plan = reviewedNewPlan ?? await _planService.CreatePlanAsync(requestedComponent, DetectPlatform(), runtimeRootDirectory, cancellationToken).ConfigureAwait(false);
                ValidateReviewedPlan(plan, expectedPlanFingerprint);
                await journal.SavePlanAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            else ValidateReviewedPlan(plan, expectedPlanFingerprint);
            if (journal.Ready)
            {
                JavaRuntimeManagedStore.CheckUnused(runtimeRootDirectory, plan.ComponentName);
                await journal.PublishAsync(plan, MatchesAsync).ConfigureAwait(false);
                RuntimesChanged?.Invoke();
                return FindJavaExecutable(plan.TargetDirectory) ?? throw new IOException("Java 安装缺少可执行文件。");
            }
            bool existingValid = FindJavaExecutable(plan.TargetDirectory) is not null;
            foreach (var existing in plan.Files)
            {
                if (!existingValid) break;
                existingValid = await MatchesAsync(existing.TargetPath, existing, cancellationToken).ConfigureAwait(false);
            }
            if (existingValid)
            {
                foreach (var existing in plan.Files) ApplyExecutableMode(existing);
                await journal.CompleteExistingAsync(cancellationToken).ConfigureAwait(false);
                RuntimesChanged?.Invoke();
                string executable = FindJavaExecutable(plan.TargetDirectory)!;
                progress?.Report(new("complete", 1d, plan.Files.Count, plan.Files.Count, executable));
                operation?.Complete("Reused verified runtime");
                return executable;
            }
            Directory.CreateDirectory(journal.Payload);

            int total = Math.Max(plan.Files.Count, 1);
            int completed = 0;
            JavaRuntimeManagedStore.CheckUnused(runtimeRootDirectory, plan.ComponentName);
            progress?.Report(new JavaRuntimeInstallProgress("prepare", 0.02d, 0, total, plan.VersionName));

            operation?.Stage("verify_and_download_files", $"count={plan.Files.Count} target={plan.TargetDirectory}");
            foreach (JavaRuntimeDownloadFile plannedFile in plan.Files)
            {
                JavaRuntimeDownloadFile file = journal.StagedFile(plannedFile);
                currentFile = file.RelativePath;
                _log?.Trace("Java", $"Runtime file verification path={file.RelativePath} expected_bytes={file.Size}");
                cancellationToken.ThrowIfCancellationRequested();
                string? parent = Path.GetDirectoryName(file.TargetPath);
                if (parent is null) throw new InvalidOperationException($"Runtime file has no parent directory: {file.RelativePath}");
                Directory.CreateDirectory(parent);

                if (File.Exists(file.TargetPath) && await MatchesAsync(file.TargetPath, file, cancellationToken).ConfigureAwait(false))
                {
                    _log?.Trace("Java", $"Reusing verified runtime file path={file.RelativePath}");
                    ApplyExecutableMode(file);
                }
                else if (await MatchesAsync(plannedFile.TargetPath, plannedFile, cancellationToken).ConfigureAwait(false))
                {
                    File.Copy(plannedFile.TargetPath, file.TargetPath, overwrite: true);
                    ApplyExecutableMode(file);
                }
                else
                {
                    await DownloadFileAsync(file, bandwidth, cancellationToken).ConfigureAwait(false);
                }

                completed++;
                progress?.Report(new JavaRuntimeInstallProgress(
                    "download",
                    0.05d + 0.9d * completed / total,
                    completed,
                    total,
                    file.RelativePath));
            }

            operation?.Stage("locate_installed_executable");
            string? javaExecutable = FindJavaExecutable(journal.Payload);
            if (javaExecutable is null)
                throw new InvalidOperationException($"Java runtime was installed but no java executable was found in '{plan.TargetDirectory}'.");
            await journal.MarkReadyAsync(cancellationToken).ConfigureAwait(false);
            await journal.PublishAsync(plan, MatchesAsync).ConfigureAwait(false);
            javaExecutable = FindJavaExecutable(plan.TargetDirectory)!;
            progress?.Report(new JavaRuntimeInstallProgress("complete", 1d, total, total, javaExecutable));
            operation?.Complete($"files={completed} executable={javaExecutable}");
            RuntimesChanged?.Invoke();
            return javaExecutable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation?.Cancel();
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            _log?.Warn("Java", $"Runtime installation failed current_file={currentFile}");
            operation?.Fail(exception);
            throw;
        }
        finally { rootLease?.Dispose(); }
    }

    private static void ValidateReviewedPlan(JavaRuntimeDownloadPlan plan, string? expectedFingerprint)
    {
        if (expectedFingerprint is not null && JavaRuntimePlanIdentity.Fingerprint(plan) != expectedFingerprint)
            throw new IOException("Java 下载计划已变化，请重新预览。");
    }

    public static JavaRuntimePlatform DetectPlatform()
    {
        JavaRuntimeOperatingSystem operatingSystem = OperatingSystem.IsWindows()
            ? JavaRuntimeOperatingSystem.Win32
            : OperatingSystem.IsMacOS()
                ? JavaRuntimeOperatingSystem.MacOs
                : JavaRuntimeOperatingSystem.Linux;
        JavaRuntimeArchitecture architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => JavaRuntimeArchitecture.X86,
            Architecture.Arm64 => JavaRuntimeArchitecture.Arm64,
            _ => JavaRuntimeArchitecture.X64,
        };
        return new JavaRuntimePlatform(operatingSystem, architecture);
    }

    public static string GetDefaultRuntimeRoot(string applicationDataDirectory) =>
        JavaRuntimePackagePlannerRoot(applicationDataDirectory);

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static string JavaRuntimePackagePlannerRoot(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        string current = Path.Combine(Path.GetFullPath(applicationDataDirectory), "Nexa", "runtime");
        string legacy = Path.Combine(Path.GetFullPath(applicationDataDirectory), "PCL-N", "runtime");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    private static async Task<bool> MatchesAsync(string path, JavaRuntimeDownloadFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        Nexa.Services.Minecraft.Management.RecoveryBlobStore.CheckLinks(path);
        if (file.Size >= 0 && new FileInfo(path).Length != file.Size) return false;
        if (string.IsNullOrWhiteSpace(file.Sha1)) return true;
        string actual = await ComputeSha1Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadFileAsync(JavaRuntimeDownloadFile file, Nexa.Services.Downloads.DownloadBandwidthBudget? bandwidth, CancellationToken cancellationToken)
    {
        _log?.Debug("Java", $"Downloading runtime file path={file.RelativePath}");
        using HttpResponseMessage response = await _httpClient.GetAsync(
            file.Url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string temporary = file.TargetPath + ".download";
        try
        {
            await using (Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (file.Size is < 0 or > 512L * 1024 * 1024) throw new InvalidDataException("Java 下载文件大小超过限制。");
                byte[] buffer = new byte[64 * 1024];
                long received = 0;
                while (true)
                {
                    int count = (int)Math.Min(Math.Min(buffer.Length, bandwidth?.MaximumReadBytes ?? buffer.Length), file.Size - received + 1);
                    int read = await network.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > file.Size - received) throw new InvalidDataException("Java 下载文件实际长度与声明不一致。");
                    if (bandwidth is not null) await bandwidth.WaitAsync(read, cancellationToken).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                }
                if (received != file.Size) throw new InvalidDataException("Java 下载文件读取不完整。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }

            if (!await MatchesAsync(temporary, file, cancellationToken).ConfigureAwait(false))
            {
                _log?.Warn("Java", $"Runtime integrity check failed path={file.RelativePath} expected_bytes={file.Size}");
                throw new InvalidOperationException($"Java runtime file hash or size mismatch: {file.RelativePath}");
            }
            File.Move(temporary, file.TargetPath, overwrite: true);
            ApplyExecutableMode(file);
            temporary = string.Empty;
        }
        finally
        {
            if (temporary.Length > 0)
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
#pragma warning disable CA5350 // Mojang runtime manifests publish SHA-1 digests.
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ApplyExecutableMode(JavaRuntimeDownloadFile file)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) || !file.Executable) return;
        try
        {
            UnixFileMode current = File.GetUnixFileMode(file.TargetPath);
            UnixFileMode executable = current | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (executable != current) File.SetUnixFileMode(file.TargetPath, executable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not mark Java runtime file executable: {file.RelativePath}", exception);
        }
    }

    private static string? FindJavaExecutable(string root)
    {
        string executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        try
        {
            return Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
