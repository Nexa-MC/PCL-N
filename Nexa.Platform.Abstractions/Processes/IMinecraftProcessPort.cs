using System.Diagnostics;


namespace Nexa.Services.Minecraft.Process;


public interface IMinecraftProcessPort
{
    /// <summary>
    /// Whether this trusted transport keeps the supplied launch arguments out of public OS
    /// process arguments. The standard OS process transport does not provide this guarantee.
    /// </summary>
    bool UsesPrivateArgumentTransport => false;

    ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default);
}
