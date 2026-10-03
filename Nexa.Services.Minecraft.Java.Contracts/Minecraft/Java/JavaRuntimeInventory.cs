using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public static class JavaRuntimeInventoryContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("java.runtime.inventory.read");
    public static readonly XsrSemanticId Manage = XsrSemanticId.Parse("java.runtime.registry.manage");
    public const string RegistryKey = "NexaJavaRuntimeRegistry";
}
public sealed record JavaRuntimeInventoryQuery(bool Refresh = false);
public sealed record JavaRuntimeInventorySnapshot(IReadOnlyList<JavaRuntimeCandidate> Runtimes)
{
    public long RegistryRevision { get; init; }
    public IReadOnlyList<JavaRuntimeRegistration> Registrations { get; init; } = [];
}
public sealed record JavaRuntimeRegistration(string Executable, bool Enabled = true, bool Custom = true);
public sealed record JavaRuntimeRegistrySnapshot(long Revision, IReadOnlyList<JavaRuntimeRegistration> Registrations);
public enum JavaRuntimeManagementAction { Add, Remove, Enable, Disable }
public sealed record JavaRuntimeManageCommand(string Executable, JavaRuntimeManagementAction Action, long ExpectedRevision);
public interface IJavaRuntimeRegistrationStore
{
    JavaRuntimeRegistrySnapshot Read();
    XsrResult Write(long expectedRevision, IReadOnlyList<JavaRuntimeRegistration> registrations);
}
