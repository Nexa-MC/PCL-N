using Nexa.Services.Minecraft.Management;

namespace Nexa.Desktop.Ui;

/// <summary>Explains observed changes without claiming that correlation proves a crash cause.</summary>
internal static class RecoveryExplanation
{
    internal static string Summary(IReadOnlyList<InstanceRecoveryChange> changes)
    {
        if (changes.Count == 0) return "恢复范围内没有更改。";
        string[] groups = changes.GroupBy(change => (change.Category, change.Kind))
            .OrderBy(group => group.Key.Category, StringComparer.Ordinal).ThenBy(group => group.Key.Kind)
            .Take(12).Select(group => $"{Kind(group.Key.Kind)} {group.Count()} 项{Display(group.Key.Category)}").ToArray();
        string summary = "Minecraft 上一次成功运行以后：\n" + string.Join("\n", groups);
        var clues = changes.Where(change => change.Kind is InstanceRecoveryChangeKind.Removed or InstanceRecoveryChangeKind.Disabled
            && change.Category is "模组" or "版本依赖").Take(3).ToArray();
        if (clues.Length > 0)
            summary += "\n\n建议先检查：\n" + string.Join("\n", clues.Select(change => $"{Kind(change.Kind)} · {Display(change.Path)}"))
                + "\n这些是变化线索，尚未确定为崩溃原因。";
        else if (changes.Any(change => change.SettingKey?.Contains("java", StringComparison.OrdinalIgnoreCase) == true))
            summary += "\n\nJava 选择发生了变化，可检查它是否符合此实例的要求。尚未确定为崩溃原因。";
        return summary;
    }

    internal static string Kind(InstanceRecoveryChangeKind kind) => kind switch
    {
        InstanceRecoveryChangeKind.Added => "新增",
        InstanceRecoveryChangeKind.Removed => "删除",
        InstanceRecoveryChangeKind.Enabled => "启用",
        InstanceRecoveryChangeKind.Disabled => "停用",
        _ => "修改"
    };

    internal static string Display(string value) => new(value.Take(300).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
