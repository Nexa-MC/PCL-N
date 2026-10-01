using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ContentGraphPageBoundsRelationsAndRetiresOldInstances()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = "instance-A";
        var pending = new List<(InstanceManagementQuery Query, CancellationToken Token, TaskCompletionSource<InstanceManagementSnapshot> Completion)>();
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        InstanceManagementSnapshot Snapshot(string name, InstanceContentGraph? graph = null) =>
            new(name, name, "1.20.1", [], [new("overview", "总览"), new("game", "游戏设置"), new("contentgraph", "内容依赖")], true, "")
            { ContentGraph = graph };
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, async (query, token) =>
        {
            if (!query.IncludeContentGraph) return XsrResult.Success(Snapshot(query.InstanceDirectory));
            var completion = new TaskCompletionSource<InstanceManagementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((query, token, completion)); return XsrResult.Success(await completion.Task);
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        void PumpUntil(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1000, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
        PumpUntil(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.contentgraph"));
        void OpenGraph()
        {
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.contentgraph").Entity);
            scene = fixture.Shell.Render(new(1000, 900));
        }
        OpenGraph(); PumpUntil(() => pending.Count == 1);
        AssertEqual("instance-A", pending[0].Query.InstanceDirectory);
        instance = "instance-B";
        PumpUntil(() => settings.SelectedSection == "overview" && pending[0].Token.IsCancellationRequested
            && scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.contentgraph"));
        var retired = new InstanceContentGraph([new(0, "retired-marker", "1", true, false, true, false, [])], 0, true, null);
        pending[0].Completion.SetResult(Snapshot("instance-A", retired));
        scene = fixture.Shell.Render(new(1000, 900));
        AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("retired-marker", StringComparison.Ordinal) == true));
        OpenGraph(); PumpUntil(() => pending.Count == 2);
        var nodes = Enumerable.Range(0, 2000).Select(index => new InstanceContentNode(index, "mod" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), "1",
            true, false, true, index == 0, index == 0
                ? [new("mod0012", "*", InstanceDependencyState.Present, [12])]
                : [])).ToArray();
        nodes[12] = nodes[12] with { Consumers = [new(0, 0)] };
        pending[1].Completion.SetResult(Snapshot("instance-B", new(Array.AsReadOnly(nodes), 0, true, null)));
        PumpUntil(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "ContentGraphNode.0"));
        XsrUiEntityId Find(string name)
        {
            XsrUiEntityId result = default;
            fixture.Shell.Tree.Walk(settings.Page, entity =>
            { if (fixture.Shell.Tree.Name(entity) == name) result = entity; return true; });
            AssertTrue(result.IsAssigned); return result;
        }
        int CountCards()
        {
            int count = 0;
            fixture.Shell.Tree.Walk(settings.Page, entity =>
            { if (fixture.Shell.Tree.Name(entity).StartsWith("ContentGraphNode.", StringComparison.Ordinal)) count++; return true; });
            return count;
        }
        AssertEqual(12, CountCards());
        Emit(fixture.Intents, "ui.settings.management.action", Find("ContentGraphDetails.0"));
        scene = fixture.Shell.Render(new(1000, 900));
        AssertTrue(scene.Nodes.Any(node => node.Text == "存在依赖循环，仅供排查。"));
        Emit(fixture.Intents, "ui.settings.management.action", Find("ContentGraphProvider.12"));
        scene = fixture.Shell.Render(new(1000, 900));
        AssertTrue(scene.Nodes.Any(node => node.Text == "mod0012 · 1"));
        AssertTrue(scene.Nodes.Any(node => node.Text == "mod0000 → mod0012"));
        Emit(fixture.Intents, "ui.settings.management.action", Find("ContentGraphConsumer.0"));
        scene = fixture.Shell.Render(new(1000, 900));
        AssertTrue(scene.Nodes.Any(node => node.Text == "mod0000 · 1"));
        Emit(fixture.Intents, "ui.settings.management.action", Find("ContentGraphBack"));
        scene = fixture.Shell.Render(new(1000, 900));
        Emit(fixture.Intents, "ui.settings.management.action", Find("ContentGraphNext.Nodes"));
        scene = fixture.Shell.Render(new(1000, 900));
        AssertEqual(12, CountCards()); Find("ContentGraphNode.12");
        var search = Find("ContentGraphSearch");
        fixture.Shell.Renderer.Focus(search);
        fixture.Shell.Renderer.SetTextInputValue(search, "mod1999");
        scene = fixture.Shell.Render(new(1000, 900));
        AssertEqual(search, fixture.Shell.Renderer.Focused);
        AssertEqual(1, CountCards()); Find("ContentGraphNode.1999");
        AssertEqual(2, pending.Count); // Rendering and searching never restart graph I/O.
        Emit(fixture.Intents, "ui.settings.management.action", Find("Management.刷新"));
        scene = fixture.Shell.Render(new(1000, 900)); PumpUntil(() => pending.Count == 3);
        instance = "instance-C";
        PumpUntil(() => pending[2].Token.IsCancellationRequested && settings.SelectedSection == "overview");
        pending[2].Completion.SetResult(Snapshot("instance-B", new(Array.AsReadOnly(nodes), 0, true, null)));
        scene = fixture.Shell.Render(new(1000, 900)); AssertEqual(0, CountCards());
    }
}
