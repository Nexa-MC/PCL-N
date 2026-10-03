using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public sealed class JavaRuntimeInventoryService(IJavaRuntimeLocator locator, IJavaRuntimeRegistrationStore? registry = null)
{
    public ValueTask<XsrResult<JavaRuntimeInventorySnapshot>> ReadAsync(JavaRuntimeInventoryQuery query, CancellationToken cancellationToken = default)
        => new(Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Refresh) locator.Invalidate();
            var registered = registry?.Read();
            var candidates = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var runtimes = candidates.GroupBy(candidate => candidate.Installation.JavaExecutablePath,
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.IsAvailable && candidate.IsEnabled).First())
                .OrderByDescending(candidate => candidate.Installation.MajorVersion)
                .ThenBy(candidate => candidate.Installation.Brand)
                .ThenBy(candidate => candidate.Installation.JavaExecutablePath, StringComparer.Ordinal).ToArray();
            return XsrResult.Success(new JavaRuntimeInventorySnapshot(Array.AsReadOnly(runtimes))
            { RegistryRevision = registered?.Revision ?? 0, Registrations = registered?.Registrations ?? [] });
        }, cancellationToken));
}
