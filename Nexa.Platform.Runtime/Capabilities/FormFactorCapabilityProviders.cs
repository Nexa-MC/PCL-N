using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>
/// Form-factor heuristic (§15) over observed battery, primary-panel and input facts.
/// Unknown input facts stay unknown instead of becoming a fabricated desktop classification.
/// </summary>
public sealed class FormFactorCapabilityProvider(Func<bool>? batteryPresent = null, Func<bool>? internalDisplay = null,
    Func<bool>? touchAvailable = null, Func<bool>? keyboardAvailable = null, Func<bool>? controllerAvailable = null)
    : IMachineCapabilityProvider
{
    private readonly Func<bool>? _batteryPresent = batteryPresent;
    private readonly Func<bool>? _internalDisplay = internalDisplay;
    private readonly Func<bool>? _touchAvailable = touchAvailable;
    private readonly Func<bool>? _keyboardAvailable = keyboardAvailable;
    private readonly Func<bool>? _controllerAvailable = controllerAvailable;

    public string Id => FormFactorCatalog.ProviderId;

    public async ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IReadOnlyList<ICapability>> power = _batteryPresent is null
            ? CollectSafelyAsync(new HardwarePowerCapabilityProvider(), timestamp, cancellationToken)
            : Task.FromResult<IReadOnlyList<ICapability>>([]);
        Task<IReadOnlyList<ICapability>> display = _internalDisplay is null
            ? CollectSafelyAsync(new DisplayCapabilityProvider(), timestamp, cancellationToken)
            : Task.FromResult<IReadOnlyList<ICapability>>([]);
        Task<IReadOnlyList<ICapability>> input = _touchAvailable is null || _keyboardAvailable is null || _controllerAvailable is null
            ? CollectSafelyAsync(new InputCapabilityProvider(), timestamp, cancellationToken)
            : Task.FromResult<IReadOnlyList<ICapability>>([]);
        await Task.WhenAll(power, display, input).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Project(timestamp,
            _batteryPresent is not null ? InvokeSafely(_batteryPresent) : ReadBoolean(await power.ConfigureAwait(false), MachineEnvironmentCatalog.PowerBatteryPresent.Id),
            _internalDisplay is not null ? InvokeSafely(_internalDisplay) : ReadBoolean(await display.ConfigureAwait(false), MachineEnvironmentCatalog.DisplayPrimaryInternal.Id),
            _touchAvailable is not null ? InvokeSafely(_touchAvailable) : ReadBoolean(await input.ConfigureAwait(false), InputCatalog.InputTouchAvailable.Id),
            _keyboardAvailable is not null ? InvokeSafely(_keyboardAvailable) : ReadBoolean(await input.ConfigureAwait(false), InputCatalog.InputKeyboardAvailable.Id),
            _controllerAvailable is not null ? InvokeSafely(_controllerAvailable) : ReadBoolean(await input.ConfigureAwait(false), InputCatalog.InputControllerAvailable.Id));
    }

    internal static IReadOnlyList<ICapability> Project(DateTimeOffset timestamp, bool? battery, bool? internalPanel,
        bool? touch, bool? keyboard, bool? controller)
    {
        const string source = "形态启发式（电池 + 内建屏 + 触摸）";
        bool? laptop = And(battery, internalPanel);
        bool? handheld = And(battery, internalPanel, touch, keyboard.HasValue ? !keyboard.Value : null, controller);
        string? type = handheld == true ? "Handheld" : laptop == false ? "Desktop"
            : laptop == true && handheld == false ? "Laptop" : null;
        return Array.AsReadOnly(new ICapability[]
        {
            type is not null ? FormFactorCatalog.FormFactorType.Observe(type, timestamp, source)
                : FormFactorCatalog.FormFactorType.Unavailable(CapabilityAvailability.Unknown, timestamp, "缺少确定设备形态所需的电池、主屏或输入事实"),
            BooleanFact(FormFactorCatalog.FormFactorPortable, battery),
            BooleanFact(FormFactorCatalog.FormFactorBatteryPowered, battery),
            BooleanFact(FormFactorCatalog.FormFactorHandheld, handheld),
        });

        ICapability BooleanFact(CapabilityDefinition<bool> definition, bool? value) => value.HasValue
            ? definition.Observe(value.Value, timestamp, source)
            : definition.Unavailable(CapabilityAvailability.Unknown, timestamp, "设备形态所需的原生检测事实不可读");
    }

    private static bool? And(params bool?[] values) => values.Contains(false) ? false : values.Any(value => !value.HasValue) ? null : true;

    private static bool? ReadBoolean(IReadOnlyList<ICapability> facts, string id) =>
        facts.FirstOrDefault(item => item.Id == id) is Capability<bool> { Availability: CapabilityAvailability.Available } fact ? fact.Value : null;

    private static bool? InvokeSafely(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException) { return null; }
    }

    private static async Task<IReadOnlyList<ICapability>> CollectSafelyAsync(IMachineCapabilityProvider provider,
        DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        try { return await provider.CollectAsync(timestamp, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not OutOfMemoryException and not AccessViolationException)
        { return []; }
    }
}

