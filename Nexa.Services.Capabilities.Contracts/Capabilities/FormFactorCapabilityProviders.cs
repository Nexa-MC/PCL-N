

namespace Nexa.Services.Capabilities;

public sealed record InputDeviceFeature(string DeviceName, bool Available);

public enum InputUsageKind { Unknown, Keyboard, Mouse, Touch, Controller }

public sealed record InputUsageSnapshot(InputUsageKind Primary, bool Keyboard, bool Mouse, bool Touch, bool Controller);

/// <summary>Registry definitions for formfactor.* (§15) and input.* (§16).</summary>
public static class FormFactorCatalog
{
    public const string ProviderId = "nexa.formfactor";

    public static readonly CapabilityDefinition<string> FormFactorType = new(
        "formfactor.type", "设备形态", "形态", ProviderId);
    public static readonly CapabilityDefinition<bool> FormFactorPortable = new(
        "formfactor.portable", "便携设备", "形态", ProviderId);
    public static readonly CapabilityDefinition<bool> FormFactorBatteryPowered = new(
        "formfactor.battery_powered", "电池供电", "形态", ProviderId, CapabilityKind.Derived,
        CapabilityStability.Dynamic, ["power.battery.present"]);
    public static readonly CapabilityDefinition<bool> FormFactorHandheld = new(
        "formfactor.handheld", "掌机形态", "形态", ProviderId);

    public static readonly string[] Scope =
    [
        FormFactorType.Id, FormFactorPortable.Id, FormFactorBatteryPowered.Id, FormFactorHandheld.Id,
    ];

    public static readonly Dictionary<string, ICapabilityDefinition> Definitions = new(StringComparer.Ordinal)
    {
        [FormFactorType.Id] = FormFactorType,
        [FormFactorPortable.Id] = FormFactorPortable,
        [FormFactorBatteryPowered.Id] = FormFactorBatteryPowered,
        [FormFactorHandheld.Id] = FormFactorHandheld,
    };
}

/// <summary>input.* availability facts (§16). Presence only — recent-usage tracking needs
/// global hooks and stays unplugged by name.</summary>
public static class InputCatalog
{
    public const string ProviderId = "nexa.input";

    public static readonly CapabilityDefinition<bool> InputKeyboardAvailable = new(
        "input.keyboard.available", "键盘可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputMouseAvailable = new(
        "input.mouse.available", "鼠标可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputTouchAvailable = new(
        "input.touch.available", "触摸屏可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputPenAvailable = new(
        "input.pen.available", "触控笔可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<int> InputControllerCount = new(
        "input.controller.count", "已连接手柄数", "输入", ProviderId, CapabilityKind.Metric);
    public static readonly CapabilityDefinition<bool> InputControllerAvailable = new(
        "input.controller.available", "手柄可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputGyroscopeAvailable = new(
        "input.gyroscope.available", "陀螺仪可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<bool> InputHapticsAvailable = new(
        "input.haptics.available", "振动反馈可用", "输入", ProviderId);
    public static readonly CapabilityDefinition<IReadOnlyList<InputDeviceFeature>> InputGyroscopeDevices = new(
        "input.gyroscope.devices", "陀螺仪", "输入", ProviderId);
    public static readonly CapabilityDefinition<IReadOnlyList<InputDeviceFeature>> InputHapticsDevices = new(
        "input.haptics.devices", "振动反馈", "输入", ProviderId);
    public static readonly CapabilityDefinition<string> InputUsagePrimary = new(
        "input.usage.primary", "主要输入方式", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentKeyboard = new(
        "input.usage.recent.keyboard", "最近使用键盘", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentMouse = new(
        "input.usage.recent.mouse", "最近使用鼠标", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentTouch = new(
        "input.usage.recent.touch", "最近使用触摸", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InputUsageRecentController = new(
        "input.usage.recent.controller", "最近使用手柄", "输入", ProviderId, CapabilityKind.Metric,
        CapabilityStability.Dynamic);

    public static IReadOnlyList<ICapabilityDefinition> Definitions() => (ICapabilityDefinition[])
    [
        InputKeyboardAvailable, InputMouseAvailable, InputTouchAvailable, InputPenAvailable,
        InputControllerCount, InputControllerAvailable, InputGyroscopeAvailable, InputHapticsAvailable,
        InputGyroscopeDevices, InputHapticsDevices,
        InputUsagePrimary, InputUsageRecentKeyboard, InputUsageRecentMouse, InputUsageRecentTouch,
        InputUsageRecentController,
    ];
}

/// <summary>
/// Session-local input evidence. Platform hosts report input after their own hit testing; the
/// capability provider only reads this bounded, lock-protected history and never installs a
/// global hook.
/// </summary>
public sealed class InputUsageTracker(TimeProvider? clock = null, TimeSpan? recentWindow = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _recentWindow = recentWindow ?? TimeSpan.FromMinutes(5);
    private readonly DateTimeOffset?[] _lastSeen = new DateTimeOffset?[5];
    private InputUsageKind _primary;

    public void Record(InputUsageKind kind)
    {
        if (kind == InputUsageKind.Unknown) return;
        lock (_gate)
        {
            _primary = kind;
            _lastSeen[(int)kind] = _clock.GetUtcNow();
        }
    }

    public InputUsageSnapshot Read()
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            bool Recent(InputUsageKind kind) => _lastSeen[(int)kind] is { } seen && now - seen <= _recentWindow;
            return new(_primary, Recent(InputUsageKind.Keyboard), Recent(InputUsageKind.Mouse),
                Recent(InputUsageKind.Touch), Recent(InputUsageKind.Controller));
        }
    }
}
