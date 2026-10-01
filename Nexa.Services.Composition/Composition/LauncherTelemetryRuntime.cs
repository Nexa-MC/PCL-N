using Nexa.Services.Foundation;
using Nexa.Services.Rollouts;
using Nexa.Services.Telemetry;
using Nexa.Services.Updates;

namespace Nexa.Services.Composition;

/// <summary>Owns client identity, telemetry transport and their shutdown order.</summary>
public sealed class LauncherTelemetryRuntime : IDisposable, IAsyncDisposable
{
    private readonly CloudflareApiClient? _identity;
    public LauncherTelemetrySession? Session { get; }

    public LauncherTelemetryRuntime(FoundationHost host, RolloutService rollouts, NexaUpdateService updates,
        string version, Func<Stream?> embeddedCertificate)
    {
        try { _identity = CloudflareApiClient.TryCreate(embeddedCertificate); }
        catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        { host.Logging.Warn("Cloudflare", "API 客户端身份不可用，联网服务暂不可用。"); }
        if (_identity is null)
        {
            host.Logging.Warn("Telemetry", "遥测未启动：此构建未包含可用 API 客户端证书。请通过 NEXA_API_CLIENT_CERT_PATH 配置有效 PFX。");
            return;
        }
        Session = new(host.Telemetry, host.Settings, new CloudflareTelemetryTransport(_identity.Client,
            () => rollouts.CompactTelemetryBatches, reason => host.Logging.Warn("Telemetry", reason)), host.Logging, version, host.Work);
        host.Logging.AddSink(Session);
        rollouts.Record = Session.Record;
        updates.Record = Session.Record;
    }
    public void Dispose() { Session?.Dispose(); _identity?.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        if (Session is not null) await Session.DisposeAsync().ConfigureAwait(false);
        _identity?.Dispose();
    }
}
