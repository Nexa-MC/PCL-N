using Nexa.Desktop.Ui;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstalledContentOnlineInfoKeepsLocalDetailsAndDiscardsStaleResults()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-online-ui-" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(root, "versions", "fixture"); Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "fixture.json"), """{"id":"fixture","_minecraftVersion":"1.21.1","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.0"}]}""");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        File.WriteAllText(Path.Combine(instance, "mods", "a.jar"), "fixture");
        try
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
            using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
            var reads = new List<(ResourceContentOnlineQuery Query, CancellationToken Token, TaskCompletionSource<ResourceContentOnline> Completion)>();
            var queries = new XsrQueryRouterBuilder();
            queries.Register<ResourceContentOnlineQuery, ResourceContentOnline>(ResourceCatalogContract.ContentOnline, async (query, token) =>
            {
                var completion = new TaskCompletionSource<ResourceContentOnline>(TaskCreationOptions.RunContinuationsAsynchronously);
                reads.Add((query, token, completion)); return XsrResult.Success(await completion.Task);
            });
            settings.ConfigureOnlineContent(queries.Build(new NoopDispatchObserver()), _ => { });
            fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
            var scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.mods"); }, TimeSpan.FromSeconds(5)));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.mods").Entity); scene = fixture.Shell.Render(new(1000, 650));
            void Open()
            {
                Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails.a.jar").Entity);
                scene = fixture.Shell.Render(new(1000, 650));
            }
            Open(); AssertEqual(1, reads.Count); AssertEqual(instance, reads[0].Query.InstanceDirectory);
            var localDetail = FindByKey(fixture.Shell, scene, "ManagementContentDetail").Entity;
            var back = FindByKey(fixture.Shell, scene, "Management.返回列表").Entity;
            var scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(fixture.Shell.Tree.Parent(localDetail))!;
            scroll.OffsetY = 400; fixture.Shell.Tree.MarkDirty(fixture.Shell.Tree.Parent(localDetail), XsrUiDirtyKinds.Layout); scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(scene.Nodes.Any(node => node.Text == "正在识别文件并获取项目资料…"));
            Emit(fixture.Intents, "ui.settings.management.action", back); scene = fixture.Shell.Render(new(1000, 650));
            AssertTrue(reads[0].Token.IsCancellationRequested);
            Open(); AssertEqual(2, reads.Count);
            reads[0].Completion.SetResult(new(new("Old", "旧结果", "", "", 1, "https://modrinth.com/project/Old"), "1", [], null));
            scene = fixture.Shell.Render(new(1000, 650)); AssertFalse(scene.Nodes.Any(node => node.Text == "旧结果"));
            localDetail = FindByKey(fixture.Shell, scene, "ManagementContentDetail").Entity;
            scroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(fixture.Shell.Tree.Parent(localDetail))!;
            scroll.OffsetY = 400; fixture.Shell.Tree.MarkDirty(fixture.Shell.Tree.Parent(localDetail), XsrUiDirtyKinds.Layout); scene = fixture.Shell.Render(new(1000, 650));
            double previousOffset = scroll.OffsetY;
            reads[1].Completion.SetResult(new(new("A", "Name", "中文简介", "Author", 12345, "https://modrinth.com/project/A") { ChineseName = "中文名称", Sources = [new(ResourceProvider.Modrinth, "A")] }, "1.0.0", [], null));
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return scene.Nodes.Any(node => node.Text == "中文名称"); }, TimeSpan.FromSeconds(5)));
            AssertEqual(localDetail, FindByKey(fixture.Shell, scene, "ManagementContentDetail").Entity);
            AssertTrue(scene.Nodes.Any(node => node.Text == "中文简介"));
            AssertEqual(previousOffset, scroll.OffsetY);
            // Integrity is a separate card after the online facts, so the last scroll position is
            // no longer the download count. Prove that the actual count remains reachable.
            for (int offset = 0; offset <= 4096 && !scene.Nodes.Any(node => node.Text == "12.3k"); offset += 128)
            {
                scroll.OffsetY = offset; fixture.Shell.Tree.MarkDirty(fixture.Shell.Tree.Parent(localDetail), XsrUiDirtyKinds.Layout);
                scene = fixture.Shell.Render(new(1000, 650));
            }
            AssertTrue(scene.Nodes.Any(node => node.Text == "12.3k"));
            AssertEqual(2, reads.Count); // Rendering completed information never starts a refresh loop.
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ResourceOptionalDependenciesWaitForUserChoice()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
        var source = new ResourceReference(ResourceProvider.Modrinth, "A"); var optional = new ResourceReference(ResourceProvider.Modrinth, "B");
        ResourceModInstallCommand? installed = null; ResourceModInstallCommand? planned = null;
        var queries = new XsrQueryRouterBuilder();
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, (query, token) => ValueTask.FromResult(XsrResult.Success(new ResourceSearchResult([
            new("A", "A", "", "", 10000, "https://modrinth.com/project/A") { Sources = [source], Kind = ResourceKind.Mod }], 1, 0))));
        queries.Register<ResourceModPlanQuery, ResourceInstallPlan>(ResourceCatalogContract.PlanMod, (query, token) =>
        {
            planned = query.Command;
            return ValueTask.FromResult(XsrResult.Success(new ResourceInstallPlan([], [new(optional, null, "可选前置")], false)));
        });
        var commands = new XsrCommandRouterBuilder();
        commands.Register<ResourceModInstallCommand>(ResourceCatalogContract.InstallMod, (command, token) => { installed = command; return ValueTask.FromResult(XsrResult.Success()); });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        page.ConfigureInstanceFilter(fixture.Foundation.Queries, () => new("root", "fixture"));
        page.ConfigureDownloads(commands.Build(new NoopDispatchObserver()), () => Task.FromResult<string?>(null), fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(page.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSelection>(page.Find("ResourceCategory.1"))!.IsSelected);
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiSelection>(page.Find("ResourceCategory.0"))!.IsSelected);
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceQuickDownload.A")); scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(installed is null && planned is not null); AssertEqual(0, planned!.OptionalDependencies.Count);
        scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(scene.Nodes.Any(node => node.Text == "选择可选依赖"));
        // The first detail action must survive retirement of list-only plugin bindings.
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceOptionalToggle.B")); scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(installed is null); AssertTrue(planned.OptionalDependencies.Contains(optional));
        scene = fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceOptionalInstall")); fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => installed is not null, TimeSpan.FromSeconds(3)));
        AssertTrue(installed!.OptionalDependencies.Contains(optional));
    }
}
