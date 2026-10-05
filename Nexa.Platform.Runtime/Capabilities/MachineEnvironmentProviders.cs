using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>Observes display geometry through the platform's own APIs; nothing is estimated.</summary>
public static class DisplayCapabilityProbe
{
    /// <summary>True when the primary monitor is a built-in panel (laptop/handheld); the
    /// display provider does the actual DisplayConfig work.</summary>
    public static bool IsPrimaryInternalProbe() => OperatingSystem.IsWindows() && DisplayCapabilityProvider.ProbePrimaryInternal() == true;
}

public sealed partial class DisplayCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineEnvironmentCatalog.DisplayProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            return CollectWindows(timestamp);
        }
        if (OperatingSystem.IsMacOS())
        {
            return CollectMacOs(timestamp);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxDisplayProbe.CollectAsync(timestamp, cancellationToken);
        }

        return Unavailable(timestamp, "此操作系统未提供显示检测通道", CapabilityAvailability.PlatformUnsupported);
    }

    private ValueTask<IReadOnlyList<ICapability>> CollectWindows(DateTimeOffset timestamp)
    {
        const uint monitorDefaultToNearest = 2;
        List<Rect> monitors = [];
        bool Callback(nint monitor, nint hdc, ref Rect rect, nint data)
        {
            monitors.Add(rect);
            return true;
        }

        bool enumerated = EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero);
        if (!enumerated || monitors.Count == 0)
        {
            return Unavailable(timestamp, "当前图形会话没有可枚举的显示器");
        }

        nint primaryHandle = MonitorFromPoint(new Point { X = 0, Y = 0 }, monitorDefaultToNearest);
        MONITORINFOEX info = new() { Size = Marshal.SizeOf<MONITORINFOEX>() };
        bool hasInfo = GetMonitorInfo(primaryHandle, ref info);
        int width = hasInfo ? info.Monitor.Right - info.Monitor.Left : 0;
        int height = hasInfo ? info.Monitor.Bottom - info.Monitor.Top : 0;
        bool? internalPanel = hasInfo ? IsPrimaryInternal(info.DeviceName) : null;
        nint dc = GetDC(nint.Zero);
        int logicalHz = 0;
        if (dc != nint.Zero)
        {
            logicalHz = GetDeviceCaps(dc, 116); // VREFRESH
            _ = ReleaseDC(nint.Zero, dc);
        }

        double refresh = logicalHz > 1 ? logicalHz : 0;
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
            MachineEnvironmentCatalog.DisplayCount.Observe(monitors.Count, timestamp, "Windows EnumDisplayMonitors"),
            internalPanel.HasValue ? MachineEnvironmentCatalog.DisplayPrimaryInternal.Observe(internalPanel.Value, timestamp, "Windows DisplayConfigGetDeviceInfo")
                : MachineEnvironmentCatalog.DisplayPrimaryInternal.Unavailable(CapabilityAvailability.Unknown, timestamp, "主屏的输出连接类型不可读"),
            width > 0 && height > 0 ? MachineEnvironmentCatalog.DisplayPrimaryResolution.Observe($"{width}×{height}", timestamp, "Windows GetMonitorInfo")
                : MachineEnvironmentCatalog.DisplayPrimaryResolution.Unavailable(CapabilityAvailability.Unknown, timestamp, "主显示器分辨率不可读"),
            refresh > 0
                ? MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Observe(refresh, timestamp, "Windows GetDeviceCaps(VREFRESH)")
                : MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(CapabilityAvailability.Unknown, timestamp, "刷新率不可读"),
        ]));
    }

    private static ValueTask<IReadOnlyList<ICapability>> CollectMacOs(DateTimeOffset timestamp)
    {
        uint mainId = CGMainDisplayID();
        if (mainId == 0)
        {
            return Unavailable(timestamp, "当前图形会话没有主显示器");
        }

        // CGGetActiveDisplayList counts the active displays; CGDisplayIsBuiltin is the
        // authoritative built-in-panel answer (Apple silicon laptops, MacBooks).
        uint displayCount = 0;
        if (CGGetActiveDisplayList(0, [], ref displayCount) != 0 || displayCount is 0 or > 64)
            return Unavailable(timestamp, "显示器枚举失败或超出检测上限");
        uint[] displays = new uint[displayCount];
        uint fetched = 0;
        int listResult = CGGetActiveDisplayList((uint)displays.Length, displays, ref fetched);

        nuint width = CGDisplayPixelsWide(mainId);
        nuint height = CGDisplayPixelsHigh(mainId);
        var mode = CGDisplayCopyDisplayMode(mainId);
        double refresh;
        try { refresh = mode != nint.Zero ? CGDisplayModeGetRefreshRate(mode) : 0; }
        finally { if (mode != 0) CFRelease(mode); }
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly<ICapability>([
            listResult == 0 && fetched > 0 && fetched <= displays.Length
                ? MachineEnvironmentCatalog.DisplayCount.Observe((int)fetched, timestamp, "CoreGraphics CGGetActiveDisplayList")
                : MachineEnvironmentCatalog.DisplayCount.Unavailable(CapabilityAvailability.Unknown, timestamp, "显示器枚举不可读"),
            CGDisplayIsBuiltin(mainId) != 0
                ? MachineEnvironmentCatalog.DisplayPrimaryInternal.Observe(true, timestamp, "CoreGraphics CGDisplayIsBuiltin")
                : MachineEnvironmentCatalog.DisplayPrimaryInternal.Observe(false, timestamp, "CoreGraphics CGDisplayIsBuiltin"),
            width is > 0 and <= 131072 && height is > 0 and <= 131072
                ? MachineEnvironmentCatalog.DisplayPrimaryResolution.Observe($"{width}×{height}", timestamp, "CoreGraphics CGMainDisplayID")
                : MachineEnvironmentCatalog.DisplayPrimaryResolution.Unavailable(CapabilityAvailability.Unknown, timestamp, "主显示器分辨率不可读"),
            double.IsFinite(refresh) && refresh > 0 && refresh <= 2000
                ? MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Observe(refresh, timestamp, "CoreGraphics CGDisplayCopyDisplayMode")
                : MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(CapabilityAvailability.Unknown, timestamp, "刷新率不可读"),
        ]));
    }
    private static ValueTask<IReadOnlyList<ICapability>> Unavailable(DateTimeOffset timestamp, string reason,
        CapabilityAvailability availability = CapabilityAvailability.TemporarilyUnavailable) =>
        ValueTask.FromResult<IReadOnlyList<ICapability>>(Array.AsReadOnly(
            MachineCapabilityCatalog.CreateRegistry().Definitions.Where(item => item.Provider == MachineEnvironmentCatalog.DisplayProviderId)
                .Select(item => item.Unavailable(availability, timestamp, reason)).ToArray()));

    /// <summary>
    /// Internal-panel detection through the display configuration API: the primary monitor's
    /// GDI device name maps to its path, and the target's outputTechnology INTERNAL flag is
    /// the authoritative built-in signal (laptop panels, handhelds).
    /// </summary>
    internal static bool? ProbePrimaryInternal()
    {
        const uint monitorDefaultToNearest = 2;
        nint handle = MonitorFromPoint(new Point { X = 0, Y = 0 }, monitorDefaultToNearest);
        MONITORINFOEX info = new() { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(handle, ref info) ? IsPrimaryInternal(info.DeviceName) : null;
    }

    private static bool? IsPrimaryInternal(string primaryGdiDeviceName)
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
        {
            return null;
        }
        if (pathCount is 0 or > 256 || modeCount > 1024) return null;

        DisplayConfigPathInfo[] paths = new DisplayConfigPathInfo[pathCount];
        DisplayConfigModeInfo[] modes = new DisplayConfigModeInfo[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, nint.Zero) != 0)
        {
            return null;
        }
        if (pathCount > paths.Length || modeCount > modes.Length) return null;

        foreach (DisplayConfigPathInfo path in paths.Take((int)pathCount))
        {
            DisplayConfigSourceDeviceName source = new()
            {
                Header = new DisplayConfigDeviceInfoHeader(DisplayConfigDeviceInfoType.GetSourceName,
                    (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(), path.SourceAdapterId, path.SourceId),
            };
            if (DisplayConfigGetDeviceInfo(ref source) != 0)
            {
                continue;
            }

            if (!string.Equals(source.ViewGdiDeviceName, primaryGdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DisplayConfigTargetDeviceName target = new()
            {
                Header = new DisplayConfigDeviceInfoHeader(DisplayConfigDeviceInfoType.GetTargetName,
                    (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(), path.TargetAdapterId, path.TargetId),
            };
            if (DisplayConfigGetDeviceInfo(ref target) != 0)
            {
                return null;
            }

            return ProjectOutputTechnology(target.OutputTechnology);
        }

        return null;
    }

    private const int DisplayConfigOutputTechnologyInternal = unchecked((int)0x80000000);

    internal static bool? ProjectOutputTechnology(int technology) => technology switch
    {
        DisplayConfigOutputTechnologyInternal or 6 or 11 or 13 => true, // INTERNAL, LVDS, embedded DisplayPort/UDI.
        0 or 1 or 2 or 3 or 4 or 5 or 8 or 9 or 10 or 12 or 14 or 15 or 16 or 17 or 18 => false,
        _ => null, // OTHER and future connector types carry no reliable built-in answer.
    };

    private enum DisplayConfigDeviceInfoType : uint
    {
        GetSourceName = 1,
        GetTargetName = 2,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DisplayConfigDeviceInfoHeader(DisplayConfigDeviceInfoType infoType, uint size, long adapterId, uint id)
    {
        public DisplayConfigDeviceInfoType Type = infoType;
        public uint Size = size;
        public long AdapterId = adapterId;
        public uint Id = id;
    }

    // LUID is DWORD + LONG with four-byte alignment, not an eight-aligned native integer.
    // wingdi.h: source(20) + target(48) + flags(4) = 72 bytes on Windows x86 and x64.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DisplayConfigPathInfo
    {
        // DISPLAYCONFIG_PATH_SOURCE_INFO
        public long SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoUnion;
        public uint SourceStatusFlags;
        // DISPLAYCONFIG_PATH_TARGET_INFO starts at byte 20.
        public long TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoUnion;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        // DISPLAYCONFIG_PATH_INFO
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public long AdapterId;
        // The trailing source/target-mode union is 48 bytes (sizeof(DISPLAYCONFIG_MODE_INFO)
        // == 64). Under-sizing it makes QueryDisplayConfig write past the marshalled array —
        // the native crash the isolated probe reproduced.
        public long Union0;
        public long Union1;
        public long Union2;
        public long Union3;
        public long Union4;
        public long Union5;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathArrayElementCount, out uint modeInfoArrayElementCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathArrayElementCount,
        [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint modeInfoArrayElementCount,
        [Out] DisplayConfigModeInfo[] modeInfoArray, nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    internal static (int Header, int Path, int Mode, int SourceName, int TargetName, int TargetAdapterOffset) DisplayConfigLayout() =>
        (Marshal.SizeOf<DisplayConfigDeviceInfoHeader>(), Marshal.SizeOf<DisplayConfigPathInfo>(), Marshal.SizeOf<DisplayConfigModeInfo>(),
            Marshal.SizeOf<DisplayConfigSourceDeviceName>(), Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
            (int)Marshal.OffsetOf<DisplayConfigPathInfo>(nameof(DisplayConfigPathInfo.TargetAdapterId)));

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int ReleaseDC(nint window, nint dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(nint dc, int index);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayPixelsHigh(uint display);

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern double CGDisplayModeGetRefreshRate(nint mode);

    [DllImport(CoreGraphics)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] activeDisplays, ref uint displayCount);

    [DllImport(CoreGraphics)]
    private static extern uint CGDisplayIsBuiltin(uint display);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(nint value);
}
