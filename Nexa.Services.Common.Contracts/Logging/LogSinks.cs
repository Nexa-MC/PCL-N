

namespace Nexa.Services.Logging;

/// <summary>
/// One sink that mirrors recorded log entries outside the state ring. Sinks never influence
/// the operation being logged: implementations must catch their own IO failures.
/// </summary>
public interface ILogSink
{
    void Write(LogEntry entry, string formattedLine);
}

/// <summary>A disk mirror that applies retention asynchronously, outside log publication.</summary>
public interface ILogRetentionSink
{
    void SetRetentionDays(int days);
}
