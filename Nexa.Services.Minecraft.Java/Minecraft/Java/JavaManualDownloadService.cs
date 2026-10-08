using System.Globalization;
using Nexa.Core;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

/// <summary>Finite confirmed acquisition over the existing managed installer lifetime.</summary>
public sealed class JavaManualDownloadService(IJavaRuntimeMetadataProvider metadata, IConfirmedJavaRuntimeInstaller installer,
    IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore registry, string runtimeRoot, JavaRuntimePlatform platform) : IDisposable
{
    private sealed record PreviewEntry(JavaManualPreview Preview, JavaRuntimeDownloadPlan Plan, long Revision);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PreviewEntry> _previews = [];
    private readonly Dictionary<Guid, JavaManualReceipt> _receipts = [];
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
    private Guid _active;
    private CancellationTokenSource? _stop;
    private bool _disposed;

    public async ValueTask<XsrResult<JavaManualPreview>> PreviewAsync(JavaManualPreviewQuery query, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (query.Major is not (8 or 17 or 21 or 25) || registry.Read().Revision != query.ExpectedRegistryRevision)
            return Reject<JavaManualPreview>("Java 下载选择或登记已变化，请重新预览。");
        if (platform is { OperatingSystem: JavaRuntimeOperatingSystem.Linux, Architecture: JavaRuntimeArchitecture.Arm64 })
            return Reject<JavaManualPreview>("Mojang 未提供此平台的原生 Java 分发。");
        try
        {
            string index = await metadata.GetRuntimeIndexAsync(token).ConfigureAwait(false);
            if (index.Length > 4 * 1024 * 1024) return Reject<JavaManualPreview>("Java 下载清单超过预算。");
            var descriptor = JavaRuntimePackagePlanner.SelectPackage(index, platform, query.Major == 8 ? "jre-legacy" : query.Major.ToString(CultureInfo.InvariantCulture));
            if (!TrustedUrl(descriptor.ManifestUrl)) return Reject<JavaManualPreview>("Java 下载清单来源不受支持。");
            string manifest = await metadata.GetManifestAsync(descriptor.ManifestUrl, token).ConfigureAwait(false);
            if (manifest.Length > 4 * 1024 * 1024) return Reject<JavaManualPreview>("Java 下载清单超过预算。");
            var plan = JavaRuntimePackagePlanner.CreateDownloadPlan(descriptor, manifest, _root);
            if (plan.ComponentName.Length is < 1 or > 128 || !plan.ComponentName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                || plan.VersionName.Length > 128 || VersionMajor(plan.VersionName) != query.Major || plan.Files.Count is < 1 or > 4096
                || !plan.Files.Any(file => Path.GetFileName(file.RelativePath) is "java" or "java.exe")
                || plan.Files.Select(file => file.TargetPath).Distinct(PathIdentity.Comparer).Count() != plan.Files.Count
                || plan.Files.Any(file => file.RelativePath.Length > 1024 || file.RelativePath.Any(char.IsControl) || file.TargetPath.Length > 4096 || !TrustedUrl(file.Url)
                    || file.Sha1.Length != 40 || !file.Sha1.All(char.IsAsciiHexDigit) || file.Size is < 0 or > 512L * 1024 * 1024))
                return Reject<JavaManualPreview>("Java 下载清单未通过完整性检查。");
            long bytes = checked(plan.Files.Sum(file => file.Size));
            if (bytes > 2L * 1024 * 1024 * 1024) return Reject<JavaManualPreview>("Java 下载清单超过预算。");
            string[] licenses = plan.Files.Where(file => IsLicense(file.RelativePath)).Select(file => file.Url).Distinct(StringComparer.Ordinal).Take(8).ToArray();
            if (licenses.Length == 0) return Reject<JavaManualPreview>("Java 下载清单未提供许可证文件，不能确认安装。");
            token.ThrowIfCancellationRequested();
            var preview = new JavaManualPreview(Guid.NewGuid(), query.Major, "Mojang distribution", plan.VersionName, platform.ToMojangKey(), plan.TargetDirectory,
                plan.Files.Count, bytes, JavaRuntimePlanIdentity.Fingerprint(plan), Array.AsReadOnly(licenses), DateTimeOffset.UtcNow.AddMinutes(10));
            lock (_gate)
            {
                if (_disposed || registry.Read().Revision != query.ExpectedRegistryRevision) return Reject<JavaManualPreview>("Java 下载选择或登记已变化，请重新预览。");
                foreach (var expired in _previews.Where(entry => entry.Value.Preview.ExpiresAt <= DateTimeOffset.UtcNow).Select(entry => entry.Key).ToArray()) _previews.Remove(expired);
                if (_previews.Count >= 8) _previews.Remove(_previews.OrderBy(entry => entry.Value.Preview.ExpiresAt).First().Key);
                _previews.Add(preview.Id, new(preview, plan, query.ExpectedRegistryRevision));
            }
            return XsrResult.Success(preview);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException or System.Text.Json.JsonException)
        { return Reject<JavaManualPreview>("无法读取有效的 Java 下载预览。"); }
    }

    public async ValueTask<XsrResult> InstallAsync(JavaManualInstallCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); PreviewEntry entry; CancellationTokenSource stop;
        lock (_gate)
        {
            if (_disposed || !command.AcceptLicense || _active != Guid.Empty || !_previews.TryGetValue(command.PreviewId, out entry!)
                || entry.Preview.ExpiresAt <= DateTimeOffset.UtcNow || registry.Read().Revision != entry.Revision)
                return Reject("请重新预览并确认许可证后安装 Java。");
            _previews.Remove(command.PreviewId); _active = command.PreviewId;
            stop = CancellationTokenSource.CreateLinkedTokenSource(token); _stop = stop;
            while (_receipts.Count >= 16) _receipts.Remove(_receipts.Keys.First());
            _receipts.Add(command.PreviewId, new(command.PreviewId, JavaManualInstallStatus.Downloading, 0, 0, entry.Plan.Files.Count, "", "", "", null, null));
        }
        try
        {
            var progress = new InlineProgress(value =>
            {
                lock (_gate) if (_receipts.TryGetValue(command.PreviewId, out var receipt)) _receipts[command.PreviewId] = receipt with
                {
                    Progress = double.IsFinite(value.Progress) ? Math.Clamp(value.Progress, 0, 1) : 0,
                    CompletedFiles = Math.Clamp(value.CompletedFiles, 0, entry.Plan.Files.Count),
                    Detail = value.Detail is { } text ? text[..Math.Min(text.Length, 256)] : ""
                };
            });
            string executable = await installer.InstallConfirmedAsync(entry.Plan.ComponentName, _root, entry.Preview.PlanFingerprint, progress, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(executable) || !Path.GetFullPath(executable).StartsWith(entry.Plan.TargetDirectory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !File.Exists(executable)) return Finish(JavaManualInstallStatus.InstalledUnverified, executable);
            locator.Invalidate(); var candidate = await locator.InspectAsync(executable, stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            if (candidate is not { IsAvailable: true } || candidate.Installation.MajorVersion != entry.Preview.Major
                || !PathIdentity.Comparer.Equals(candidate.Installation.JavaExecutablePath, executable)) return Finish(JavaManualInstallStatus.InstalledUnverified, executable);
            var before = registry.Read();
            var registrations = before.Registrations.Where(item => !PathIdentity.Comparer.Equals(item.Executable, executable)).Append(new JavaRuntimeRegistration(executable, Custom: false)).ToArray();
            var registered = before.Revision == entry.Revision && registrations.Length <= 64 && registry.Write(before.Revision, Array.AsReadOnly(registrations)).IsSuccess;
            locator.Invalidate(); return Finish(registered ? JavaManualInstallStatus.Installed : JavaManualInstallStatus.InstalledUnregistered, executable, candidate.Installation);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return Finish(JavaManualInstallStatus.Canceled); }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or OverflowException or System.Text.Json.JsonException)
        { return Finish(JavaManualInstallStatus.Failed); }
        finally { lock (_gate) { if (ReferenceEquals(_stop, stop)) { _active = default; _stop = null; } } stop.Dispose(); }

        XsrResult Finish(JavaManualInstallStatus status, string executable = "", JavaInstallation? facts = null)
        {
            lock (_gate) _receipts[command.PreviewId] = _receipts[command.PreviewId] with
            {
                Status = status,
                Executable = executable,
                ActualVersion = facts?.Version.ToString() ?? "",
                ActualBrand = facts?.Brand,
                ActualArchitecture = facts?.Architecture
            };
            return status == JavaManualInstallStatus.Installed ? XsrResult.Success() : Reject("Java 安装未完成全部验证与登记，请查看实际结果。");
        }
    }

    public ValueTask<XsrResult<JavaManualReceipt>> StatusAsync(JavaManualStatusQuery query, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_receipts.TryGetValue(query.PreviewId, out var receipt)
            ? XsrResult.Success(receipt) : Reject<JavaManualReceipt>("没有此 Java 下载操作的记录。"));
    }
    public ValueTask<XsrResult> CancelAsync(JavaManualCancelCommand command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (_active != command.PreviewId || _stop is null) return ValueTask.FromResult(Reject("此 Java 下载操作已结束。"));
            _stop.Cancel(); return ValueTask.FromResult(XsrResult.Success());
        }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _stop?.Cancel(); _previews.Clear(); } }
    private sealed class InlineProgress(Action<JavaRuntimeInstallProgress> report) : IProgress<JavaRuntimeInstallProgress> { public void Report(JavaRuntimeInstallProgress value) => report(value); }
    private static bool IsLicense(string path) => Path.GetFileName(path).Contains("license", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path) is "COPYRIGHT" or "ASSEMBLY_EXCEPTION";
    private static int VersionMajor(string value)
    {
        if (Version.TryParse(value.Split(['_', '+', '-'])[0], out var version)) return version.Major == 1 ? version.Minor : version.Major;
        // Mojang's legacy catalog uses the actual distribution label "8u202".
        int update = value.IndexOf('u', StringComparison.Ordinal);
        return update > 0 && update + 1 < value.Length && int.TryParse(value.AsSpan(0, update), NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            && value.AsSpan(update + 1).IndexOfAnyExceptInRange('0', '9') < 0 ? major : -1;
    }
    private static bool TrustedUrl(string text) => text.Length <= 2048 && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Host is "piston-data.mojang.com" or "launcher.mojang.com" or "launchermeta.mojang.com";
    private static XsrResult Reject(string text) => XsrResult.Failure(new(XsrErrorKind.Rejected, JavaManualDownloadContract.Install, text));
    private static XsrResult<T> Reject<T>(string text) => XsrResult.Failure<T>(new(XsrErrorKind.Rejected, JavaManualDownloadContract.Preview, text));
}
