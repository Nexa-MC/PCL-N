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
    private static void InstalledContentListsAssociateIconsVersionsAndPackUpdates()
    {
        foreach (string page in new[] { "mods", "resourcepacks", "shaderpacks" })
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            string instance = "instance-A", filename = page == "mods" ? "local.jar" : "local.zip";
            var queries = new XsrQueryRouterBuilder();
            fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
            fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
            queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
                (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
            queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
                (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
            int scans = 0;
            queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            {
                scans++;
                return ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [],
                    [new("overview", "总览"), new("game", "游戏设置"), new(page, page, q.InstanceDirectory)], true, "")
                { Contents = [new(page, [new(filename, false, 10) { ModifiedUtcTicks = 1, DisplayName = "Local", Description = "本地描述", Version = "34", Enabled = page == "mods" ? true : null }], true, null)] }));
            });
            var project = new ResourceProject("A", "Online", "Summary", "Author", 1, "https://modrinth.com/project/A")
            { ChineseName = "在线中文名称", IconUrl = "https://cdn.modrinth.com/data/A/icon.png", Sources = [new(ResourceProvider.Modrinth, "A")] };
            var next = new ResourceVersion("New", "New", "2.0", "正式版", ["1.21.1"], [], "2026-02-01T00:00:00Z", "")
            { ProjectId = "A", File = new("new.zip", "https://cdn.modrinth.com/data/A/new.zip", 10, null, null) };
            var online = new ResourceContentOnline(project, "1.0", [next], null)
            { InstalledFiles = [new(new(ResourceProvider.Modrinth, "A"), "Old", filename, "hash", true)], UpdateVersion = next, UpdateAvailable = true };
            var resources = new XsrQueryRouterBuilder(); int batches = 0, icons = 0;
            resources.Register<ResourceContentOnlineBatchQuery, ResourceContentOnlineBatch>(ResourceCatalogContract.ContentOnlineBatch, (q, _) =>
            { batches++; return ValueTask.FromResult(XsrResult.Success(new ResourceContentOnlineBatch(q.Files.Select(f => new ResourceContentOnlineMatch(f, online)).ToArray()))); });
            resources.Register<ResourceContentOnlineQuery, ResourceContentOnline>(ResourceCatalogContract.ContentOnline, (_, _) => ValueTask.FromResult(XsrResult.Success(online)));
            var image = Nexa.Core.Media.PngImage.TryCreate(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="))!;
            var iconReply = new TaskCompletionSource<ResourceIconResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            resources.Register<ResourceIconQuery, ResourceIconResult>(ResourceCatalogContract.Icon, async (_, _) =>
            { icons++; return XsrResult.Success(await iconReply.Task); });
            ResourceContentUpdateCommand? updated = null;
            var completion = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commands = new XsrCommandRouterBuilder();
            commands.Register<ResourceContentUpdateCommand>(ResourceCatalogContract.UpdateContent, async (q, _) => { updated = q; return await completion.Task; });
            using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
                fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
            settings.ConfigureOnlineContent(resources.Build(new NoopDispatchObserver()), _ => { }, commands.Build(new NoopDispatchObserver()));
            fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
            var scene = fixture.Shell.Render(new(1000, 900));
            void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
            Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "SettingsNav." + page));
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav." + page).Entity);
            scene = fixture.Shell.Render(new(1000, 900));
            var search = FindByKey(fixture.Shell, scene, "ManagementContentSearch").Entity;
            fixture.Shell.Renderer.Focus(search);
            Pump(() => icons == 1);
            var rowDetails = FindByKey(fixture.Shell, scene, "ManagementContentDetails." + filename).Entity;
            iconReply.SetResult(new(image));
            Pump(() => scene.Nodes.Any(n => n.RasterImage is not null));
            AssertEqual(search, fixture.Shell.Renderer.Focused); AssertEqual(1, batches); AssertEqual(1, icons);
            AssertEqual(rowDetails, FindByKey(fixture.Shell, scene, "ManagementContentDetails." + filename).Entity);
            AssertTrue(scene.Nodes.Any(n => n.Text == "1.0"));
            if (page == "resourcepacks") { AssertTrue(scene.Nodes.Any(n => n.Text == "local")); AssertTrue(scene.Nodes.Any(n => n.Text == "本地描述")); }
            else AssertTrue(scene.Nodes.Any(n => n.Text == "在线中文名称"));
            for (int i = 0; i < 10; i++) fixture.Shell.Render(new(1000, 900));
            AssertEqual(1, batches); AssertEqual(1, icons);
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementContentDetails." + filename).Entity);
            Pump(() => scene.Nodes.Any(n => n.Text == "在线中文名称"));
            if (page == "mods") { AssertFalse(scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "ManagementOnlineUpdate")); continue; }
            AssertTrue(scene.Nodes.Any(n => n.Text == "34")); // Local pack-format metadata is not replaced by an online release.
            var detail = FindByKey(fixture.Shell, scene, "ManagementContentDetail").Entity;
            var detailPage = fixture.Shell.Tree.Parent(detail);
            fixture.Shell.Tree.GetComponent<XsrUiScroll>(detailPage)!.OffsetY = 400;
            fixture.Shell.Tree.MarkDirty(detailPage, XsrUiDirtyKinds.Layout);
            Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "ManagementOnlineUpdate"));
            Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "ManagementOnlineUpdate").Entity);
            Pump(() => updated is not null);
            AssertEqual(instance, updated!.File.InstanceDirectory); AssertEqual(page, updated.File.PageId);
            AssertEqual(filename, updated.File.Name); AssertEqual(10L, updated.File.ExpectedSize); AssertEqual(1L, updated.File.ExpectedModifiedUtcTicks);
            AssertEqual("New", updated.VersionId); AssertEqual(new ResourceReference(ResourceProvider.Modrinth, "A"), updated.Source);
            completion.SetResult(XsrResult.Success()); Pump(() => scans >= 2);
        }
    }
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
