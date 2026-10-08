using System.Globalization;
using Nexa.Desktop.Ui;
using Nexa.Services.Capabilities;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void HardwareAdviceUsesSameCapturedEstimatePreflightAndNeverWritesConfiguration()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var empty = new MachineCapabilitySnapshot(0, now, []);
        var unknown = SettingsPageController.CaptureHardwareAdvice(empty, null);
        AssertTrue(unknown.Shader.Contains("不可用", StringComparison.Ordinal));
        AssertTrue(unknown.RenderDistance.Contains("不可用", StringComparison.Ordinal));
        AssertTrue(unknown.Risk.Contains("未知", StringComparison.Ordinal));
        AssertFalse(unknown.ResourceRisk);
        var distance = MachineInstanceCatalog.MinecraftSettingsRenderDistance;
        var packs = MachineInstanceCatalog.MinecraftSettingsResourcePacks;
        var gpuPressure = new CapabilityDefinition<bool>("gpu.derived.vram_pressure", "GPU risk", "显卡", "Fixture");
        var loadPeak = new CapabilityDefinition<long>("resource.derived.load_peak", "Load peak", "资源", "Fixture");
        var shaderInput = new CapabilityDefinition<long>("resource.shader.estimated_gpu_memory", "Shader estimate", "资源", "Fixture");
        MachineCapabilitySnapshot Snapshot(long revision, int chunks, bool risk)
        {
            IReadOnlyList<ICapability> input = [distance.Observe(chunks, now, "captured options fixture"),
                packs.Observe(["private-pack-path.zip", "second-private-name.zip"], now, "captured options fixture"),
                loadPeak.Observe(256L * 1048576, now, "resource analysis fixture"),
                shaderInput.Observe(512L * 1048576, now, "resource analysis fixture"), gpuPressure.Observe(risk, now, "GPU metric fixture")];
            var projected = new ResourceEstimatorProjection().Project(input.ToDictionary(value => value.Id, StringComparer.Ordinal), now,
                new("/fixture/instance", "fixture"));
            return new(revision, now, input.Concat(projected));
        }
        var first = Snapshot(1, 24, true);
        var report = CapabilityPreflightEngine.Evaluate(first);
        var facts = SettingsPageController.CaptureHardwareAdvice(first, report);
        AssertTrue(facts.ResourceRisk);
        AssertTrue(facts.Shader.Contains(first.Get<long>("estimate.graphics.shader")!.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        AssertTrue(facts.ResourcePacks.Contains(first.Get<long>("estimate.resource.load_peak")!.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        AssertTrue(facts.RenderDistance.StartsWith("24 chunks", StringComparison.Ordinal));
        AssertFalse(facts.ResourcePacks.Contains("private", StringComparison.Ordinal));
        AssertTrue(facts.Risk.Contains("GPU_VRAM_RUNTIME_LOW", StringComparison.Ordinal));

        var instance = Instance("hardware-advice-fixture");
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([instance]));
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        var current = first;
        MachineCapabilityQuery? capturedScope = null;
        queries.Register<MachineCapabilityQuery, MachineCapabilitySnapshot>(MachineCapabilityStateContract.SnapshotQuery,
            (query, _) => { capturedScope = query; return ValueTask.FromResult(XsrResult.Success(current)); });
        var pending = new TaskCompletionSource<XsrResult<CapabilityPreflightReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<MachineCapabilitySnapshot> evaluated = [];
        queries.Register<LaunchPreflightQuery, CapabilityPreflightReport>(MachineCapabilityStateContract.PreflightQuery,
            (query, _) =>
            {
                evaluated.Add(query.Snapshot); return ReferenceEquals(query.Snapshot, first)
                ? new(pending.Task) : ValueTask.FromResult(XsrResult.Success(CapabilityPreflightEngine.Evaluate(query.Snapshot)));
            });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<MachineCapabilityRefresh>(MachineCapabilityStateContract.RefreshCommand, (_, token) =>
        {
            current = Snapshot(2, 16, false);
            fixture.Store.Publish(fixture.Store.Resolve(MachineCapabilityStateContract.RevisionKey), 2L, cancellationToken: token);
            return ValueTask.FromResult(XsrResult.Success());
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1100, 2800));
        void PumpUntil(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1100, 2800)); return condition(); }, TimeSpan.FromSeconds(5)));
        PumpUntil(() => HasKey(fixture.Shell, scene, "SettingsNav.platform"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.platform").Entity);
        PumpUntil(() => HasKey(fixture.Shell, scene, "HardwareAdvice.Risk.Facts"));
        AssertEqual(instance.DirectoryPath, capturedScope!.InstanceDirectory);
        AssertTrue(FindByKey(fixture.Shell, scene, "HardwareAdvice.Risk.Facts").Text!.Contains("未知", StringComparison.Ordinal));
        pending.SetResult(XsrResult.Success(report));
        PumpUntil(() => FindByKey(fixture.Shell, scene, "HardwareAdvice.Risk.Facts").Text!.Contains("GPU_VRAM_RUNTIME_LOW", StringComparison.Ordinal));
        long revision = fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Revision;
        foreach (string key in new[] { "Shader", "ResourcePack", "RenderDistance", "Risk" })
            fixture.Shell.Tree.Walk(FindByKey(fixture.Shell, scene, "HardwareAdvice." + key).Entity, entity =>
            { AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiTextInput>(entity) is null); return true; });
        AssertTrue(evaluated.All(snapshot => ReferenceEquals(snapshot, first)));
        AssertFalse(scene.Nodes.Where(node => fixture.Shell.Tree.Name(node.Entity).StartsWith("HardwareAdvice.", StringComparison.Ordinal))
            .Any(node => node.Text?.Contains("private-pack", StringComparison.Ordinal) == true));
        Emit(fixture.Intents, "ui.settings.platform.refresh", FindByKey(fixture.Shell, scene, "PlatformRefresh").Entity);
        PumpUntil(() => FindByKey(fixture.Shell, scene, "HardwareAdvice.RenderDistance.Facts").Text!.StartsWith("16 chunks", StringComparison.Ordinal));
        AssertEqual(revision, fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Revision);
        AssertTrue(evaluated.Any(snapshot => ReferenceEquals(snapshot, current)));
    }
}
