using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Nexa.Services.Capabilities;

internal sealed record LinuxDisplayOutput(string Name, bool Primary, int Width, int Height, double? RefreshHz);

internal static class LinuxDisplayProbe
{
    internal static ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        string? session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (session == "wayland" || string.IsNullOrEmpty(session) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(Unavailable(timestamp, CapabilityAvailability.PlatformUnsupported, "Wayland 没有通用的主屏查询接口；XWayland 输出不能代表完整桌面"));
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return ValueTask.FromResult<IReadOnlyList<ICapability>>(Unavailable(timestamp, CapabilityAvailability.DependencyMissing, "没有 X11 图形会话（DISPLAY）"));
        return QueryAsync(timestamp, cancellationToken);
    }

    internal static async ValueTask<IReadOnlyList<ICapability>> QueryAsync(DateTimeOffset timestamp, CancellationToken cancellationToken,
        string executable = "xrandr", string drmRoot = "/sys/class/drm")
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--query");
        process.StartInfo.ArgumentList.Add("--current");
        process.StartInfo.Environment["LC_ALL"] = "C";
        try
        {
            if (!process.Start()) return Unavailable(timestamp, CapabilityAvailability.TemporarilyUnavailable, "无法启动 XRandR 查询");
            Task<string> output = ReadBoundedAsync(process.StandardOutput, 65536, deadline);
            Task<string> error = ReadBoundedAsync(process.StandardError, 8192, deadline);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return Unavailable(timestamp, CapabilityAvailability.TemporarilyUnavailable, "XRandR 无法读取当前图形会话");
            return Project(Parse(await output.ConfigureAwait(false)), timestamp, drmRoot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(timestamp, CapabilityAvailability.TemporarilyUnavailable, "XRandR 查询超时（2 秒）");
        }
        catch (Win32Exception exception)
        {
            return Unavailable(timestamp, exception.NativeErrorCode switch
            {
                2 => CapabilityAvailability.DependencyMissing,
                13 => CapabilityAvailability.PermissionDenied,
                _ => CapabilityAvailability.TemporarilyUnavailable,
            }, exception.NativeErrorCode == 2 ? "系统未安装 xrandr" : "无法启动 XRandR 查询");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return Unavailable(timestamp, CapabilityAvailability.Unknown, "XRandR 查询结果无效或超出读取上限");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationTokenSource deadline)
    {
        StringBuilder text = new();
        char[] buffer = new char[2048];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - text.Length + 1)), deadline.Token).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            if (text.Length + count > maximum)
            {
                deadline.Cancel();
                throw new InvalidDataException("XRandR output exceeded its bound.");
            }
            text.Append(buffer, 0, count);
        }
    }

    internal static IReadOnlyList<LinuxDisplayOutput> Parse(string output)
    {
        if (output.Length > 65536) throw new InvalidDataException("XRandR output exceeded its bound.");
        List<LinuxDisplayOutput> outputs = [];
        int current = -1;
        foreach (string line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!char.IsWhiteSpace(line[0]))
            {
                current = -1;
                if (fields.Length < 2 || fields[1] is not ("connected" or "disconnected")) continue;
                if (fields[1] == "disconnected") continue;
                bool primary = fields.Contains("primary", StringComparer.Ordinal);
                string? geometry = fields.Skip(2).FirstOrDefault(field => TryGeometry(field, out _, out _));
                if (geometry is null) continue; // Connected but no active CRTC is not an active display.
                if (outputs.Count >= 64 || !TryGeometry(geometry, out int width, out int height))
                    throw new InvalidDataException("XRandR active output limit exceeded.");
                current = outputs.Count;
                outputs.Add(new(fields[0], primary, width, height, null));
                continue;
            }
            if (current < 0) continue;
            string[] rates = fields.Skip(1).Where(field => field.Contains('*', StringComparison.Ordinal)).ToArray();
            if (rates.Length == 0) continue;
            if (rates.Length != 1 || outputs[current].RefreshHz.HasValue
                || !double.TryParse(rates[0].TrimEnd('*', '+'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double rate)
                || !double.IsFinite(rate) || rate is <= 0 or > 2000)
                throw new InvalidDataException("XRandR current refresh rate is invalid.");
            outputs[current] = outputs[current] with { RefreshHz = rate };
        }
        if (outputs.Count == 0 || outputs.Count(item => item.Primary) > 1)
            throw new InvalidDataException("XRandR did not provide a valid active display list.");
        return outputs.AsReadOnly();
    }

    private static bool TryGeometry(string value, out int width, out int height)
    {
        width = height = 0;
        int separator = value.IndexOf('x');
        if (separator <= 0) return false;
        int offset = value.IndexOfAny(['+', '-'], separator + 1);
        if (offset <= separator + 1) return false;
        int secondOffset = value.IndexOfAny(['+', '-'], offset + 1);
        return secondOffset > offset + 1
            && int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(value.AsSpan(separator + 1, offset - separator - 1), NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && int.TryParse(value.AsSpan(offset, secondOffset - offset), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            && int.TryParse(value.AsSpan(secondOffset), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            && width is > 0 and <= 131072 && height is > 0 and <= 131072;
    }

    internal static IReadOnlyList<ICapability> Project(IReadOnlyList<LinuxDisplayOutput> outputs, DateTimeOffset timestamp,
        string drmRoot = "/sys/class/drm")
    {
        const string source = "Linux XRandR current active outputs";
        LinuxDisplayOutput? primary = outputs.SingleOrDefault(item => item.Primary) ?? (outputs.Count == 1 ? outputs[0] : null);
        bool? builtIn = primary is null ? null : ReadInternalConnector(primary.Name, drmRoot);
        return new ICapability[]
        {
            MachineEnvironmentCatalog.DisplayCount.Observe(outputs.Count, timestamp, source),
            builtIn.HasValue ? MachineEnvironmentCatalog.DisplayPrimaryInternal.Observe(builtIn.Value, timestamp, "Linux DRM connector type")
                : MachineEnvironmentCatalog.DisplayPrimaryInternal.Unavailable(CapabilityAvailability.Unknown, timestamp, "主屏的 DRM 连接器不可确认"),
            primary is not null ? MachineEnvironmentCatalog.DisplayPrimaryResolution.Observe($"{primary.Width}×{primary.Height}", timestamp, source)
                : MachineEnvironmentCatalog.DisplayPrimaryResolution.Unavailable(CapabilityAvailability.Unknown, timestamp, "多个活动输出未指定主屏"),
            primary?.RefreshHz is { } rate ? MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Observe(rate, timestamp, source)
                : MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(CapabilityAvailability.Unknown, timestamp, "主屏或当前刷新率不可读"),
        };
    }

    private static bool? ReadInternalConnector(string outputName, string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            string[] paths = Directory.EnumerateDirectories(root).Take(257).ToArray();
            if (paths.Length > 256) return null;
            string[] matches = paths.Where(path => Path.GetFileName(path).EndsWith("-" + outputName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) return null;
            using StreamReader reader = new(Path.Combine(matches[0], "status"));
            char[] buffer = new char[32];
            int length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length == buffer.Length || new string(buffer, 0, length).Trim() != "connected") return null;
            return outputName.StartsWith("eDP-", StringComparison.Ordinal) || outputName.StartsWith("LVDS-", StringComparison.Ordinal)
                || outputName.StartsWith("DSI-", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static ICapability[] Unavailable(DateTimeOffset timestamp, CapabilityAvailability availability, string reason) =>
    [
        MachineEnvironmentCatalog.DisplayCount.Unavailable(availability, timestamp, reason),
        MachineEnvironmentCatalog.DisplayPrimaryInternal.Unavailable(availability, timestamp, reason),
        MachineEnvironmentCatalog.DisplayPrimaryResolution.Unavailable(availability, timestamp, reason),
        MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Unavailable(availability, timestamp, reason),
    ];
}
