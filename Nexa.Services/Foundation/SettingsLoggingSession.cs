using System.Globalization;
using Nexa.Services.Logging;
using Nexa.Services.Settings;

namespace Nexa.Services.Foundation;

/// <summary>Applies durable log preferences at the composition boundary, outside rendering.</summary>
internal sealed class SettingsLoggingSession : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SettingsPolicyService _policy;
    private readonly LogService _log;
    private readonly LogLevel _defaultLevel;
    private readonly object _gate = new();
    private long _revision = -1;
    private bool _disposed;

    internal SettingsLoggingSession(SettingsService settings, SettingsPolicyService policy, LogService log)
    {
        _settings = settings; _policy = policy; _log = log; _defaultLevel = log.MaximumLevel;
        settings.Changed += Apply; Apply(settings.Revision);
    }

    private void Apply(long _)
    {
        var result = _policy.Read(new());
        if (!result.IsSuccess) return;
        var snapshot = result.Value!;
        lock (_gate)
        {
            if (_disposed || snapshot.Revision <= _revision) return;
            _revision = snapshot.Revision;
            var level = snapshot.Values.FirstOrDefault(item => item.Key == "diagnostics.log-level" && item.ValidationError is null)?.Value.Value;
            _log.MaximumLevel = int.TryParse(level, CultureInfo.InvariantCulture, out int severity) && severity is >= 0 and <= 4 ? (LogLevel)severity : _defaultLevel;
            var lines = snapshot.Values.FirstOrDefault(item => item.Key == "diagnostics.log-lines" && item.ValidationError is null)?.Value.Value;
            if (int.TryParse(lines, CultureInfo.InvariantCulture, out int count) && count is >= 50 and <= 2000) _log.RetentionLimit = count;
            var days = snapshot.Values.FirstOrDefault(item => item.Key == "diagnostics.disk-log-days" && item.ValidationError is null)?.Value.Value;
            if (int.TryParse(days, CultureInfo.InvariantCulture, out int retention) && retention is >= 1 and <= 90) _log.DiskRetentionDays = retention;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _settings.Changed -= Apply; }
    }
}
