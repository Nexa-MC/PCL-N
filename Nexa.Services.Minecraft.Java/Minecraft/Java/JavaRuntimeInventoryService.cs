using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public sealed class JavaRuntimeInventoryService(IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore? registry,
    IReadOnlyList<string>? managedRuntimeRoots)
{
    public JavaRuntimeInventoryService(IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore? registry = null)
        : this(locator, registry, null) { }

    public ValueTask<XsrResult<JavaRuntimeInventorySnapshot>> ReadAsync(JavaRuntimeInventoryQuery query, CancellationToken cancellationToken = default)
        => new(Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Refresh) locator.Invalidate();
            var registered = registry?.Read();
            var candidates = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
            var managed = await new JavaRuntimeManagedStore(managedRuntimeRoots ?? []).ReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (registered is not null && registry!.Read().Revision != registered.Revision)
                return XsrResult.Failure<JavaRuntimeInventorySnapshot>(new(XsrErrorKind.Rejected,
                    XsrSemanticId.Parse("java.runtime.inventory.stale"), "Java 列表已变化，请重新扫描。"));
            var runtimes = candidates.Select(candidate => managed.Any(entry => Nexa.Core.PathIdentity.Comparer.Equals(entry.Executable, candidate.Installation.JavaExecutablePath))
                    ? candidate with { Source = JavaSource.AutoInstalled } : candidate).GroupBy(candidate => candidate.Installation.JavaExecutablePath,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.IsAvailable && candidate.IsEnabled).First())
                .OrderByDescending(candidate => candidate.Installation.MajorVersion)
                .ThenBy(candidate => candidate.Installation.Brand)
                .ThenBy(candidate => candidate.Installation.JavaExecutablePath, StringComparer.Ordinal).ToArray();
            return XsrResult.Success(new JavaRuntimeInventorySnapshot(Array.AsReadOnly(runtimes))
            { RegistryRevision = registered?.Revision ?? 0, Registrations = registered?.Registrations ?? [], ManagedRuntimes = managed });
        }, cancellationToken));
}
