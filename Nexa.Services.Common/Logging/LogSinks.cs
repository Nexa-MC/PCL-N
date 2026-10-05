namespace Nexa.Services.Logging;

/// <summary>
/// Mirrors log entries to the process console. Active only when the process actually has a
/// console output stream (launched from a terminal or with redirected output); detached GUI
/// launches disable the sink instead of paying for writes nobody sees.
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private bool _disabled;

    public void Write(LogEntry entry, string formattedLine)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            Console.WriteLine(formattedLine);
        }
        catch (Exception)
        {
            // No console (detached GUI launch) or a broken stdout: stop mirroring instead of
            // touching the log path on every entry.
            _disabled = true;
        }
    }
}
