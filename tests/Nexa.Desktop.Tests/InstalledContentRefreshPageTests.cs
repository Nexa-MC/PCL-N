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
    private static void ContentRefreshPublishesSmallBatchesAndCombinesUpdateChecks()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.GetFullPath("refresh-instance"); var queries = new XsrQueryRouterBuilder(); int scans = 0;
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (query, _) =>
        {
            scans++; AssertFalse(query.CheckModUpdates);
            return new(XsrResult.Success(new InstanceManagementSnapshot(instance, instance, "1.21.1", [],
                [new("overview", "总览"), new("game", "游戏设置"), new("mods", "模组", instance)], true, "")
            {
                Contents = [new("mods", Enumerable.Range(0, 8).Select(i => new InstanceContentEntry($"mod-{i}.jar", false, 10)
                { ModifiedUtcTicks = 1, DisplayName = "Local " + i, Enabled = true }).ToArray(), true, null)]
            }));
        });
        var pending = new List<(ResourceContentOnlineBatchQuery Query, CancellationToken Token, TaskCompletionSource<XsrResult<ResourceContentOnlineBatch>> Reply)>();
        var resources = new XsrQueryRouterBuilder();
        resources.Register<ResourceContentOnlineBatchQuery, ResourceContentOnlineBatch>(ResourceCatalogContract.ContentOnlineBatch, (query, token) =>
        {
            var reply = new TaskCompletionSource<XsrResult<ResourceContentOnlineBatch>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((query, token, reply)); return new(reply.Task);
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        settings.ConfigureOnlineContent(resources.Build(new NoopDispatchObserver()), _ => { });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
        Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "SettingsNav.mods"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity);
        Pump(() => pending.Count == 2);
        AssertTrue(pending.All(item => item.Query.Files.Count <= 2)); AssertEqual(1, scans);
        var refresh = FindByKey(fixture.Shell, scene, "Management.刷新");
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiImage>(refresh.Entity) is not null);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiText>(refresh.Entity) is null);
        AssertEqual("刷新", fixture.Shell.Tree.GetComponent<XsrUiSemantic>(refresh.Entity)!.Label);
        var toolbar = FindByKey(fixture.Shell, scene, "ManagementToolbar");
        AssertTrue(Math.Abs(refresh.Rect.X + refresh.Rect.Width - toolbar.Rect.X - toolbar.Rect.Width) < 1);
        AssertFalse(scene.Nodes.Any(n => n.Text == "检查更新"));
        var search = FindByKey(fixture.Shell, scene, "ManagementContentSearch").Entity; fixture.Shell.Renderer.Focus(search);
        void Complete(int index, string title) => pending[index].Reply.SetResult(XsrResult.Success(new ResourceContentOnlineBatch(pending[index].Query.Files.Select(file =>
            new ResourceContentOnlineMatch(file, new(new("A", title, "", "", 0, "https://modrinth.com/project/A"), "2.0", [], null)
            { UpdateAvailable = true, UpdateVersion = new("next", "next", "3.0", "正式版", [], [], "", "") })).ToArray())));
        Complete(1, "Fast online");
        Pump(() => scene.Nodes.Any(n => n.Text == "Fast online")); // First batch remains pending.
        AssertFalse(pending[0].Reply.Task.IsCompleted); AssertEqual(search, fixture.Shell.Renderer.Focused);
        AssertEqual(1, scans); AssertTrue(scene.Nodes.Any(n => n.Text?.Contains("2/8", StringComparison.Ordinal) == true));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.刷新").Entity);
        Pump(() => scans == 2 && pending.Count >= 5);
        AssertTrue(pending[0].Token.IsCancellationRequested); AssertTrue(pending[2].Token.IsCancellationRequested);
        AssertTrue(pending.Skip(3).All(item => item.Query.Refresh));
        Complete(0, "Retired online"); fixture.Shell.Render(new(1000, 900));
        AssertFalse(scene.Nodes.Any(n => n.Text == "Retired online"));
        foreach (var item in pending.Where(item => !item.Reply.Task.IsCompleted)) item.Reply.SetResult(XsrResult.Success(new ResourceContentOnlineBatch([])));
    }
}
