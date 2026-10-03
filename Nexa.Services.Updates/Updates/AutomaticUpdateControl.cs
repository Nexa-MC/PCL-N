using Nexa.Platform.Updates;

namespace Nexa.Services.Updates;

/// <summary>Unprivileged async projection and OS-authorized requests. Owns no mutation paths.</summary>
public sealed class AutomaticUpdateControl(IUpdateHost host) : IAutomaticUpdateControl
{
    public Task<AutomaticUpdateStatus> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!host.IsSystemInstallation) return new AutomaticUpdateStatus(null, null, "manual", false, false);
        using IUpdateDirectory root = ProtectedUpdateDirectory.Open(host.InstallationPath);
        string[]? status = ReadOptional(root, AutomaticUpdateTransaction.StatusName);
        string[]? active = ReadOptional(root, AutomaticUpdateTransaction.ActivationName);
        using FileStream helper = root.OpenRead("Nexa.Update.Helper" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        // A process exit after rollback activation but before status publication must not
        // advertise the superseded update. Activation is the authoritative selected version.
        if (status is { Length: 3 } && status[2] == "complete" && active is { Length: 4 } && active[0] != status[0])
            return new AutomaticUpdateStatus(active[0], null, "rolledback", true, active[2].Length != 0);
        return new AutomaticUpdateStatus(status is { Length: 3 } ? status[0] : null,
            status is { Length: 3 } ? status[1] : null, status is { Length: 3 } ? status[2] : "idle", true,
            active is { Length: 4 } && active[2].Length != 0);
    }, token);
    public async Task<AutomaticUpdateStatus> InstallAsync(string version, string channel, CancellationToken token)
    {
        int result = await host.RunHelperAsync(version, channel, token).ConfigureAwait(false);
        if (result != 0) throw new IOException("自动更新未完成。可继续更新，或下载安装包。");
        AutomaticUpdateStatus status = await ReadAsync(token).ConfigureAwait(false);
        if (status.Version != version || status.Phase != "complete") throw new IOException("更新尚未提交完整版本。");
        return status;
    }
    public async Task<AutomaticUpdateStatus> RollbackAsync(CancellationToken token)
    {
        if (await host.RunHelperAsync("2.0.0", "rollback", token).ConfigureAwait(false) != 0) throw new IOException("回滚未完成。");
        return await ReadAsync(token).ConfigureAwait(false);
    }
    public void Restart() => host.RestartLauncher();
    private static string[]? ReadOptional(IUpdateDirectory root, string name)
    {
        try { using FileStream file = root.OpenRead(name); return UpdateTransactionJournal.Read(file); }
        catch (IOException error) when (error is FileNotFoundException || error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }) { return null; }
    }
}
