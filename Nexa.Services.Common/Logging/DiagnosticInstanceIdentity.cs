using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Logging;

public static class DiagnosticInstanceIdentity
{
    public static string ScopeHash(string instanceDirectory)
    {
        if (!Path.IsPathFullyQualified(instanceDirectory) || instanceDirectory.Length > 4096 || instanceDirectory.Any(char.IsControl))
            throw new ArgumentException("请选择有效的绝对实例目录。", nameof(instanceDirectory));
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static bool Valid(DiagnosticInstanceContext instance) => instance.ScopeHash is { Length: 64 } scope
        && scope.All(char.IsAsciiHexDigit) && instance.SessionId != Guid.Empty
        && (instance.FailureCode is null || instance.FailureCode.Length is > 0 and <= 128
            && instance.FailureCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+'))
        && instance.LaunchDurationMilliseconds is not < 0 and not > 86_400_000
        && !(instance.StartedAt is { } started && instance.EndedAt is { } ended && ended < started);
}
