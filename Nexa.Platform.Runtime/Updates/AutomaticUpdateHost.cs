using System.Diagnostics;

namespace Nexa.Platform.Updates;

/// <summary>Only a preinstalled admitted helper can receive OS administrator authorization.</summary>
public sealed class AutomaticUpdateHost : IUpdateHost
{
    public string InstallationPath => UpdateInstallation.Root;
    public bool IsSystemInstallation
    {
        get
        {
            string directory = System.IO.Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            string root = UpdateInstallation.Root;
            return directory.Equals(OperatingSystem.IsMacOS() ? root + "/Nexa.app/Contents/MacOS" : root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || directory.StartsWith(root + System.IO.Path.DirectorySeparatorChar + ".nexa-slot-", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
    public async Task<int> RunHelperAsync(string version, string channel, CancellationToken token)
    {
        if (!IsSystemInstallation) throw new UnauthorizedAccessException("便携安装请使用系统安装包更新。");
        if (version.Length is 0 or > 128 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.') || channel is not ("alpha" or "beta" or "stable" or "rollback"))
            throw new ArgumentException("更新请求无效。");
        using IUpdateDirectory installation = ProtectedUpdateDirectory.Open(UpdateInstallation.Root);
        using FileStream helper = installation.OpenRead(System.IO.Path.GetFileName(UpdateInstallation.Helper));
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new(UpdateInstallation.Helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(version); start.ArgumentList.Add(channel);
        }
        else if (OperatingSystem.IsMacOS())
        {
            start = new("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
            // Constant AppleScript program; strings become separate arguments, never script syntax.
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("on run argv\nreturn do shell script \"/usr/bin/env -i PATH=/usr/bin:/bin \" & quoted form of item 1 of argv & \" \" & quoted form of item 2 of argv & \" \" & quoted form of item 3 of argv with administrator privileges\nend run");
            start.ArgumentList.Add(UpdateInstallation.Helper); start.ArgumentList.Add(version); start.ArgumentList.Add(channel);
        }
        else
        {
            using IUpdateDirectory system = ProtectedUpdateDirectory.Open("/usr/bin");
            using FileStream tool = system.OpenRead("pkexec");
            start = new("/usr/bin/pkexec") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(UpdateInstallation.Helper); start.ArgumentList.Add(version); start.ArgumentList.Add(channel);
        }
        token.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new IOException("无法启动更新 helper。");
        // Do not kill an elevated process during activation. UI cancellation only stops waiting.
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return process.ExitCode;
    }
    public void RestartLauncher()
    {
        using IUpdateDirectory root = ProtectedUpdateDirectory.Open(UpdateInstallation.Root);
        using Process process = Process.Start(new ProcessStartInfo(UpdateInstallation.Bootstrap) { UseShellExecute = false })
            ?? throw new IOException("无法重新启动 NexaCL。");
    }
}
