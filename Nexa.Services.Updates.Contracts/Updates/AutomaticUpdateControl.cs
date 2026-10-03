namespace Nexa.Services.Updates;

public sealed record AutomaticUpdateStatus(string? Version, string? Channel, string Phase, bool CanInstall, bool CanRollback);
public interface IAutomaticUpdateControl
{
    Task<AutomaticUpdateStatus> ReadAsync(CancellationToken token);
    Task<AutomaticUpdateStatus> InstallAsync(string version, string channel, CancellationToken token);
    Task<AutomaticUpdateStatus> RollbackAsync(CancellationToken token);
    void Restart();
}
