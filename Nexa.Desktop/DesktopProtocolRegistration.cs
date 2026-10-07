using System.Diagnostics;
using Microsoft.Win32;

namespace Nexa.Desktop;

internal static class DesktopProtocolRegistration
{
    internal static IReadOnlyList<string> LauncherCommand()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("无法定位启动器。");
        return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [executable, Path.GetFullPath(Environment.GetCommandLineArgs()[0])] : [executable];
    }

    internal static string WindowsCommand(IReadOnlyList<string> command) =>
        string.Join(" ", command.Select(argument => "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")) + " \"%1\"";

    internal static string LinuxDesktopEntry(IReadOnlyList<string> command) =>
        "[Desktop Entry]\nType=Application\nName=NexaCL Firefly\nNoDisplay=true\nTerminal=false\n"
        + "Exec=" + string.Join(" ", command.Select(argument => "\"" + argument.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\""))
        + " %u\nMimeType=x-scheme-handler/nexacl;\n";

    internal static async Task<string?> RegisterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<string> command = LauncherCommand();
            if (OperatingSystem.IsWindows())
            {
                using RegistryKey protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\nexacl");
                protocol.SetValue("", "URL:NexaCL Protocol");
                protocol.SetValue("URL Protocol", "");
                using RegistryKey icon = protocol.CreateSubKey("DefaultIcon");
                icon.SetValue("", "\"" + command[0] + "\",0");
                using RegistryKey open = protocol.CreateSubKey(@"shell\open\command");
                open.SetValue("", WindowsCommand(command));
                return null;
            }
            if (OperatingSystem.IsLinux())
            {
                string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } configured && Path.IsPathFullyQualified(configured)
                    ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                string directory = Path.Combine(data, "applications");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "nexacl-protocol.desktop");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, LinuxDesktopEntry(command), cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return await RunAsync("xdg-mime", ["default", "nexacl-protocol.desktop", "x-scheme-handler/nexacl"], cancellationToken).ConfigureAwait(false);
            }
            if (OperatingSystem.IsMacOS())
            {
                string? bundle = AppContext.BaseDirectory;
                while (bundle is not null && !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) bundle = Path.GetDirectoryName(bundle.TrimEnd(Path.DirectorySeparatorChar));
                if (bundle is null) return "nexacl:// 协议需要使用 NexaCL.app 应用包。";
                return await RunAsync("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
                    ["-f", bundle], cancellationToken).ConfigureAwait(false);
            }
            return "当前平台不支持 nexacl:// 协议注册。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { return "nexacl:// 协议注册失败：" + error.Message; }
    }

    private static async Task<string?> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("无法启动系统协议注册工具。");
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        return process.ExitCode == 0 ? null : "nexacl:// 协议注册工具返回错误：" + process.ExitCode;
    }
}
