using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Nexa.Platform;

/// <summary>Fixed bounded Windows event windows; temporal events are never declared causal.</summary>
internal static class WindowsSystemEventCorrelation
{
    internal static async ValueTask<IReadOnlyList<PlatformSystemEvent>?> ReadAsync(int processId,
        DateTimeOffset since, DateTimeOffset until, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var system = ReadChannelAsync("System", processId, since, until, deadline.Token);
        var application = ReadChannelAsync("Application", processId, since, until, deadline.Token);
        await Task.WhenAll(system, application).ConfigureAwait(false);
        if (system.Result is null || application.Result is null) return null;
        return system.Result.Concat(application.Result).OrderBy(item => item.Timestamp).TakeLast(32).ToArray();
    }

    private static async Task<IReadOnlyList<PlatformSystemEvent>?> ReadChannelAsync(string channel, int pid,
        DateTimeOffset since, DateTimeOffset until, CancellationToken token)
    {
        string begin = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string end = until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string admitted = channel == "System"
            ? "(Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=1 or EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=46 or EventID=47))"
            : "EventID=1000 or EventID=1001";
        string query = $"*[System[TimeCreated[@SystemTime>='{begin}' and @SystemTime<='{end}'] and (Execution[@ProcessID='{pid.ToString(CultureInfo.InvariantCulture)}'] or {admitted})]]";
        var info = new ProcessStartInfo("wevtutil.exe")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "qe", channel, "/q:" + query, "/f:xml", "/c:32", "/rd:true" }) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            try
            {
                char[] output = new char[32768];
                int count = await process.StandardOutput.ReadBlockAsync(output.AsMemory(), token).ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                return process.ExitCode == 0 && count < output.Length
                    ? Parse(new string(output, 0, count), channel, pid, since, until) : null;
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        { return null; }
    }

    internal static IReadOnlyList<PlatformSystemEvent>? Parse(string xml, string channel, int pid,
        DateTimeOffset since, DateTimeOffset until)
    {
        if (xml.Length >= 32768 || channel is not ("System" or "Application")) return null;
        if (string.IsNullOrWhiteSpace(xml)) return [];
        try
        {
            using var reader = XmlReader.Create(new StringReader("<CapturedEvents>" + xml + "</CapturedEvents>"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32800 });
            var document = XDocument.Load(reader);
            List<PlatformSystemEvent> result = [];
            foreach (var entry in document.Descendants().Where(element => element.Name.LocalName == "Event").Take(32))
            {
                var system = entry.Elements().FirstOrDefault(element => element.Name.LocalName == "System");
                XElement? Field(string name) => system?.Elements().FirstOrDefault(element => element.Name.LocalName == name);
                if (!int.TryParse(Field("EventID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int eventId)
                    || !DateTimeOffset.TryParse(Field("TimeCreated")?.Attribute("SystemTime")?.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var time) || time < since || time > until
                    || Field("Channel")?.Value != channel) continue;
                string? provider = Field("Provider")?.Attribute("Name")?.Value;
                if (provider is null || provider.Length is < 1 or > 128
                    || provider.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ' '))) continue;
                bool relatedPid = int.TryParse(Field("Execution")?.Attribute("ProcessID")?.Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int recordedPid) && recordedPid == pid;
                bool tdr = channel == "System" && provider == "Display" && eventId == 4101;
                bool whea = channel == "System" && provider == "Microsoft-Windows-WHEA-Logger"
                    && eventId is 1 or 17 or 18 or 19 or 20 or 46 or 47;
                bool nativeCrash = channel == "Application" && eventId is 1000 or 1001;
                if (!relatedPid && !tdr && !whea && !nativeCrash) continue;
                string category = tdr ? "gpu-reset" : whea ? "hardware-error" : nativeCrash ? "application-error" : "process-event";
                result.Add(new(time, "windows.wevtutil/" + channel,
                    $"channel={channel} provider={provider} event_id={eventId.ToString(CultureInfo.InvariantCulture)} category={category} relation={(relatedPid ? "process-id" : "time-window")}"));
            }
            return result.AsReadOnly();
        }
        catch (XmlException) { return null; }
    }
}
