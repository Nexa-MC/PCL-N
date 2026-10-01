namespace Nexa.Services.Logging;

/// <summary>Logging port for producers that do not own the log service lifecycle.</summary>
public interface ILogWriter
{
    void Write(LogLevel level, string subsystem, string message, string? exceptionText = null);
}

public static class LogWriterExtensions
{
    public static void Info(this ILogWriter writer, string subsystem, string message) => writer.Write(LogLevel.Info, subsystem, message);
    public static void Warn(this ILogWriter writer, string subsystem, string message) => writer.Write(LogLevel.Warn, subsystem, message);
    public static void Debug(this ILogWriter writer, string subsystem, string message) => writer.Write(LogLevel.Debug, subsystem, message);
    public static void Error(this ILogWriter writer, string subsystem, string message, string? exceptionText = null) => writer.Write(LogLevel.Error, subsystem, message, exceptionText);
}
