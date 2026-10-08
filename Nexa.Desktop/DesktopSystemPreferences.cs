using System.Diagnostics;
using Microsoft.Win32;

namespace Nexa.Desktop;

/// <summary>Current-user registrations; every removable artifact has a NexaCL-owned identity.</summary>
internal static class DesktopSystemPreferences
{
    internal static string DesktopEntry(IReadOnlyList<string> command, bool fileArguments)
    {
        string value = DesktopProtocolRegistration.LinuxDesktopEntry(command);
        return fileArguments ? value.Replace(" %u\n", " %f\n", StringComparison.Ordinal)
            .Replace("MimeType=x-scheme-handler/nexacl;", "MimeType=application/x-nexacl-mrpack;application/x-nexacl-pack;", StringComparison.Ordinal)
            : value.Replace(" %u\n", "\n", StringComparison.Ordinal)
                .Replace("MimeType=x-scheme-handler/nexacl;\n", "X-GNOME-Autostart-enabled=true\n", StringComparison.Ordinal);
    }

    internal static string UserDirectory(string variable, params string[] fallback)
    {
        string? configured = Environment.GetEnvironmentVariable(variable);
        return configured is { Length: > 0 } && Path.IsPathFullyQualified(configured) ? configured
            : Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), .. fallback]);
    }

    internal static async Task ApplyAutostartAsync(bool enabled, CancellationToken token)
    {
        IReadOnlyList<string> command = DesktopProtocolRegistration.LauncherCommand();
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue("NexaCL", DesktopProtocolRegistration.WindowsCommand(command).Replace(" \"%1\"", "", StringComparison.Ordinal));
            else key.DeleteValue("NexaCL", false);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            string path = Path.Combine(UserDirectory("XDG_CONFIG_HOME", ".config"), "autostart", "nexacl.desktop");
            await WriteOwnedAsync(path, enabled ? DesktopEntry(command, false) : null, token).ConfigureAwait(false);
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "org.nexacl.launcher.plist");
            if (File.Exists(path)) await RunAsync("/bin/launchctl", ["unload", path], token, requireSuccess: false).ConfigureAwait(false);
            string Escape(string text) => System.Security.SecurityElement.Escape(text)!;
            string? contents = enabled ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\"><plist version=\"1.0\"><dict><key>Label</key><string>org.nexacl.launcher</string><key>ProgramArguments</key><array>"
                + string.Concat(command.Select(argument => "<string>" + Escape(argument) + "</string>"))
                + "</array><key>RunAtLoad</key><true/></dict></plist>" : null;
            await WriteOwnedAsync(path, contents, token).ConfigureAwait(false);
            if (enabled) await RunAsync("/bin/launchctl", ["load", path], token).ConfigureAwait(false);
            return;
        }
        throw new PlatformNotSupportedException("当前平台不支持开机启动。");
    }

    internal static async Task ApplyFileAssociationsAsync(bool enabled, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var (extension, name) in new[] { (".mrpack", "NexaCL.ModrinthPack"), (".nexapack", "NexaCL.Pack") })
            {
                string root = @"Software\Classes\";
                if (enabled)
                {
                    using RegistryKey type = Registry.CurrentUser.CreateSubKey(root + name);
                    type.SetValue("", "NexaCL Pack");
                    using RegistryKey open = type.CreateSubKey(@"shell\open\command");
                    open.SetValue("", DesktopProtocolRegistration.WindowsCommand(DesktopProtocolRegistration.LauncherCommand()));
                    using RegistryKey choices = Registry.CurrentUser.CreateSubKey(root + extension + @"\OpenWithProgids");
                    choices.SetValue(name, Array.Empty<byte>(), RegistryValueKind.None);
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(root + name, false);
                    using RegistryKey? choices = Registry.CurrentUser.OpenSubKey(root + extension + @"\OpenWithProgids", true);
                    choices?.DeleteValue(name, false);
                }
            }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            string data = UserDirectory("XDG_DATA_HOME", ".local", "share");
            await WriteOwnedAsync(Path.Combine(data, "applications", "nexacl-pack.desktop"), enabled ? DesktopEntry(DesktopProtocolRegistration.LauncherCommand(), true) : null, token).ConfigureAwait(false);
            string mime = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\"><mime-type type=\"application/x-nexacl-mrpack\"><comment>Modrinth pack</comment><glob pattern=\"*.mrpack\"/></mime-type><mime-type type=\"application/x-nexacl-pack\"><comment>NexaCL pack</comment><glob pattern=\"*.nexapack\"/></mime-type></mime-info>";
            string mimeRoot = Path.Combine(data, "mime");
            await WriteOwnedAsync(Path.Combine(mimeRoot, "packages", "nexacl-pack.xml"), enabled ? mime : null, token).ConfigureAwait(false);
            if (Directory.Exists(mimeRoot)) await RunAsync("update-mime-database", [mimeRoot], token).ConfigureAwait(false);
            // Register Open With capability; preserve the user's existing default application.
            await RunAsync("update-desktop-database", [Path.Combine(data, "applications")], token, requireSuccess: false).ConfigureAwait(false);
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            // Bundle declarations are immutable, while Launch Services registration is user-local.
            string? error = await DesktopProtocolRegistration.RegisterAsync(token).ConfigureAwait(false);
            if (enabled && error is not null) throw new IOException(error);
            return;
        }
        throw new PlatformNotSupportedException("当前平台不支持文件关联。");
    }

    private static async Task WriteOwnedAsync(string path, string? contents, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contents is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, contents, token).ConfigureAwait(false); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }

    internal static async Task<bool> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token, bool requireSuccess = true)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new IOException("无法启动系统集成工具。");
        try { await process.WaitForExitAsync(budget.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
        if (requireSuccess && process.ExitCode != 0) throw new IOException("系统集成工具失败：" + process.ExitCode);
        return process.ExitCode == 0;
    }
}
