using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public static class JavaRuntimeInventoryContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("java.runtime.inventory.read");
}
public sealed record JavaRuntimeInventoryQuery(bool Refresh = false);
public sealed record JavaRuntimeInventorySnapshot(IReadOnlyList<JavaRuntimeCandidate> Runtimes);
