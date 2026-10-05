using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ManagedJavaDeletionConfirmsCapturedIdentityAndRetiresOnNavigation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (q, ct) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
        string home = Path.GetFullPath("managed-java-settings");
        string executable = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var candidate = new JavaRuntimeCandidate(new JavaInstallation(home, executable, null,
            new Version(21, 0), JavaBrand.Microsoft, JavaArchitecture.X64, true, true));
        string externalHome = Path.GetFullPath("external-java-settings");
        string external = Path.Combine(externalHome, "bin", "java");
        var externalCandidate = new JavaRuntimeCandidate(new JavaInstallation(externalHome, external, null,
            new Version(17, 0), JavaBrand.Microsoft, JavaArchitecture.X64, true, true));
        string identity = new('A', 64);
        var snapshot = new JavaRuntimeInventorySnapshot([candidate, externalCandidate])
        { RegistryRevision = 27, ManagedRuntimes = [new(executable, home, identity)], Registrations = [new(external)] };
        queries.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query,
            (_, _) => ValueTask.FromResult(XsrResult.Success(snapshot)));
        List<JavaRuntimeManageCommand> writes = [];
        var commands = new XsrCommandRouterBuilder();
        commands.Register<JavaRuntimeManageCommand>(JavaRuntimeInventoryContract.Manage,
            (command, _) => { writes.Add(command); return ValueTask.FromResult(XsrResult.Success()); });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            commands.Build(new NoopDispatchObserver()), fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1200, 2100));
        void Navigate(string page)
        {
            Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav." + page).Entity);
            scene = fixture.Shell.Render(new(1200, 2100)); scene = fixture.Shell.Render(new(1200, 2100));
        }
        void Delete()
        {
            var button = FindByKey(fixture.Shell, scene, "SettingsJavaDelete." + executable);
            AssertTrue(button.IsEnabled && button.IsClickable);
            var visible = button.ClipRect ?? button.Rect;
            AssertEqual(button.Entity, fixture.Shell.Renderer.HitTest(new(visible.X + visible.Width / 2, visible.Y + visible.Height / 2)));
            AssertTrue(fixture.Shell.Renderer.Activate(button.Entity));
            // Feedback reconciliation runs before the settings intent handler on each frame.
            AssertTrue(SpinWait.SpinUntil(() =>
            {
                scene = fixture.Shell.Render(new(1200, 2100));
                return scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "DialogMessage");
            }, TimeSpan.FromSeconds(5)));
            AssertTrue(FindByKey(fixture.Shell, scene, "DialogMessage").Text!.Contains(home, StringComparison.Ordinal));
        }
        Navigate("java");
        AssertFalse(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsJavaDelete." + external));
        AssertTrue(scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == "SettingsJavaRemove." + external));
        Delete(); AssertTrue(fixture.Shell.Renderer.HandleKey(XsrUiKey.Escape));
        scene = fixture.Shell.Render(new(1200, 2100)); AssertEqual(0, writes.Count);
        Delete();
        // Navigation retires the confirmation's captured inventory generation.
        Navigate("general");
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "DialogAccept").Entity));
        scene = fixture.Shell.Render(new(1200, 2100)); AssertEqual(0, writes.Count);
        Navigate("java"); Delete();
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "DialogAccept").Entity));
        AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1200, 2100)); return writes.Count == 1; }, TimeSpan.FromSeconds(5)));
        var command = writes.Single();
        AssertEqual(JavaRuntimeManagementAction.DeleteManaged, command.Action);
        AssertEqual(executable, command.Executable); AssertEqual(27L, command.ExpectedRevision); AssertEqual(identity, command.ExpectedManagedIdentity);
    }
}
