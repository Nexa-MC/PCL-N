using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ServerPageEditsThroughSealedCommandsAndJoinsTransiently()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
        string instance = "instance-A";
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [],
                [new("overview", "总览"), new("game", "游戏设置"), new("servers", "服务器")], true, ""))));
        int reads = 0;
        queries.Register<InstanceServerListQuery, InstanceServerList>(InstanceServerListContract.Read, (q, _) =>
        { reads++; return ValueTask.FromResult(XsrResult.Success(new InstanceServerList("revision-1", [new(0, "Example", "example.org")]))); });
        int statusChecks = 0;
        queries.Register<InstanceServerStatusQuery, InstanceServerStatus>(InstanceServerListContract.Status, (q, _) =>
        {
            AssertEqual(instance, q.InstanceDirectory); AssertEqual("revision-1", q.ExpectedRevision); AssertEqual(0, q.SourceIndex);
            int? server = ++statusChecks switch { 1 => null, 2 => 763, _ => 764 };
            return ValueTask.FromResult(XsrResult.Success(new InstanceServerStatus(true, "Fixture description", "Untrusted version name", 3, 20, 5) { ClientProtocol = 763, ServerProtocol = server }));
        });
        var commands = new XsrCommandRouterBuilder(); InstanceServerListSaveCommand? saved = null;
        commands.Register<InstanceServerListSaveCommand>(InstanceServerListContract.Save, (c, _) =>
        { saved = c; return ValueTask.FromResult(XsrResult.Success()); });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback, () => instance);
        string? joined = null; settings.JoinManagementServer = (directory, address) => { AssertEqual(instance, directory); joined = address; };
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
        void Click(string name, string intent) => Emit(fixture.Intents, intent, FindByKey(fixture.Shell, scene, name).Entity);
        Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "SettingsNav.servers"));
        Click("SettingsNav.servers", "ui.settings.section"); Pump(() => scene.Nodes.Any(n => n.Text == "Example"));
        Click("Management.检查状态", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(n => n.Text?.Contains("协议兼容无法判定", StringComparison.Ordinal) == true));
        Click("Management.检查状态", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(n => n.Text?.Contains("协议号一致（模组与认证另行检查）", StringComparison.Ordinal) == true));
        Click("Management.检查状态", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(n => n.Text?.Contains("协议号不同: 客户端 763 / 服务器 764", StringComparison.Ordinal) == true));
        Click("Management.加入", "ui.settings.management.action"); Pump(() => joined is not null); AssertEqual("example.org", joined);
        Click("Management.编辑", "ui.settings.management.action"); Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "ServerName"));
        var field = FindByKey(fixture.Shell, scene, "ServerName").Entity; fixture.Shell.Renderer.Focus(field); fixture.Shell.Renderer.SetTextInputValue(field, "Renamed");
        Click("Management.保存", "ui.settings.management.action"); Pump(() => saved is not null);
        AssertEqual(instance, saved!.InstanceDirectory); AssertEqual("revision-1", saved.ExpectedRevision); AssertEqual("Renamed", saved.Entries[0].Name);
        AssertEqual(0, saved.Entries[0].SourceIndex);
        Pump(() => reads == 2); fixture.Shell.Render(new(1000, 900)); AssertEqual(2, reads);
    }
}
