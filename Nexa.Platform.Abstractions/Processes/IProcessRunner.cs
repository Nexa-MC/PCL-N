

namespace Nexa.Services.Updates;


/// <summary>
/// External process execution port. Production runs the real tool; tests substitute a fake
/// that asserts arguments and simulates the tool's effect.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs the executable and returns its exit code (zero means success).</summary>
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}
