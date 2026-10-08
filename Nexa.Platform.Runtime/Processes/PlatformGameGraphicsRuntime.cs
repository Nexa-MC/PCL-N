using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace Nexa.Platform;

/// <summary>Documented Mesa environment requests admitted by current local evidence.</summary>
public sealed class PlatformGameGraphicsRuntime : IPlatformGameGraphicsRuntime
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private static readonly string[] SoftwareDri =
    ["/usr/lib/x86_64-linux-gnu/dri/swrast_dri.so", "/usr/lib/aarch64-linux-gnu/dri/swrast_dri.so", "/usr/lib/dri/swrast_dri.so", "/usr/lib64/dri/swrast_dri.so"];
    private static readonly string[] MesaGlx =
    ["/usr/lib/x86_64-linux-gnu/libGLX_mesa.so.0", "/usr/lib/aarch64-linux-gnu/libGLX_mesa.so.0", "/usr/lib/libGLX_mesa.so.0", "/usr/lib64/libGLX_mesa.so.0"];
    private readonly string _drmRoot;
    private readonly IReadOnlyList<string> _softwareDri, _mesaGlx;
    public PlatformGameGraphicsRuntime() : this("/sys/class/drm", SoftwareDri, MesaGlx) { }
    internal PlatformGameGraphicsRuntime(string drmRoot, IReadOnlyList<string> softwareDri, IReadOnlyList<string> mesaGlx)
    { _drmRoot = drmRoot; _softwareDri = softwareDri; _mesaGlx = mesaGlx; }

    public PlatformGameGraphicsSnapshot Describe()
    {
        if (!OperatingSystem.IsLinux()) return new(false, false, null, "platform.unsupported",
            "当前平台的私有 JVM Host 不提供按实例指定 GPU 或 Mesa renderer。");
        try
        {
            List<(string Pci, bool Primary, string Driver)> devices = [];
            if (Directory.Exists(_drmRoot))
                foreach (string card in Directory.EnumerateDirectories(_drmRoot, "card*", SearchOption.TopDirectoryOnly).Take(32))
                {
                    string name = Path.GetFileName(card);
                    if (name.Length is < 5 or > 8 || name.AsSpan(4).ContainsAnyExceptInRange('0', '9')) continue;
                    string? device = new DirectoryInfo(Path.Combine(card, "device")).ResolveLinkTarget(true)?.Name;
                    if (!ValidPci(device)) continue;
                    string? driver = new DirectoryInfo(Path.Combine(card, "device", "driver", "module")).ResolveLinkTarget(true)?.Name;
                    if (driver is not ("i915" or "xe" or "amdgpu" or "radeon" or "nouveau")) continue;
                    string primaryPath = Path.Combine(card, "device", "boot_vga");
                    string primary = File.Exists(primaryPath) ? ReadBounded(primaryPath) : "";
                    if (primary is not ("0" or "1")) continue;
                    if (!devices.Any(item => item.Pci == device)) devices.Add((device!, primary == "1", driver));
                }
            var primaryDevices = devices.Where(item => item.Primary).ToArray();
            var secondary = primaryDevices.Length == 1
                ? devices.Where(item => !item.Primary).OrderBy(item => item.Pci, StringComparer.Ordinal).FirstOrDefault() : default;
            bool software = _softwareDri.Take(8).Any(IsNativeElf) && _mesaGlx.Take(8).Any(IsNativeElf);
            return new(secondary.Pci is not null, software, secondary.Pci, "linux.sysfs.drm+mesa.local-libraries",
                secondary.Pci is null ? "尚无唯一主 GPU 和可用的第二个 PCI Mesa GPU；软件 renderer 仍按本机库检测。" : "已捕获实际 PCI Mesa GPU；环境请求不证明 Minecraft 的实际渲染设备。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(false, false, null, "linux.provider.unavailable", "无法读取本机 DRM / Mesa 依赖，显式 graphics 请求不可用。"); }
    }

    public PlatformGameGraphicsResult Prepare(string gpuPreference, string rendererPreference)
    {
        if (gpuPreference is not ("auto" or "secondary") || rendererPreference is not ("auto" or "mesa-software"))
            return new(false, "graphics_invalid_selection", "不支持的 GPU / renderer 选项。", Empty);
        if (gpuPreference == "auto" && rendererPreference == "auto") return new(true, "graphics_auto", "", Empty);
        if (!OperatingSystem.IsLinux()) return new(false, "platform_unsupported", Describe().Reason, Empty);
        var snapshot = Describe();
        if (gpuPreference == "secondary" && !snapshot.SecondaryAvailable
            || rendererPreference == "mesa-software" && !snapshot.MesaSoftwareAvailable)
            return new(false, "graphics_dependency_unavailable", snapshot.Reason, Empty);
        Dictionary<string, string> environment = [];
        if (gpuPreference == "secondary") environment.Add("DRI_PRIME", "pci-" + snapshot.SecondaryPciDevice!.Replace(':', '_').Replace('.', '_'));
        if (rendererPreference == "mesa-software") environment.Add("LIBGL_ALWAYS_SOFTWARE", "1");
        return new(true, "graphics_environment_prepared", "", new ReadOnlyDictionary<string, string>(environment));
    }

    private static bool ValidPci(string? value) => value is { Length: 12 }
        && value[4] == ':' && value[7] == ':' && value[10] == '.' && value[8] is '0' or '1' && value[11] is >= '0' and <= '7'
        && value.Where((_, index) => index is not (4 or 7 or 10)).All(char.IsAsciiHexDigit);
    private static string ReadBounded(string path)
    {
        using var reader = File.OpenText(path);
        char[] value = new char[8];
        int count = reader.Read(value, 0, value.Length);
        return count == value.Length ? "" : new string(value, 0, count).Trim();
    }
    private static bool IsNativeElf(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[64];
            file.ReadExactly(header);
            if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F'
                || header[5] != 1 || header[6] != 1 || header[4] != (Environment.Is64BitProcess ? 2 : 1)) return false;
            int machine = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
            return RuntimeInformation.ProcessArchitecture switch
            { Architecture.X64 => machine == 62, Architecture.X86 => machine == 3, Architecture.Arm64 => machine == 183, Architecture.Arm => machine == 40, _ => false };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
