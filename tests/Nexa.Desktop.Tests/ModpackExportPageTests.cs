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
    private static void ModpackExportPagePreservesFieldsAndDispatchesSelectedScope()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        const string instance = "instance-A";
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, q.InstanceDirectory, "1.21.1", [],
                [new("overview", "总览"), new("game", "游戏设置"), new("modpack", "整合包与导出")], true, ""))));
        queries.Register<InstanceModpackExportQuery, InstanceModpackExportPreview>(InstanceModpackExportContract.Preview, (q, _) =>
            ValueTask.FromResult(XsrResult.Success(new InstanceModpackExportPreview(instance, [new("mods", "模组", 1, 100), new("saves", "存档", 1, 100)]))));
        var commands = new XsrCommandRouterBuilder(); InstanceModpackExportCommand? saved = null;
        commands.Register<InstanceModpackExportCommand>(InstanceModpackExportContract.Export, (c, _) =>
        { saved = c; return ValueTask.FromResult(XsrResult.Success()); });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
        void Click(string name, string intent) => Emit(fixture.Intents, intent, FindByKey(fixture.Shell, scene, name).Entity);
        Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "SettingsNav.modpack"));
        Click("SettingsNav.modpack", "ui.settings.section"); Pump(() => scene.Nodes.Any(n => fixture.Shell.Tree.Name(n.Entity) == "Management.导出整合包"));
        fixture.Shell.Renderer.SetTextInputValue(FindByKey(fixture.Shell, scene, "ExportName").Entity, "My Pack");
        fixture.Shell.Renderer.SetTextInputValue(FindByKey(fixture.Shell, scene, "ExportVersion").Entity, "3.2");
        fixture.Shell.Renderer.SetTextInputValue(FindByKey(fixture.Shell, scene, "ExportPath").Entity, Path.Combine(Path.GetTempPath(), "test.mrpack"));
        var nameEntity = FindByKey(fixture.Shell, scene, "ExportName").Entity;
        fixture.Shell.Renderer.Focus(nameEntity);
        var saves = FindByKey(fixture.Shell, scene, "ExportCategoryChoice.saves");
        AssertEqual(XsrUiSemanticRole.CheckBox, saves.Role); AssertEqual(false, saves.IsChecked);
        Click("ExportCategoryChoice.saves", "ui.settings.management.action");
        Pump(() => FindByKey(fixture.Shell, scene, "ExportCategoryChoice.saves").IsChecked == true);
        AssertEqual(nameEntity, FindByKey(fixture.Shell, scene, "ExportName").Entity);
        AssertEqual(nameEntity, fixture.Shell.Renderer.Focused);
        AssertEqual("My Pack", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(FindByKey(fixture.Shell, scene, "ExportName").Entity)!.ReadDraft());
        Click("Management.导出整合包", "ui.settings.management.action"); Pump(() => saved is not null);
        AssertEqual(instance, saved!.InstanceDirectory); AssertEqual("My Pack", saved.Name); AssertEqual("3.2", saved.Version);
        AssertTrue(saved.Categories.SequenceEqual(["mods", "saves"]));
    }
}
