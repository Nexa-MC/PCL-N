using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void FileWorkspaceRequiresPreviewBeforeExplicitSaveAndPreservesOtherLines()
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
                [new("overview", "总览"), new("game", "游戏设置"), new("files", "文件工作区")], true, ""))));
        queries.Register<InstanceFileListQuery, InstanceFileListing>(InstanceFileWorkspaceContract.List, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceFileListing(q.InstanceDirectory, q.Area, q.RelativeDirectory, "config", [new("sample.cfg", false, 17, 1)], true))));
        queries.Register<InstanceFileReadQuery, InstanceFileDocument>(InstanceFileWorkspaceContract.Read, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceFileDocument(q, "a=1\r\nb=keep\r\n", "old", "utf8", true))));
        InstanceFileSavePreviewQuery? previewed = null;
        queries.Register<InstanceFileSavePreviewQuery, InstanceFileSavePreview>(InstanceFileWorkspaceContract.Preview, (q, _) =>
        {
            previewed = q;
            return ValueTask.FromResult(XsrResult.Success(new InstanceFileSavePreview(q.Original, q.Text, "new", 17, 17, 3, 3)));
        });
        InstanceFileSaveCommand? saved = null;
        var completion = new TaskCompletionSource<XsrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new XsrCommandRouterBuilder(); commands.Register<InstanceFileSaveCommand>(InstanceFileWorkspaceContract.Save,
            async (q, _) => { saved = q; return await completion.Task; });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), commands.Build(new NoopDispatchObserver()),
            fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1100, 1200));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1100, 1200)); return condition(); }, TimeSpan.FromSeconds(5)));
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.files"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.files").Entity);
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "FileWorkspaceRow.sample.cfg"));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.查看文本").Entity);
        Pump(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "FileWorkspaceLineEditor"));
        AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "Management.确认保存配置"));
        var editor = FindByKey(fixture.Shell, scene, "FileWorkspaceLineEditor").Entity;
        fixture.Shell.Renderer.SetTextInputValue(editor, "a=2");
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.预览保存").Entity);
        Pump(() => previewed is not null && scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "Management.确认保存配置"));
        AssertEqual("a=2\r\nb=keep\r\n", previewed!.Text); AssertTrue(saved is null);
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.确认保存配置").Entity);
        Pump(() => saved is not null); AssertEqual(previewed.Text, saved!.Preview.Text);
        AssertEqual("instance-A", saved.Preview.Original.File.InstanceDirectory); AssertEqual("config", saved.Preview.Original.File.Area);
        instance = "instance-B"; Pump(() => settings.SelectedSection == "overview"); completion.SetResult(XsrResult.Success());
        scene = fixture.Shell.Render(new(1100, 1200)); AssertEqual("overview", settings.SelectedSection);
    }
}
