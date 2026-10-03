namespace Nexa.Platform.Updates;

public interface IUpdateHost
{
    string InstallationPath { get; }
    bool IsSystemInstallation { get; }
    Task<int> RunHelperAsync(string version, string channel, CancellationToken token);
    void RestartLauncher();
}
