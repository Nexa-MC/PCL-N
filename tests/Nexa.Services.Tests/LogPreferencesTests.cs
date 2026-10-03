using Nexa.Services.Foundation;
using Nexa.Services.Logging;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void LogPreferencesApplyCommittedStateAndDispose()
    {
        var port = new PolicyFailingPort(); var (_, initial) = PolicyFixture(port);
        AssertTrue(initial.Set(new("diagnostics.log-level", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "3"))).IsSuccess);
        AssertTrue(initial.Set(new("diagnostics.log-lines", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "80"))).IsSuccess);
        using var host = FoundationComposer.Compose(port, LauncherDefaults.CreateSchema(), new ThrowingProfilePort(), configureLogging: log => log.MaximumLevel = LogLevel.RealTime);
        AssertEqual(LogLevel.Debug, host.Logging.MaximumLevel); AssertEqual(80, host.Logging.RetentionLimit);
        var policy = host.SettingsPolicy;
        AssertTrue(policy.Set(new("diagnostics.log-level", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
        host.Logging.Clear(); host.Logging.Info("fixture", "not retained"); host.Logging.Error("fixture", "retained");
        AssertEqual(1, host.Logging.GetSnapshot().Count);
        AssertTrue(policy.Set(new("diagnostics.log-level", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2"))).IsSuccess);
        host.Logging.Clear();
        for (int i = 0; i < 100; i++) host.Logging.Info("fixture", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AssertEqual(80, host.Logging.GetSnapshot().Count);
        AssertTrue(policy.Set(new("diagnostics.log-lines", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "50"))).IsSuccess);
        var retained = host.Logging.GetSnapshot(); AssertEqual(50, retained.Count);
        AssertTrue(retained.Zip(retained.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        AssertEqual(50, host.StateStore.ReadCollection<LogEntry>(host.StateStore.Resolve(LogService.EntriesKey)).Count);
        port.Fail = true;
        AssertFalse(policy.Set(new("diagnostics.log-level", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "4"))).IsSuccess);
        AssertEqual(LogLevel.Info, host.Logging.MaximumLevel);
        port.Fail = false;
        AssertFalse(policy.Set(new("diagnostics.log-lines", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2001"))).IsSuccess);
        AssertFalse(policy.Set(new("diagnostics.log-level", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "4"), Path.GetFullPath("logs-instance"))).IsSuccess);
        AssertTrue(policy.Set(new("diagnostics.log-level", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual(LogLevel.RealTime, host.Logging.MaximumLevel);
        AssertTrue(policy.Set(new("diagnostics.log-lines", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
        AssertEqual(500, host.Logging.RetentionLimit);
        host.Dispose();
        AssertTrue(policy.Set(new("diagnostics.log-level", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "0"))).IsSuccess);
        AssertEqual(LogLevel.RealTime, host.Logging.MaximumLevel); // Observer is gone.
        host.Logging.Dispose();
    }

    private static async ValueTask LogRetentionChangesDoNotWaitForObservers()
    {
        using var observer = new BlockingRetentionObserver(); using var log = CreateLogService(128, observer);
        var writer = Task.Run(() => log.Info("fixture", "first"));
        AssertTrue(observer.Entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var preference = Task.Run(() => log.RetentionLimit = 50);
            AssertTrue(preference.Wait(TimeSpan.FromSeconds(2))); AssertEqual(50, log.RetentionLimit);
        }
        finally { observer.Release.Set(); await writer; }
        log.RetentionLimit = 500; AssertEqual(128, log.RetentionLimit);
        for (int i = 0; i < 150; i++) log.Info("fixture", "next");
        log.RetentionLimit = 50; AssertEqual(50, log.GetSnapshot().Count);
        log.RetentionLimit = 100; AssertEqual(50, log.GetSnapshot().Count);
    }

    private sealed class BlockingRetentionObserver : IXsrStateObserver, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        private int _blocked;
        public void OnChanged(XsrStateChange change)
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0) return;
            Entered.Set(); Release.Wait(TimeSpan.FromSeconds(10));
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    private static void LogPreferencesRetainLegacyValuesAndBounds()
    {
        foreach (var (slider, expected) in new[] { (0, 50), (5, 100), (12, 450), (13, 500), (20, 1200), (29, 2000) })
        {
            var port = new InMemorySettingsPort(); port.Save(new Dictionary<string, string> { ["SystemLogLevel"] = "4", ["SystemMaxLog"] = slider.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var (_, policy) = PolicyFixture(port);
            AssertEqual("4", Effective(policy, "diagnostics.log-level").Value.Value);
            AssertEqual(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Effective(policy, "diagnostics.log-lines").Value.Value);
        }
    }
}
