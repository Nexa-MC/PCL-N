using System.Diagnostics;

namespace Nexa.Services.Updates;


/// <summary>
/// Process launch port. The real implementation starts the process and releases the handle
/// immediately — the replacement process outlives the updater by design.
/// </summary>
public interface IProcessLauncher
{
    void Launch(ProcessStartInfo startInfo);
}
