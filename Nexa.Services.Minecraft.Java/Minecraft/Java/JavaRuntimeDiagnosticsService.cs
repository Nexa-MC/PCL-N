using Nexa.Core;
using Nexa.Platform;
using Nexa.Services.Logging;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

/// <summary>Inventory admission and immutable facts over a fixed Platform Java probe port.</summary>
public sealed class JavaRuntimeDiagnosticsService(JavaRuntimeInventoryService inventory, IPlatformJavaDiagnostics platform)
{
    public ValueTask<XsrResult<JavaRuntimeDiagnosticsSnapshot>> ReadPropertiesAsync(JavaRuntimePropertiesQuery query, CancellationToken token = default)
        => ReadAsync(query.Executable, query.ExpectedRegistryRevision, PlatformJavaProbeKind.Properties, token);
    public ValueTask<XsrResult<JavaRuntimeDiagnosticsSnapshot>> ReadModulesAsync(JavaRuntimeModulesQuery query, CancellationToken token = default)
        => ReadAsync(query.Executable, query.ExpectedRegistryRevision, PlatformJavaProbeKind.Modules, token);

    private async ValueTask<XsrResult<JavaRuntimeDiagnosticsSnapshot>> ReadAsync(string executable, long revision, PlatformJavaProbeKind kind, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Length > 8192 || executable.Any(char.IsControl) || !Path.IsPathFullyQualified(executable))
            return Empty(JavaRuntimeDiagnosticStatus.Rejected, "");
        executable = Path.GetFullPath(executable);
        var read = await inventory.ReadAsync(new(), token).ConfigureAwait(false);
        if (!read.IsSuccess || read.Value is not { } snapshot || snapshot.RegistryRevision != revision) return Empty(JavaRuntimeDiagnosticStatus.Stale, executable);
        var candidate = snapshot.Runtimes.FirstOrDefault(item => item.IsAvailable && PathIdentity.Comparer.Equals(item.Installation.JavaExecutablePath, executable));
        if (candidate is null) return Empty(JavaRuntimeDiagnosticStatus.Rejected, executable);
        if (kind == PlatformJavaProbeKind.Modules && candidate.Installation.MajorVersion < 9)
            return Empty(JavaRuntimeDiagnosticStatus.PlatformUnsupported, executable);
        PlatformJavaExecutableIdentity identity;
        PlatformJavaProbeOutput result;
        try
        {
            identity = await platform.CaptureIdentityAsync(executable, token).ConfigureAwait(false);
            result = await platform.ProbeAsync(identity, kind, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Empty(JavaRuntimeDiagnosticStatus.Failed, executable); }
        token.ThrowIfCancellationRequested();
        var current = await inventory.ReadAsync(new(), token).ConfigureAwait(false);
        if (!current.IsSuccess || current.Value?.RegistryRevision != revision) return Empty(JavaRuntimeDiagnosticStatus.Stale, executable);
        string raw = result.StandardOutput + "\n" + result.StandardError;
        var preview = DiagnosticRawWorkspace.Preview(RemovePersonalProperties(raw));
        JavaRuntimeDiagnosticStatus status = result.Status switch
        {
            PlatformJavaProbeStatus.Available => JavaRuntimeDiagnosticStatus.Available,
            PlatformJavaProbeStatus.IdentityChanged => JavaRuntimeDiagnosticStatus.IdentityChanged,
            PlatformJavaProbeStatus.TimedOut => JavaRuntimeDiagnosticStatus.TimedOut,
            PlatformJavaProbeStatus.OutputLimit => JavaRuntimeDiagnosticStatus.OutputLimit,
            PlatformJavaProbeStatus.Rejected => JavaRuntimeDiagnosticStatus.Rejected,
            _ => JavaRuntimeDiagnosticStatus.Failed,
        };
        JavaRuntimeDiagnosticFacts? facts = null; IReadOnlyList<JavaRuntimeModule> modules = [];
        if (status == JavaRuntimeDiagnosticStatus.Available && kind == PlatformJavaProbeKind.Properties)
        {
            string Property(string key) => raw.Split('\n').Select(line => line.Split('=', 2)).FirstOrDefault(parts => parts.Length == 2 && parts[0].Trim() == key)
                is { } match ? match[1].Trim() : "";
            string vendor = Property("java.vendor"), arch = Property("os.arch"), vm = Property("java.vm.name"), vmVersion = Property("java.vm.version");
            if (!LocalJavaRuntimeLocator.TryCreateCandidate(executable, raw, out var probed) || probed is null
                || vendor.Length is < 1 or > 256 || arch.Length is < 1 or > 64 || vm.Length > 256 || vmVersion.Length > 128
                || probed.Installation.Architecture == JavaArchitecture.Unknown)
                status = JavaRuntimeDiagnosticStatus.Malformed;
            else facts = new(probed.Installation.Version.ToString(), vendor, arch, vm, vmVersion,
                probed.Installation.MajorVersion, probed.Installation.Version == candidate.Installation.Version
                    && probed.Installation.Architecture == candidate.Installation.Architecture && probed.Installation.Brand == candidate.Installation.Brand);
        }
        if (status == JavaRuntimeDiagnosticStatus.Available && kind == PlatformJavaProbeKind.Modules)
        {
            var rows = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rows.Length is < 1 or > 2048 || rows.Any(line => line.Length > 256 || line.Any(char.IsWhiteSpace) || line.Any(char.IsControl)))
                status = JavaRuntimeDiagnosticStatus.Malformed;
            else
            {
                var parsed = rows.Select(line => { int at = line.IndexOf('@'); return new JavaRuntimeModule(at < 0 ? line : line[..at], at < 0 ? "" : line[(at + 1)..]); }).ToArray();
                if (!parsed.Any(module => module.Name == "java.base") || parsed.Any(module => module.Name.Length == 0
                    || module.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '$')))
                    || parsed.Select(module => module.Name).Distinct(StringComparer.Ordinal).Count() != parsed.Length)
                    status = JavaRuntimeDiagnosticStatus.Malformed;
                else modules = Array.AsReadOnly(parsed);
            }
        }
        return XsrResult.Success(new JavaRuntimeDiagnosticsSnapshot(status, executable, identity.Sha256, facts, modules, preview.RedactedText, preview.Utf8Bytes));
    }

    private static string RemovePersonalProperties(string raw)
    {
        string[] hidden = ["java.home", "user.home", "user.dir", "user.name", "java.library.path", "sun.boot.library.path", "java.class.path", "sun.java.command"];
        return string.Join('\n', raw.Split('\n').Select(line => line.IndexOf('=') is int separator && separator >= 0
            && hidden.Contains(line[..separator].Trim(), StringComparer.Ordinal) ? line[..(separator + 1)] + " <redacted>" : line));
    }
    private static XsrResult<JavaRuntimeDiagnosticsSnapshot> Empty(JavaRuntimeDiagnosticStatus status, string executable) =>
        XsrResult.Success(new JavaRuntimeDiagnosticsSnapshot(status, executable, "", null, [], "", 0));
}