/// <summary>input.* availability: presence probes only, per platform.</summary>
public sealed class InputCapabilityProvider(InputUsageTracker? usage = null) : IMachineCapabilityProvider
{
    private readonly InputUsageTracker _usage = usage ?? new InputUsageTracker();
    public string Id => InputCatalog.ProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(CollectWindows(timestamp), timestamp));
        }

        if (OperatingSystem.IsLinux())
        {
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(CollectLinux(timestamp), timestamp));
        }

        if (OperatingSystem.IsMacOS())
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(MacInputProbe.Collect(timestamp), timestamp));
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(AppendUsage(Unavailable(timestamp, "此操作系统未提供输入设备检测通道"), timestamp));
    }

    private System.Collections.ObjectModel.ReadOnlyCollection<ICapability> AppendUsage(IEnumerable<ICapability> facts, DateTimeOffset timestamp)
    {
        InputUsageSnapshot usage = _usage.Read();
        const string source = "Nexa 会话输入事件";
        return Array.AsReadOnly(facts.Concat(new ICapability[]
        {
            InputCatalog.InputUsagePrimary.Observe(usage.Primary.ToString(), timestamp, source,
                usage.Primary == InputUsageKind.Unknown ? CapabilityConfidence.Low : CapabilityConfidence.High),
            InputCatalog.InputUsageRecentKeyboard.Observe(usage.Keyboard, timestamp, source),
            InputCatalog.InputUsageRecentMouse.Observe(usage.Mouse, timestamp, source),
            InputCatalog.InputUsageRecentTouch.Observe(usage.Touch, timestamp, source),
            InputCatalog.InputUsageRecentController.Observe(usage.Controller, timestamp, source),
        }).ToArray());
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> CollectWindows(DateTimeOffset timestamp)
    {
        const string source = "Windows GetSystemMetrics / XInput";
        const int SmDigitizer = 0x2004;
        const int SmMousePresent = 0x13;
        int digitizer = GetSystemMetrics(SmDigitizer);
        bool? keyboard = WindowsInputProbe.KeyboardPresent();
        List<(string Name, bool Haptics)> controllers = ReadXInputControllers();
        IReadOnlyList<InputDeviceFeature> haptics = Array.AsReadOnly(controllers
            .Select(static controller => new InputDeviceFeature(controller.Name, controller.Haptics)).ToArray());
        return Array.AsReadOnly(new ICapability[]
        {
            keyboard is { } present
                ? InputCatalog.InputKeyboardAvailable.Observe(present, timestamp, "Windows Raw Input 设备列表")
                : InputCatalog.InputKeyboardAvailable.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, "无法读取 Windows 键盘设备列表"),
            InputCatalog.InputMouseAvailable.Observe(GetSystemMetrics(SmMousePresent) != 0, timestamp, source),
            InputCatalog.InputTouchAvailable.Observe(WindowsInputProbe.HasTouch(digitizer), timestamp, source),
            InputCatalog.InputPenAvailable.Observe(WindowsInputProbe.HasPen(digitizer), timestamp, source),
            InputCatalog.InputControllerCount.Observe(controllers.Count, timestamp, source),
            InputCatalog.InputControllerAvailable.Observe(controllers.Count > 0, timestamp, source),
            InputCatalog.InputGyroscopeAvailable.Unavailable(CapabilityAvailability.Unknown, timestamp, "XInput 不公开陀螺仪通道"),
            InputCatalog.InputHapticsAvailable.Observe(controllers.Any(static controller => controller.Haptics), timestamp, source),
            InputCatalog.InputGyroscopeDevices.Unavailable(CapabilityAvailability.Unknown, timestamp, "XInput 不公开陀螺仪通道"),
            InputCatalog.InputHapticsDevices.Observe(haptics, timestamp, source),
        });
    }

    private static IReadOnlyList<ICapability> CollectLinux(DateTimeOffset timestamp) =>
        LinuxInputProbe.Project(LinuxInputProbe.Read(), timestamp);

    private static System.Collections.ObjectModel.ReadOnlyCollection<ICapability> Unavailable(DateTimeOffset timestamp, string reason) =>
        Array.AsReadOnly(InputCatalog.Definitions().Where(static definition => !definition.Id.StartsWith("input.usage.", StringComparison.Ordinal))
            .Select(definition => definition.Unavailable(CapabilityAvailability.PlatformUnsupported, timestamp, reason)).ToArray());

    private static List<(string Name, bool Haptics)> ReadXInputControllers()
    {
        List<(string Name, bool Haptics)> devices = [];
        for (uint user = 0; user < 4; user++)
        {
            if (XInputGetCapabilities(user, 0, out XInputCapabilities capabilities) != 0)
            {
                continue;
            }

            string kind = capabilities.SubType switch
            {
                0x02 => "方向盘",
                0x03 => "街机摇杆",
                0x04 => "飞行摇杆",
                0x05 => "舞蹈垫",
                0x06 => "吉他控制器",
                0x08 => "鼓控制器",
                _ => "游戏手柄",
            };
            bool haptics = (capabilities.Flags & 0x0001) != 0
                || capabilities.Vibration.LeftMotorSpeed != 0
                || capabilities.Vibration.RightMotorSpeed != 0;
            devices.Add(($"XInput {kind} {user + 1}", haptics));
        }

        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputCapabilities
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [DllImport("xinput1_4.dll", SetLastError = false)]
    private static extern int XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilities capabilities);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int GetSystemMetrics(int index);
}
