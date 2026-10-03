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
    private static void OnlineContentListRetainsFocusAndRejectsRetiredInstances()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
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
                [new("overview", "总览"), new("game", "游戏设置"), new("mods", "模组", q.InstanceDirectory)], true, "")
            { Contents = [new("mods", [new("local.jar", false, 10) { ModifiedUtcTicks = 1, DisplayName = "Local", Version = "1", Enabled = true }], true, null)] })));
        var pending = new List<(ResourceContentOnlineBatchQuery Query, CancellationToken Token, TaskCompletionSource<ResourceContentOnlineBatch> Reply)>();
        var resources = new XsrQueryRouterBuilder();
        resources.Register<ResourceContentOnlineBatchQuery, ResourceContentOnlineBatch>(ResourceCatalogContract.ContentOnlineBatch, async (q, ct) =>
        { var reply = new TaskCompletionSource<ResourceContentOnlineBatch>(TaskCreationOptions.RunContinuationsAsynchronously); pending.Add((q, ct, reply)); return XsrResult.Success(await reply.Task); });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        settings.ConfigureOnlineContent(resources.Build(new NoopDispatchObserver()), _ => { });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
        void Open()
        {
            Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "SettingsNav.mods"));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity);
        }
        Open(); Pump(() => pending.Count == 1);
        AssertTrue(scene.Nodes.Any(n => n.Text == "Local"));
        instance = "instance-B"; Pump(() => settings.SelectedSection == "overview" && pending[0].Token.IsCancellationRequested);
        ResourceContentOnlineBatch Reply(int index, string title) => new([new(pending[index].Query.Files[0], new(new("A", title, "", "", 0, "https://modrinth.com/project/A"), "2.34", [], null))]);
        pending[0].Reply.SetResult(Reply(0, "Retired"));
        Open(); Pump(() => pending.Count == 2);
        var search = FindByKey(fixture.Shell, scene, "ManagementContentSearch").Entity;
        fixture.Shell.Renderer.Focus(search); fixture.Shell.Renderer.SetTextInputValue(search, "Matched");
        pending[1].Reply.SetResult(Reply(1, "Matched"));
        Pump(() => scene.Nodes.Any(n => n.Text == "Matched"));
        AssertEqual(search, fixture.Shell.Renderer.Focused);
        AssertTrue(scene.Nodes.Any(n => n.Text == "2.34"));
        AssertFalse(scene.Nodes.Any(n => n.Text == "Retired"));
        fixture.Shell.Render(new(1000, 900)); AssertEqual(2, pending.Count);
    }
}
