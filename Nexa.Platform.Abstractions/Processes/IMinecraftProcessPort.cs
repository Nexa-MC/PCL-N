using System.Diagnostics;


namespace Nexa.Services.Minecraft.Process;


public interface IMinecraftProcessPort
{
    ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default);
}
