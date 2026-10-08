using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Desktop.Ui;

internal sealed partial class SettingsPageController
{
    /// <summary>Populates the real retained settings page before native presentation.</summary>
    internal async Task PrepareStartupAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_catalog is null)
        {
            if (!_queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var route))
                throw new InvalidOperationException("Settings catalog route is not registered.");
            _catalogReading ??= _queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(route,
                new(true), cancellationToken: token).AsTask();
            var catalog = await _catalogReading.WaitAsync(token).ConfigureAwait(true);
            if (!catalog.IsSuccess) throw new IOException(catalog.Error?.Message ?? "无法读取设置目录。");
            _catalogReading = null;
            _catalog = catalog.Value!;
            BuildNavigation();
            BuildSections();
        }
        string? instance = _instanceDirectory?.Invoke();
        if (_instanceDirectory is not null && string.IsNullOrWhiteSpace(instance)) return;
        _instance = instance;
        if (!_queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var read))
            throw new InvalidOperationException("Effective settings route is not registered.");
        if (_values is null || _revision != _store.Read<long>(_revisionId, token).Value)
        {
            _reading ??= _queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(read,
                new(instance), cancellationToken: token).AsTask();
            var settings = await _reading.WaitAsync(token).ConfigureAwait(true);
            if (!settings.IsSuccess) throw new IOException(settings.Error?.Message ?? "无法读取初始设置。");
            _reading = null;
            _values = settings.Value!;
            _revision = _values.Revision;
            _developer = _values.Values.Any(item => item.Key == "developer.enabled" && item.Value.Value == "true");
            BuildSections();
            UpdateEditors();
        }
    }

    internal async Task PrepareVisibleStartupAsync(CancellationToken token)
    {
        if (_shell.Stage.Navigation.Current != Page) return;
        // Only queries actually admitted by this section participate. User operations,
        // installation, exports, remote probes and periodic update clocks are excluded.
        for (int pass = 0; pass < 4; pass++)
        {
            OnFrame(this, EventArgs.Empty);
            Task[] pending = new Task?[]
            {
                _reading, _catalogReading, _machineReading, _hardwareAdviceRead,
                _identityRead, _managementRead, _storageStatusRead, _javaInventoryRead,
                _javaDiagnosticRead, _launchProfilesReading, _runtimeTraceRead,
                _networkTraceReading, _resourceResolutionReading, _updatePolicyReading,
            }.OfType<Task>().Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(token).ConfigureAwait(true);
        }
        OnFrame(this, EventArgs.Empty);
    }

    internal async Task PrepareIndependentStartupAsync(CancellationToken token)
    {
        UpdateIndependentFacts();
        // Read the initial local updater receipt and committed channel policy once. Its
        // installation/resume operation and subsequent progress reads are lifetime work.
        Task[] initial = new Task?[] { _updatePolicyReading, _automaticReading }
            .OfType<Task>().ToArray();
        await Task.WhenAll(initial).WaitAsync(token).ConfigureAwait(true);
        UpdateIndependentFacts();
    }

    internal async Task PrepareInitialUpdateAsync(CancellationToken token)
    {
        if (_updateReading is { } initial)
            await initial.WaitAsync(token).ConfigureAwait(true);
        UpdateReleaseCheck();
    }

    internal void SetStartupUpdateOffline()
    {
        _updateCheckStop?.Cancel();
        _updateCheckStop?.Dispose();
        _updateCheckStop = null;
        if (_updateReading is { } retired)
            _ = retired.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _updateReading = null;
        _startupUpdateChecked = true;
        _updateStatus = "暂时无法检查更新，请稍后重试。";
        if (_selected == "advanced") BuildSections();
    }
}
