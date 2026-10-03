namespace Nexa.Services.Settings;

/// <summary>Default for newly installed instances; existing metadata is authoritative.</summary>
public sealed record SettingsInstanceIsolationPolicy(string Mode)
{
    public static bool IsValidMode(string? mode) => mode is "all" or "none" or "loaders" or "non-release" or "loaders-or-non-release";

    public static SettingsInstanceIsolationPolicy FromSnapshot(SettingsEffectiveSnapshot? snapshot)
    {
        var setting = snapshot?.Values.FirstOrDefault(value => value.Key == "game.default-isolation");
        string mode = setting?.Value.Value ?? "all";
        if (setting?.ValidationError is not null || !IsValidMode(mode)) throw new InvalidDataException("默认实例隔离设置无效。");
        return new(mode);
    }

    public bool Isolate(bool hasLoader, string? releaseType)
    {
        return Mode switch
        {
            "all" => true,
            "none" => false,
            "loaders" => hasLoader,
            "loaders-or-non-release" when hasLoader => true,
            "non-release" or "loaders-or-non-release" => releaseType switch
            {
                "release" => false,
                "snapshot" or "old_alpha" or "old_beta" => true,
                _ => throw new InvalidDataException("版本清单没有可信的发布类型，无法应用非正式版隔离策略。")
            },
            _ => throw new InvalidDataException("默认实例隔离设置无效。")
        };
    }
}
