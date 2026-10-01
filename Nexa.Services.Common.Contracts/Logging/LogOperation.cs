
using System.Globalization;


namespace Nexa.Services.Logging;


/// <summary>
/// Locale-independent exception facts. Raw messages, Data and HTTP bodies can contain secrets
/// or translated UI text; diagnostic records deliberately keep only type/status and stack.
/// </summary>
public static class ExceptionDiagnostics
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        List<string> lines = [];
        for (Exception? current = exception; current is not null && lines.Count < 16; current = current.InnerException)
        {
            string status = current is HttpRequestException { StatusCode: { } code }
                ? $" http_status={(int)code}" : string.Empty;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"exception={current.GetType().FullName} hresult=0x{current.HResult:X8}{status}"));
            if (!string.IsNullOrWhiteSpace(current.StackTrace)) lines.Add(current.StackTrace);
        }
        return string.Join(Environment.NewLine, lines);
    }
}
