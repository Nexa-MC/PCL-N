using Nexa.Desktop.Ui;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstanceDiagnosticsJoinScopePreserveUnknownAndUseDurableHistory()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
        string instance = "instance-A"; DateTimeOffset started = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero); Guid ownedId = Guid.NewGuid(), otherId = Guid.NewGuid();
        MinecraftProcessSnapshot owned = new(ownedId, "same name", 42, MinecraftProcessState.Exited, 7, started, started.AddMinutes(1)) { InstanceDirectory = instance };
        MinecraftProcessSnapshot other = new(otherId, "same name", 43, MinecraftProcessState.Exited, 0, started.AddMinutes(2), started.AddMinutes(3)) { InstanceDirectory = "instance-B" };
        fixture.Store.PublishDelta(fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey), new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [owned, other], []));
        JvmHostObservation observation = new(ownedId, "same name", started, started.AddMinutes(1), 123, 900, 500, 2, 80, 999999, 999999, 999999, 999999, 999999, 0, 0, null, null, 7, [], [])
        { CoreMetricsObserved = true, CpuPercentObserved = false, MeasuredHeapPeakBytes = null, MeasuredNativePeakBytes = 2048, LaunchWindowMilliseconds = 30000, SystemEventsObserved = true, SystemEvents = ["owned scoped event"] };
        var otherObservation = observation with { SessionId = otherId, LaunchDurationMilliseconds = 999999, SystemEvents = ["OTHER INSTANCE PRIVATE EVENT"] };
        fixture.Store.PublishDelta(fixture.Store.Resolve(JvmHostStateContract.ObservationsKey), new XsrCollectionDelta<JvmHostObservation, Guid>(0, [observation, otherObservation], []));
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [], [new("overview", "总览"), new("game", "游戏设置"), new("diagnostics", "实例诊断")], true, ""))));
        int historyReads = 0; DurableInstanceDiagnosticHistoryQuery? requested = null;
        var pending = new TaskCompletionSource<XsrResult<IReadOnlyList<DurableDiagnosticEntry>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new DiagnosticInstanceContext(new string('A', 64), ownedId) { StartedAt = started, EndedAt = started.AddMinutes(1), ExitCode = 7, FailureCode = "FixtureCrash", LaunchDurationMilliseconds = 123 };
        DurableDiagnosticEntry crash = new(started.AddMinutes(1), "Process", "GameExit", "abnormal-exit", DiagnosticOperationOutcome.Failed) { Instance = context };
        DurableDiagnosticEntry unscoped = new(started, "Process", "OldUnscoped", "normal-exit", DiagnosticOperationOutcome.Completed);
        var stages = Enumerable.Range(0, 14).Select(index => new DurableDiagnosticEntry(started.AddSeconds(index), "Process", "GameLaunch", "fixture-stage-" + index, DiagnosticOperationOutcome.Entered) { Instance = context }).ToArray();
        queries.Register<DurableInstanceDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(DiagnosticWorkspaceContract.InstanceHistory, (q, _) =>
        {
            requested = q;
            return ++historyReads == 1 ? ValueTask.FromResult(XsrResult.Success<IReadOnlyList<DurableDiagnosticEntry>>([crash, unscoped, .. stages])) : new(pending.Task);
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Foundation.Commands,
            fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1100, 6000));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1100, 6000)); return condition(); }, TimeSpan.FromSeconds(5)));
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.diagnostics"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.diagnostics").Entity);
        Pump(() => scene.Nodes.Any(node => node.Text == "FixtureCrash"));
        AssertEqual("instance-A", requested!.InstanceDirectory);
        AssertEqual("123 ms", FindByKey(fixture.Shell, scene, "InstanceDiagnostics.LaunchDuration.Value").Text);
        AssertEqual("不可用", FindByKey(fixture.Shell, scene, "InstanceDiagnostics.Heap.Value").Text);
        AssertEqual("不可用", FindByKey(fixture.Shell, scene, "InstanceDiagnostics.CpuPeak.Value").Text);
        AssertEqual(2, scene.Nodes.Count(node => fixture.Shell.Tree.Name(node.Entity) == "InstanceDiagnostics.HistorySession.Value"));
        AssertTrue(scene.Nodes.Any(node => node.Text == "owned scoped event")); AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("OTHER INSTANCE", StringComparison.Ordinal) == true));
        AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("999999", StringComparison.Ordinal) == true));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.查看操作阶段").Entity);
        Pump(() => scene.Nodes.Any(node => node.Text == "fixture-stage-13"));
        AssertEqual(12, scene.Nodes.Count(node => fixture.Shell.Tree.Name(node.Entity) == "InstanceDiagnostics.OperationStage.Value"));
        AssertFalse(scene.Nodes.Any(node => node.Text == "OldUnscoped"));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.1/2 · 下一页").Entity);
        Pump(() => scene.Nodes.Any(node => node.Text == "fixture-stage-0"));
        AssertEqual(3, scene.Nodes.Count(node => fixture.Shell.Tree.Name(node.Entity) == "InstanceDiagnostics.OperationStage.Value"));
        AssertTrue(scene.Nodes.Any(node => node.Text == "fixture-stage-1"));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.刷新实例诊断").Entity);
        Pump(() => historyReads == 2); instance = "instance-B"; Pump(() => settings.SelectedSection == "overview");
        pending.SetResult(XsrResult.Success<IReadOnlyList<DurableDiagnosticEntry>>([crash])); scene = fixture.Shell.Render(new(1100, 2400));
        AssertFalse(scene.Nodes.Any(node => node.Text == "FixtureCrash")); AssertEqual("overview", settings.SelectedSection);
    }
}
