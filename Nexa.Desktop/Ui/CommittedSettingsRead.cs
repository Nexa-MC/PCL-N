using Nexa.Services.Settings;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Ui;

internal static class CommittedSettingsRead
{
    internal static async ValueTask<SettingsEffectiveSnapshot?> QueryAsync(XsrQueryRouter queries, CancellationToken token)
    {
        if (!queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var route)) return null;
        var result = await queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(route, new(), cancellationToken: token).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }
}
