
using System.Diagnostics;


namespace Nexa.Services.Minecraft.Process;


public sealed class SystemMinecraftProcessPort : IMinecraftProcessPort
{
    public ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        System.Diagnostics.Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Minecraft process could not be started.");
        return ValueTask.FromResult(process);
    }
}
