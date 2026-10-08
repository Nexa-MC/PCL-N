namespace Nexa.Desktop;

/// <summary>One safe product destination, shared by CLI and the command palette.</summary>
internal sealed record DesktopCommandRoute(string Id, string Label, string? NavigationCommand, string? SettingsSection = null);

internal readonly record struct DesktopCommandLineRequest(bool IsSuccess, bool ListCommands, bool SafeMode,
    DesktopCommandRoute? Command, string? Error);

internal static class DesktopCommandLine
{
    private static readonly IReadOnlyList<DesktopCommandRoute> Catalog = Array.AsReadOnly<DesktopCommandRoute>([
        new("launch", "启动", "ui.navigation.launch"),
        new("install", "安装", "ui.navigation.download"),
        new("resources", "资源", "ui.navigation.community"),
        new("settings", "设置", "ui.navigation.settings"),
        new("tasks", "任务中心", "ui.tasks.open"),
        new("java", "Java 管理", "ui.navigation.settings", "java"),
        new("storage", "存储与迁移", "ui.navigation.settings", "storage"),
        new("about", "关于", "ui.navigation.settings", "about"),
    ]);

    internal static IReadOnlyList<DesktopCommandRoute> Commands => Catalog;

    internal static bool TryResolve(string id, out DesktopCommandRoute? command)
    {
        command = Catalog.FirstOrDefault(entry => entry.Id.Equals(id, StringComparison.Ordinal));
        return command is not null;
    }

    internal static DesktopCommandLineRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool listing = false, safe = false;
        DesktopCommandRoute? command = null;
        if (arguments.Count > 128) return Invalid("命令行参数过多。");
        foreach (string argument in arguments)
        {
            if (argument is null || argument.Length > 8192 || argument.Any(char.IsControl))
                return Invalid("命令行参数无效。");
            if (argument == "--list-commands")
            {
                if (listing) return Invalid("请只指定一次命令列表选项。");
                listing = true;
            }
            else if (argument == "--safe-mode")
            {
                if (safe) return Invalid("请只指定一次安全模式选项。");
                safe = true;
            }
            else if (argument.StartsWith("--command=", StringComparison.Ordinal))
            {
                if (command is not null) return Invalid("请只指定一个导航命令。");
                if (!TryResolve(argument[10..], out command)) return Invalid("未知导航命令；使用 --list-commands 查看可用命令。");
            }
            else if (argument == "--command" || argument.StartsWith("--list-commands=", StringComparison.Ordinal)
                || argument.StartsWith("--safe-mode=", StringComparison.Ordinal))
                return Invalid("命令行选项格式无效。");
        }
        if (listing && command is not null) return Invalid("命令列表与导航命令不能同时使用。");
        return new(true, listing, safe, command, null);
    }

    internal static string FormatListing(Func<string, string>? localize = null) =>
        string.Join(Environment.NewLine, Catalog.Select(entry => entry.Id + "\t" + (localize?.Invoke(entry.Label) ?? entry.Label)));

    private static DesktopCommandLineRequest Invalid(string message) => new(false, false, false, null, message);
}
