using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void AdvancedLaunchFactsPageUsesReadOnlyTypedQueriesAndPagedSnapshots()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.Combine(fixture.TemporaryDirectory, "minecraft", "versions", "diagnostic");
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.20.1", [],
                [new("overview", "总览"), new("game", "游戏设置"), new("diagnostics", "实例诊断")], true, ""))));
        int reads = 0, writes = 0;
        queries.Register<MinecraftLaunchPlanDiagnosticQuery, MinecraftLaunchPlanDiagnosticSnapshot>(MinecraftLaunchPlanDiagnosticContract.Query, (q, _) =>
        {
            reads++; AssertEqual(instance, q.InstanceDirectory); return ValueTask.FromResult(XsrResult.Success(new MinecraftLaunchPlanDiagnosticSnapshot(instance,
            DateTimeOffset.UtcNow, "fixture-java", instance, "fixture.Main", "Vanilla", 4096, false, ["fixture.Main", "--accessToken", "<redacted>"],
            ["fixture-classpath"], ["fixture-native"], ["ENV_NAME"], false)));
        });
        queries.Register<InstanceLocalDocumentsQuery, InstanceLocalDocumentsSnapshot>(InstanceLocalDocumentsContract.Query, (q, _) =>
        {
            reads++; AssertEqual(instance, q.InstanceDirectory); return ValueTask.FromResult(XsrResult.Success(new InstanceLocalDocumentsSnapshot(instance, DateTimeOffset.UtcNow,
            [new("manifest", "versions/diagnostic/diagnostic.json", "read", Enumerable.Range(0, 70).Select(index => "fixture-line-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), false)], true)));
        });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<InstanceIdentitySaveCommand>(InstanceIdentityContract.Save, (_, _) => { writes++; return ValueTask.FromResult(XsrResult.Success()); });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        // The launch-facts card follows the actual runtime/resource cards; expose all
        // supported cards so viewport clipping does not stand in for query/paging behavior.
        XsrUiScene scene = fixture.Shell.Render(new(1500, 6000));
        void Pump(Func<bool> ready) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1500, 6000)); return ready(); }, TimeSpan.FromSeconds(5)));
        void Click(string name, string intent) => Emit(fixture.Intents, intent, FindByKey(fixture.Shell, scene, name).Entity);
        Pump(() => HasKey(fixture.Shell, scene, "SettingsNav.diagnostics"));
        Click("SettingsNav.diagnostics", "ui.settings.section"); Pump(() => HasKey(fixture.Shell, scene, "AdvancedLaunchDiagnostics"));
        AssertEqual(0, reads);
        Click("Management.读取启动事实", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(node => node.Text == "fixture.Main"));
        AssertEqual(2, reads); AssertEqual(0, writes);
        Click("Management.Classpath", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(node => node.Text == "fixture-classpath"));
        Click("Management.Manifest", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(node => node.Text == "fixture-line-0"));
        AssertFalse(scene.Nodes.Any(node => node.Text == "fixture-line-69"));
        Click("Management.下一页事实", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(node => node.Text == "fixture-line-40"));
        AssertFalse(scene.Nodes.Any(node => node.Text == "fixture-line-0")); AssertEqual(2, reads); AssertEqual(0, writes);
    }
}
