using Nexa.Platform;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void WindowsEventCorrelationAdmitsActualMetadataWithoutPayloadsOrCausality()
    {
        var since = DateTimeOffset.Parse("2026-10-08T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var until = since.AddMinutes(1);
        string Event(string channel, string provider, int id, int pid, string time = "2026-10-08T00:00:30Z") =>
            $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{provider}'/><EventID>{id}</EventID><TimeCreated SystemTime='{time}'/><Execution ProcessID='{pid}'/><Channel>{channel}</Channel></System><EventData><Data>C:\\private\\account-secret.dmp</Data></EventData></Event>";
        string system = Event("System", "Display", 4101, 0)
            + Event("System", "Microsoft-Windows-WHEA-Logger", 18, 0)
            + Event("System", "Fixture.Provider", 7, 42)
            + Event("System", "Fixture.Provider", 7, 99)
            + Event("System", "Display", 4101, 0, "2026-10-08T00:02:00Z")
            + Event("System", "Microsoft-Windows-WHEA-Logger", 99, 0)
            + Event("Application", "Application Error", 1000, 0);
        var result = WindowsSystemEventCorrelation.Parse(system, "System", 42, since, until);
        AssertTrue(result is not null);
        AssertEqual(3, result!.Count);
        AssertTrue(result[0].Message.Contains("category=gpu-reset relation=time-window", StringComparison.Ordinal));
        AssertTrue(result[1].Message.Contains("category=hardware-error relation=time-window", StringComparison.Ordinal));
        AssertTrue(result[2].Message.Contains("category=process-event relation=process-id", StringComparison.Ordinal));
        AssertTrue(result.All(entry => entry.Source == "windows.wevtutil/System" && entry.Timestamp >= since && entry.Timestamp <= until));
        AssertFalse(result.Any(entry => entry.Message.Contains("private", StringComparison.Ordinal)
            || entry.Message.Contains("account-secret", StringComparison.Ordinal) || entry.Message.Contains("cause", StringComparison.Ordinal)));

        var application = WindowsSystemEventCorrelation.Parse(Event("Application", "Application Error", 1000, 0),
            "Application", 42, since, until);
        AssertTrue(application is { Count: 1 });
        AssertTrue(application![0].Message.Contains("category=application-error relation=time-window", StringComparison.Ordinal));
        AssertEqual(0, WindowsSystemEventCorrelation.Parse("", "System", 42, since, until)!.Count);
        AssertTrue(WindowsSystemEventCorrelation.Parse("<broken>", "System", 42, since, until) is null);
        AssertTrue(WindowsSystemEventCorrelation.Parse(new string('x', 32768), "System", 42, since, until) is null);
        AssertTrue(WindowsSystemEventCorrelation.Parse(system, "arbitrary-channel", 42, since, until) is null);
        AssertTrue(WindowsSystemEventCorrelation.Parse("<!DOCTYPE Event [<!ENTITY secret SYSTEM 'file:///private'>]><Event>&secret;</Event>",
            "System", 42, since, until) is null);
        string oversizedWindow = string.Concat(Enumerable.Repeat(Event("System", "Display", 4101, 0), 40));
        AssertEqual(32, WindowsSystemEventCorrelation.Parse(oversizedWindow, "System", 42, since, until)!.Count);
    }
}
