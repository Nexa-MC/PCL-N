using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstanceJavaInventoryWritesOnlySelectedInstance()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
        string home = Path.GetFullPath("instance-java-choice");
        var runtime = new JavaInstallation(home, Path.Combine(home, "bin", "java"), null,
            new Version(21, 0), JavaBrand.EclipseTemurin, JavaArchitecture.X64, true, true);
        queries.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query,
            (_, _) => ValueTask.FromResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([new(runtime)]))));
        string instance = Path.GetFullPath("instance-java-one");
        string? global = fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(v => v.Key == "java.runtime").Value.Value;
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 1700));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 1700));
        var input = FindByKey(fixture.Shell, scene, "SettingsInput.java.runtime").Entity;
        var choice = FindByKey(fixture.Shell, scene, "SettingsJavaChoose.0");
        AssertEqual(XsrUiSemanticRole.RadioButton, choice.Role);
        Emit(fixture.Intents, "ui.settings.java.choose", choice.Entity);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            scene = fixture.Shell.Render(new(1000, 1700));
            return FindByKey(fixture.Shell, scene, "SettingsJavaChoose.0").IsSelected;
        }, TimeSpan.FromSeconds(5)));
        AssertEqual(runtime.JavaExecutablePath, fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(v => v.Key == "java.runtime").Value.Value);
        AssertEqual(global, fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(v => v.Key == "java.runtime").Value.Value);
        AssertEqual(input, FindByKey(fixture.Shell, scene, "SettingsInput.java.runtime").Entity);
        instance = Path.GetFullPath("other-root/instance-java-two");
        scene = fixture.Shell.Render(new(1000, 1700));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.game").Entity);
        scene = fixture.Shell.Render(new(1000, 1700));
        AssertFalse(FindByKey(fixture.Shell, scene, "SettingsJavaChoose.0").IsSelected);
        AssertEqual(global, fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value!.Values.Single(v => v.Key == "java.runtime").Value.Value);
    }

    private static void JavaInventoryPageRetiresLateReadsAndPersistsDefaultSelection()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var pending = new List<(JavaRuntimeInventoryQuery Query, CancellationToken Token, TaskCompletionSource<XsrResult<JavaRuntimeInventorySnapshot>> Completion)>();
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        queries.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query, (query, token) =>
        {
            var completion = new TaskCompletionSource<XsrResult<JavaRuntimeInventorySnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((query, token, completion)); return new(completion.Task);
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 1000));
        void Navigate(string page)
        {
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav." + page).Entity);
            scene = fixture.Shell.Render(new(1000, 1000));
        }
        void PumpUntil(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1000, 1000)); return condition(); }, TimeSpan.FromSeconds(5)));
        JavaRuntimeCandidate Candidate(string name, int major, bool available = true)
        {
            string home = Path.GetFullPath(name);
            return new(new JavaInstallation(home, Path.Combine(home, "bin", "java"), null,
                new Version(major, 0), JavaBrand.EclipseTemurin, JavaArchitecture.X64, true, true), IsAvailable: available);
        }
        long revision = fixture.Foundation.Host.Settings.Revision;
        Navigate("java"); AssertEqual(1, pending.Count); AssertFalse(pending[0].Query.Refresh);
        AssertTrue(scene.Nodes.Any(node => node.Text == "正在扫描 Java…"));
        Navigate("general"); AssertTrue(pending[0].Token.IsCancellationRequested);
        Navigate("java"); AssertEqual(2, pending.Count);
        pending[0].Completion.SetResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([Candidate("stale-inventory", 8)])));
        scene = fixture.Shell.Render(new(1000, 1000));
        AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("stale-inventory", StringComparison.Ordinal) == true));
        var vendor = FindByKey(fixture.Shell, scene, "SettingsSelector.java.vendor").Entity;
        var vendorScroll = fixture.Shell.Tree.GetComponent<XsrUiScroll>(vendor)!;
        vendorScroll.OffsetX = 100;
        fixture.Shell.Tree.MarkDirty(vendor, XsrUiDirtyKinds.Layout);
        var runtimeInput = FindByKey(fixture.Shell, scene, "SettingsInput.java.runtime").Entity;
        fixture.Shell.Renderer.Focus(runtimeInput);
        fixture.Shell.Renderer.SetTextInputValue(runtimeInput, "uncommitted Java draft");
        scene = fixture.Shell.Render(new(1000, 1000));
        double vendorPosition = vendorScroll.OffsetX;
        var selected = Candidate("当前 Java", 21);
        pending[1].Completion.SetResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([selected, Candidate("unavailable-java", 17, false)])));
        PumpUntil(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsJavaChoose.0"));
        AssertEqual(vendor, FindByKey(fixture.Shell, scene, "SettingsSelector.java.vendor").Entity);
        AssertEqual(vendorPosition, vendorScroll.OffsetX);
        AssertEqual(runtimeInput, fixture.Shell.Renderer.Focused);
        AssertEqual("uncommitted Java draft", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(runtimeInput)!.ReadDraft());
        AssertTrue(scene.Nodes.Any(node => node.Text == "Java 21.0 · Temurin · x64"));
        AssertEqual(revision, fixture.Foundation.Host.Settings.Revision);
        AssertFalse(FindByKey(fixture.Shell, scene, "SettingsJavaChoose.1").IsEnabled);
        Emit(fixture.Intents, "ui.settings.java.choose", FindByKey(fixture.Shell, scene, "SettingsJavaChoose.1").Entity);
        scene = fixture.Shell.Render(new(1000, 1000)); AssertEqual(revision, fixture.Foundation.Host.Settings.Revision);
        Emit(fixture.Intents, "ui.settings.java.choose", FindByKey(fixture.Shell, scene, "SettingsJavaChoose.0").Entity);
        PumpUntil(() => FindByKey(fixture.Shell, scene, "SettingsJavaChoose.0").Text == "已选");
        AssertEqual(selected.Installation.JavaExecutablePath,
            new SettingsPolicyService(fixture.Foundation.Host.Settings).Read(new()).Value!.Values.Single(value => value.Key == "java.runtime").Value.Value);
        Emit(fixture.Intents, "ui.settings.java.scan", FindByKey(fixture.Shell, scene, "SettingsJavaScan").Entity);
        scene = fixture.Shell.Render(new(1000, 1000)); AssertEqual(3, pending.Count); AssertTrue(pending[2].Query.Refresh);
        AssertFalse(FindByKey(fixture.Shell, scene, "SettingsJavaScan").IsEnabled);
        pending[2].Completion.SetResult(XsrResult.Failure<JavaRuntimeInventorySnapshot>(new(XsrErrorKind.Rejected,
            XsrSemanticId.Parse("fixture.java.failed"), "private failure detail")));
        PumpUntil(() => scene.Nodes.Any(node => node.Text == "无法扫描 Java，请重试。"));
        AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("private failure detail", StringComparison.Ordinal) == true));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsJavaScan").IsEnabled);
        Emit(fixture.Intents, "ui.settings.java.scan", FindByKey(fixture.Shell, scene, "SettingsJavaScan").Entity);
        scene = fixture.Shell.Render(new(1000, 1000)); AssertEqual(4, pending.Count);
        pending[3].Completion.SetResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([])));
        PumpUntil(() => scene.Nodes.Any(node => node.Text == "未找到可用的 Java。"));
        var retiredScan = FindByKey(fixture.Shell, scene, "SettingsJavaScan").Entity;
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity);
        Emit(fixture.Intents, "ui.settings.java.scan", retiredScan);
        scene = fixture.Shell.Render(new(1000, 1000)); AssertEqual(4, pending.Count);
        Navigate("java"); AssertEqual(5, pending.Count); AssertFalse(pending[4].Query.Refresh);
        pending[4].Completion.SetResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([])));
        PumpUntil(() => scene.Nodes.Any(node => node.Text == "未找到可用的 Java。"));
    }
}
