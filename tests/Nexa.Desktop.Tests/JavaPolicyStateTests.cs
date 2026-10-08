using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static async ValueTask JavaPolicyStateFollowsCommittedPreferenceAndInheritanceWithoutRebuildingEditors()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        AssertTrue(fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog));
        AssertTrue(fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective));
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        queries.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query,
            (_, _) => ValueTask.FromResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([]))));
        var queryRouter = queries.Build(new NoopDispatchObserver());
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queryRouter,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 2800));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.java").Entity);
        scene = fixture.Shell.Render(new(1000, 2800));
        const string automatic = "当前使用自动选择策略。";
        const string preferred = "已配置首选 Java 路径，启动时仍会验证兼容性。";
        void PumpUntil(string caption) => AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 2800));
            return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "JavaPolicy.Current" && node.Text == caption);
        }, TimeSpan.FromSeconds(5)));
        PumpUntil(automatic);
        XsrUiEntityId card = FindByKey(fixture.Shell, scene, "JavaPolicy").Entity;
        XsrUiEntityId status = FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Entity;
        XsrUiEntityId input = FindByKey(fixture.Shell, scene, "SettingsInput.java.runtime").Entity;
        fixture.Shell.Renderer.Focus(input);
        fixture.Shell.Renderer.SetTextInputValue(input, "uncommitted Java draft");
        AssertTrue(fixture.Foundation.Commands.TryResolve(SettingsPolicyContract.SetCommand, out var set));
        string executable = Path.GetFullPath("private-preferred-java/bin/java");
        AssertTrue((await fixture.Foundation.Commands.Dispatch(set,
            new SettingsMutation("java.runtime", SettingsLayer.Global, new(SettingsOverrideMode.Custom, executable))).Completion).IsSuccess);
        PumpUntil(preferred);
        AssertEqual(executable, fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "java.runtime").Value.Value);
        AssertEqual(card, FindByKey(fixture.Shell, scene, "JavaPolicy").Entity);
        AssertEqual(status, FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Entity);
        AssertEqual(input, fixture.Shell.Renderer.Focused);
        AssertEqual("uncommitted Java draft", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        AssertEqual(preferred, fixture.Shell.Tree.GetComponent<XsrUiSemantic>(status)!.Label);
        AssertFalse(FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Text!.Contains(executable, StringComparison.Ordinal));
        AssertTrue((await fixture.Foundation.Commands.Dispatch(set,
            new SettingsMutation("java.runtime", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).Completion).IsSuccess);
        PumpUntil(automatic);
        AssertEqual(status, FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Entity);
        AssertEqual(input, fixture.Shell.Renderer.Focused);
        AssertEqual("uncommitted Java draft", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
        long revision = fixture.Foundation.Host.Settings.Revision;
        for (int frame = 0; frame < 8; frame++) scene = fixture.Shell.Render(new(1000, 2800));
        AssertEqual(revision, fixture.Foundation.Host.Settings.Revision);
        AssertEqual(status, FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Entity);
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity);
        scene = fixture.Shell.Render(new(1000, 2800));
        AssertTrue((await fixture.Foundation.Commands.Dispatch(set,
            new SettingsMutation("java.runtime", SettingsLayer.Global, new(SettingsOverrideMode.Custom, executable))).Completion).IsSuccess);
        scene = fixture.Shell.Render(new(1000, 2800));
        AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "JavaPolicy.Current"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.java").Entity);
        PumpUntil(preferred);

        string instance = Path.GetFullPath("merged-instance-java-policy");
        using var scopedSettings = new SettingsPageController(fixture.Shell, fixture.Intents, queryRouter,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Stage.Navigation.Replace(scopedSettings.Page);
        scene = fixture.Shell.Render(new(1000, 2800));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        PumpUntil(preferred);
        AssertEqual("game", scopedSettings.SelectedSection);
        AssertTrue(scene.Nodes.Any(node => node.Text == "每次启动都会检查所选运行时；不兼容的手动选择会明确拒绝，不会静默换用其他 Java。"));
        status = FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Entity;
        input = FindByKey(fixture.Shell, scene, "SettingsInput.java.runtime").Entity;
        fixture.Shell.Renderer.Focus(input);
        fixture.Shell.Renderer.SetTextInputValue(input, "uncommitted instance Java draft");
        AssertTrue((await fixture.Foundation.Commands.Dispatch(set,
            new SettingsMutation("java.runtime", SettingsLayer.Instance, new(SettingsOverrideMode.Auto), instance)).Completion).IsSuccess);
        PumpUntil(automatic);
        AssertEqual(SettingsOverrideMode.Auto, fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(value => value.Key == "java.runtime").Value.Mode);
        AssertEqual(executable, fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(value => value.Key == "java.runtime").Value.Value);
        AssertTrue((await fixture.Foundation.Commands.Dispatch(set,
            new SettingsMutation("java.runtime", SettingsLayer.Instance, new(SettingsOverrideMode.Inherit), instance)).Completion).IsSuccess);
        PumpUntil(preferred);
        AssertEqual(executable, fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(value => value.Key == "java.runtime").Value.Value);
        AssertEqual(status, FindByKey(fixture.Shell, scene, "JavaPolicy.Current").Entity);
        AssertEqual(input, fixture.Shell.Renderer.Focused);
        AssertEqual("uncommitted instance Java draft", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft());
    }
}
