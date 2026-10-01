
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Java;
using static Nexa.Services.Capabilities.MachineInstanceCatalog;
namespace Nexa.Services.Capabilities;

public static class MinecraftMachineFacts
{
    public static async ValueTask<IReadOnlyList<ICapability>> CollectJavaAsync(
        IJavaRuntimeLocator locator, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        IReadOnlyList<JavaRuntimeCandidate> runtimes;
        try
        {
            runtimes = await locator.FindAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            return Array.AsReadOnly(new ICapability[]
            {
                JavaInstalled.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, exception.Message),
            });
        }

        return CollectJava(runtimes, timestamp);
    }
    public static async ValueTask<IReadOnlyList<ICapability>> CollectMinecraftFilesAsync(
        IReadOnlyList<MinecraftExpectedFile> expected, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        int missing = 0;
        foreach (MinecraftExpectedFile file in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MinecraftFileVerifier.VerifyAsync(file, cancellationToken).ConfigureAwait(false))
            {
                missing++;
            }
        }

        const string source = "MinecraftFileVerifier";
        return Array.AsReadOnly(new ICapability[]
        {
            MinecraftFilesRequired.Observe(expected.Count, timestamp, source),
            MinecraftFilesMissing.Observe(missing, timestamp, source),
        });
    }
}
