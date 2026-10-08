using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void JavaDiagnosticWorkspaceDispatchesSelectedRevisionCopiesPreviewAndRetiresReads()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        JavaRuntimeCandidate Candidate(string name, bool available = true)
        {
            string home = Path.GetFullPath(name);
            return new(new JavaInstallation(home, Path.Combine(home, "bin", "java"), null,
                new Version(21, 0, 1), JavaBrand.EclipseTemurin, JavaArchitecture.X64, true, false), IsAvailable: available);
        }
        var first = Candidate("diagnostics-first"); var second = Candidate("diagnostics-second");
        queries.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query,
            (_, _) => ValueTask.FromResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([first, second, Candidate("unavailable-diagnostics", false)]) { RegistryRevision = 17 })));
        var properties = new List<(JavaRuntimePropertiesQuery Query, CancellationToken Token, TaskCompletionSource<XsrResult<JavaRuntimeDiagnosticsSnapshot>> Completion)>();
        queries.Register<JavaRuntimePropertiesQuery, JavaRuntimeDiagnosticsSnapshot>(JavaRuntimeDiagnosticsContract.Properties, (query, token) =>
        {
            var completion = new TaskCompletionSource<XsrResult<JavaRuntimeDiagnosticsSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            properties.Add((query, token, completion)); return new(completion.Task);
        });
        List<JavaRuntimeModulesQuery> modules = [];
        queries.Register<JavaRuntimeModulesQuery, JavaRuntimeDiagnosticsSnapshot>(JavaRuntimeDiagnosticsContract.Modules, (query, _) =>
        {
            modules.Add(query);
            return ValueTask.FromResult(XsrResult.Success(new JavaRuntimeDiagnosticsSnapshot(JavaRuntimeDiagnosticStatus.Available,
                query.Executable, "sha-fixture", null, [new("java.base", "21.0.1")], "java.base@21.0.1", 16)));
        });
        string? copied = null;
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback)
        { CopyJavaDiagnosticsTextAsync = text => { copied = text; return Task.CompletedTask; } };
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 2800));
        void Navigate(string page)
        {
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav." + page).Entity);
            scene = fixture.Shell.Render(new(1000, 2800));
        }
        void PumpUntil(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1000, 2800)); return condition(); }, TimeSpan.FromSeconds(5)));
        void Click(string key) => Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, key).Entity);
        Navigate("java"); PumpUntil(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "JavaDiagnostics.Properties"));
        AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "JavaDiagnostics.Select.2"));
        var ownedRoot = FindByKey(fixture.Shell, scene, "JavaDiagnostics.Root").Entity;
        for (int index = 0; index < 8; index++) scene = fixture.Shell.Render(new(1000, 2800));
        AssertEqual(ownedRoot, FindByKey(fixture.Shell, scene, "JavaDiagnostics.Root").Entity);
        AssertEqual(1, scene.Nodes.Count(node => fixture.Shell.Tree.Name(node.Entity) == "JavaDiagnostics.Properties"));
        Click("JavaDiagnostics.Select.1"); scene = fixture.Shell.Render(new(1000, 2800));
        AssertEqual(ownedRoot, FindByKey(fixture.Shell, scene, "JavaDiagnostics.Root").Entity);
        AssertEqual(1, scene.Nodes.Count(node => fixture.Shell.Tree.Name(node.Entity) == "JavaDiagnostics.Properties"));
        Click("JavaDiagnostics.Properties"); scene = fixture.Shell.Render(new(1000, 2800));
        AssertEqual(new JavaRuntimePropertiesQuery(second.Installation.JavaExecutablePath, 17), properties.Single().Query);
        AssertFalse(FindByKey(fixture.Shell, scene, "JavaDiagnostics.Properties").IsEnabled);
        AssertFalse(FindByKey(fixture.Shell, scene, "JavaDiagnostics.Modules").IsEnabled);
        properties[0].Completion.SetResult(XsrResult.Success(new JavaRuntimeDiagnosticsSnapshot(JavaRuntimeDiagnosticStatus.Available,
            second.Installation.JavaExecutablePath, "sha-fixture", new("21.0.1", "Eclipse Adoptium", "amd64", "OpenJDK VM", "21.0.1", 21, true), [], "user.home = <redacted>", 22)));
        PumpUntil(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "JavaDiagnostics.Copy"));
        AssertTrue(scene.Nodes.Any(node => node.Text == "Eclipse Adoptium"));
        Click("JavaDiagnostics.Copy"); scene = fixture.Shell.Render(new(1000, 2800));
        AssertEqual("user.home = <redacted>", copied);
        Click("JavaDiagnostics.Modules"); PumpUntil(() => scene.Nodes.Any(node => node.Text == "java.base@21.0.1"));
        AssertEqual(new JavaRuntimeModulesQuery(second.Installation.JavaExecutablePath, 17), modules.Single());
        Click("JavaDiagnostics.Properties"); scene = fixture.Shell.Render(new(1000, 2800));
        var retired = FindByKey(fixture.Shell, scene, "JavaDiagnostics.Properties").Entity;
        Navigate("general"); AssertTrue(properties[1].Token.IsCancellationRequested);
        AssertFalse(fixture.Shell.Tree.IsAlive(ownedRoot));
        properties[1].Completion.SetResult(XsrResult.Success(new JavaRuntimeDiagnosticsSnapshot(JavaRuntimeDiagnosticStatus.Available,
            second.Installation.JavaExecutablePath, "retired-fingerprint", null, [], "retired-private-preview", 23)));
        Emit(fixture.Intents, "ui.settings.management.action", retired);
        Navigate("java"); PumpUntil(() => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "JavaDiagnostics.Properties"));
        AssertEqual(2, properties.Count);
        AssertFalse(scene.Nodes.Any(node => node.Text == "retired-private-preview" || node.Text == "retired-fingerprint"));
    }
}
