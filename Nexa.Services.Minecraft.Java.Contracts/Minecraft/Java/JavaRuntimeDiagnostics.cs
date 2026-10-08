using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public static class JavaRuntimeDiagnosticsContract
{
    public static readonly XsrSemanticId Properties = XsrSemanticId.Parse("java.runtime.diagnostics.properties");
    public static readonly XsrSemanticId Modules = XsrSemanticId.Parse("java.runtime.diagnostics.modules");
}
public sealed record JavaRuntimePropertiesQuery(string Executable, long ExpectedRegistryRevision);
public sealed record JavaRuntimeModulesQuery(string Executable, long ExpectedRegistryRevision);
public enum JavaRuntimeDiagnosticStatus { Available, Rejected, Stale, PlatformUnsupported, IdentityChanged, TimedOut, OutputLimit, Failed, Malformed }
public sealed record JavaRuntimeDiagnosticFacts(string Version, string Vendor, string Architecture, string VirtualMachine,
    string VirtualMachineVersion, int MajorVersion, bool MatchesInventory);
public sealed record JavaRuntimeModule(string Name, string Version);
public sealed record JavaRuntimeDiagnosticsSnapshot(JavaRuntimeDiagnosticStatus Status, string Executable, string Fingerprint,
    JavaRuntimeDiagnosticFacts? Facts, IReadOnlyList<JavaRuntimeModule> Modules, string RedactedRaw, int RawUtf8Bytes);
