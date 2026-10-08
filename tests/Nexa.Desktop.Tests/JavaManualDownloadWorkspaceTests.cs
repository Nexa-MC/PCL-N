using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Java;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ManualJavaWorkspaceRequiresLicenseUsesActualReceiptAndRetiresActions()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var settingsQueries = new XsrQueryRouterBuilder();
        fixture.Foundation.Queries.TryResolve(Nexa.Services.Settings.SettingsPolicyContract.CatalogQuery, out var catalog);
        fixture.Foundation.Queries.TryResolve(Nexa.Services.Settings.SettingsPolicyContract.EffectiveQuery, out var effective);
        settingsQueries.Register<Nexa.Services.Settings.SettingsCatalogQuery, Nexa.Services.Settings.SettingsCatalogSnapshot>(Nexa.Services.Settings.SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<Nexa.Services.Settings.SettingsCatalogQuery, Nexa.Services.Settings.SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        settingsQueries.Register<Nexa.Services.Settings.SettingsEffectiveQuery, Nexa.Services.Settings.SettingsEffectiveSnapshot>(Nexa.Services.Settings.SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<Nexa.Services.Settings.SettingsEffectiveQuery, Nexa.Services.Settings.SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        settingsQueries.Register<JavaRuntimeInventoryQuery, JavaRuntimeInventorySnapshot>(JavaRuntimeInventoryContract.Query,
            (_, _) => ValueTask.FromResult(XsrResult.Success(new JavaRuntimeInventorySnapshot([]) { RegistryRevision = 7 })));
        var queries = new XsrQueryRouterBuilder(); var commands = new XsrCommandRouterBuilder();
        List<JavaManualPreviewQuery> previewRequests = []; List<(JavaManualInstallCommand Command, CancellationToken Token)> installs = []; List<Uri> links = [];
        List<(JavaManualStatusQuery Query, CancellationToken Token, TaskCompletionSource<XsrResult<JavaManualReceipt>> Completion)> delayedStatuses = [];
        bool delayStatus = false;
        JavaManualPreview? current = null; JavaManualReceipt? receipt = null;
        queries.Register<JavaManualPreviewQuery, JavaManualPreview>(JavaManualDownloadContract.Preview, (query, _) =>
        {
            previewRequests.Add(query); receipt = null;
            current = new(Guid.NewGuid(), query.Major, "Mojang distribution", "21.0.2", "linux", "/actual/runtime", 2, 10, new string('A', 64), ["https://piston-data.mojang.com/license"], DateTimeOffset.UtcNow.AddMinutes(10));
            return ValueTask.FromResult(XsrResult.Success(current));
        });
        queries.Register<JavaManualStatusQuery, JavaManualReceipt>(JavaManualDownloadContract.Status, (query, token) =>
        {
            if (!delayStatus) return ValueTask.FromResult(XsrResult.Success(receipt!));
            // This provider deliberately finishes after cancellation, exercising consumer ownership.
            var completion = new TaskCompletionSource<XsrResult<JavaManualReceipt>>();
            delayedStatuses.Add((query, token, completion));
            return new(completion.Task);
        });
        commands.Register<JavaManualInstallCommand>(JavaManualDownloadContract.Install, async (command, token) =>
        {
            installs.Add((command, token)); var operationReceipt = new JavaManualReceipt(command.PreviewId, JavaManualInstallStatus.Downloading, .5, 1, 2, "bin/java", "", "", null, null); receipt = operationReceipt;
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { if (receipt?.PreviewId == command.PreviewId) receipt = operationReceipt with { Status = JavaManualInstallStatus.Canceled }; }
            return XsrResult.Success();
        });
        var policyQueries = settingsQueries.Build(new NoopDispatchObserver());
        var manualQueries = queries.Build(new NoopDispatchObserver()); var manualCommands = commands.Build(new NoopDispatchObserver());
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, policyQueries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        settings.ConfigureJavaManualDownload(manualQueries, manualCommands, links.Add);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 3000));
        void PumpUntil(Func<bool> predicate) => AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 3000)); return predicate(); }, TimeSpan.FromSeconds(5)));
        bool Has(string name) => scene.Nodes.Any(node => fixture.Shell.Tree.Name(node.Entity) == name);
        bool Enabled(string name) => Has(name) && fixture.Shell.Tree.GetComponent<XsrUiInput>(FindByKey(fixture.Shell, scene, name).Entity)?.Enabled == true;
        void Click(string name) { Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, name).Entity); scene = fixture.Shell.Render(new(1000, 3000)); }
        void Navigate(string name) { Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav." + name).Entity); scene = fixture.Shell.Render(new(1000, 3000)); }
        Navigate("java"); PumpUntil(() => Has("JavaManual.Preview"));
        var root = FindByKey(fixture.Shell, scene, "JavaManual.Root").Entity;
        for (int index = 0; index < 5; index++) scene = fixture.Shell.Render(new(1000, 3000));
        AssertEqual(root, FindByKey(fixture.Shell, scene, "JavaManual.Root").Entity);
        Click("JavaManual.Preview"); PumpUntil(() => Has("JavaManual.Install")); AssertEqual(new JavaManualPreviewQuery(21, 7), previewRequests.Single());
        Click("JavaManual.License.0"); AssertEqual("https://piston-data.mojang.com/license", links.Single().AbsoluteUri);
        Click("JavaManual.Install"); var dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(dialog.Message.Contains("/actual/runtime", StringComparison.Ordinal)); AssertEqual(0, installs.Count);
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, false)); scene = fixture.Shell.Render(new(1000, 3000)); AssertEqual(0, installs.Count);
        Click("JavaManual.Install"); dialog = fixture.Feedback.Snapshot().Dialog!; AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true));
        PumpUntil(() => Has("JavaManual.Cancel")); AssertTrue(installs.Single().Command.AcceptLicense); AssertEqual(current!.Id, installs.Single().Command.PreviewId);
        Click("JavaManual.Cancel"); AssertTrue(installs.Single().Token.IsCancellationRequested);
        PumpUntil(() => scene.Nodes.Any(node => node.Text == "此 Java 下载已取消，恢复记录已保留。"));
        Click("JavaManual.Preview"); PumpUntil(() => Has("JavaManual.Install")); Click("JavaManual.Install"); dialog = fixture.Feedback.Snapshot().Dialog!;
        var retired = FindByKey(fixture.Shell, scene, "JavaManual.Install").Entity; Navigate("general");
        AssertTrue(fixture.Feedback.Snapshot().Dialog is null); AssertFalse(fixture.Feedback.ResolveDialog(dialog.Id, true));
        Emit(fixture.Intents, "ui.settings.management.action", retired); scene = fixture.Shell.Render(new(1000, 3000)); AssertEqual(1, installs.Count);
        Navigate("java"); PumpUntil(() => Has("JavaManual.Preview")); AssertFalse(Has("JavaManual.Install")); AssertEqual(1, installs.Count);

        // The instance's merged Game/Java page uses the same shared managed install workflow.
        settings.Dispose(); string instance = Path.GetFullPath("manual-java-instance-one");
        string? preferred = fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value.Values.Single(value => value.Key == "java.runtime").Value.Value;
        using var versionSettings = new SettingsPageController(fixture.Shell, fixture.Intents, policyQueries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        versionSettings.ConfigureJavaManualDownload(manualQueries, manualCommands, links.Add);
        fixture.Shell.Stage.Navigation.Replace(versionSettings.Page); scene = fixture.Shell.Render(new(1000, 3000));
        Navigate("game"); PumpUntil(() => Has("JavaManual.Preview"));
        Click("JavaManual.Preview"); PumpUntil(() => Has("JavaManual.Install")); Click("JavaManual.Install"); dialog = fixture.Feedback.Snapshot().Dialog!;
        instance = Path.GetFullPath("manual-java-instance-two"); scene = fixture.Shell.Render(new(1000, 3000));
        AssertTrue(fixture.Feedback.Snapshot().Dialog is null); AssertFalse(fixture.Feedback.ResolveDialog(dialog.Id, true)); AssertEqual(1, installs.Count);
        AssertFalse(Has("JavaManual.Preview"));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsNav.overview").IsSelected);
        Navigate("game");
        PumpUntil(() => Has("JavaManual.Preview")); Click("JavaManual.Preview"); PumpUntil(() => Has("JavaManual.Install")); Click("JavaManual.Install");
        dialog = fixture.Feedback.Snapshot().Dialog!; AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true)); PumpUntil(() => installs.Count == 2);
        AssertEqual(preferred, fixture.Foundation.Host.SettingsPolicy.Read(new(instance)).Value.Values.Single(value => value.Key == "java.runtime").Value.Value);
        instance = Path.GetFullPath("manual-java-instance-three"); scene = fixture.Shell.Render(new(1000, 3000)); AssertTrue(installs[1].Token.IsCancellationRequested);
        AssertFalse(Has("JavaManual.Preview"));
        AssertTrue(FindByKey(fixture.Shell, scene, "SettingsNav.overview").IsSelected);
        Navigate("game");
        PumpUntil(() => Has("JavaManual.Preview")); AssertFalse(Has("JavaManual.Install")); AssertEqual(2, installs.Count);
        Click("JavaManual.Preview"); PumpUntil(() => Has("JavaManual.Install")); Click("JavaManual.Install"); dialog = fixture.Feedback.Snapshot().Dialog!;
        AssertTrue(fixture.Feedback.ResolveDialog(dialog.Id, true)); PumpUntil(() => installs.Count == 3);
        instance = ""; scene = fixture.Shell.Render(new(1000, 3000)); AssertTrue(installs[2].Token.IsCancellationRequested); AssertFalse(Has("JavaManual.Preview"));

        // A late status response from a completed/canceled install cannot attach to a new preview.
        using var raceSettings = new SettingsPageController(fixture.Shell, fixture.Intents, policyQueries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        raceSettings.ConfigureJavaManualDownload(manualQueries, manualCommands, links.Add);
        fixture.Shell.Stage.Navigation.Replace(raceSettings.Page); scene = fixture.Shell.Render(new(1000, 3000));
        Navigate("java"); PumpUntil(() => Has("JavaManual.Preview")); delayStatus = true;
        AssertDelayedStatusRetired(changeMajor: true, expectedInstalls: 4);
        AssertDelayedStatusRetired(changeMajor: false, expectedInstalls: 5);

        void AssertDelayedStatusRetired(bool changeMajor, int expectedInstalls)
        {
            int statusCount = delayedStatuses.Count;
            Click("JavaManual.Preview"); PumpUntil(() => Enabled("JavaManual.Install"));
            Guid oldPreview = current!.Id;
            Click("JavaManual.Install"); var licenseDialog = fixture.Feedback.Snapshot().Dialog!;
            AssertTrue(fixture.Feedback.ResolveDialog(licenseDialog.Id, true));
            PumpUntil(() => installs.Count == expectedInstalls && delayedStatuses.Count == statusCount + 1 && Has("JavaManual.Cancel"));
            var pending = delayedStatuses[statusCount];
            AssertEqual(oldPreview, pending.Query.PreviewId);
            AssertFalse(installs[^1].Token.IsCancellationRequested);
            int previewCount = previewRequests.Count;
            Click("JavaManual.Major.17"); Click("JavaManual.Preview");
            AssertEqual(previewCount, previewRequests.Count); AssertEqual(oldPreview, current!.Id);
            AssertFalse(installs[^1].Token.IsCancellationRequested);
            Click("JavaManual.Cancel"); PumpUntil(() => Enabled("JavaManual.Preview") && !Has("JavaManual.Cancel"));
            AssertTrue(installs[^1].Token.IsCancellationRequested);
            AssertFalse(pending.Token.IsCancellationRequested); // Install cancellation preserves terminal-status observation.
            if (changeMajor) Click("JavaManual.Major.17");
            Click("JavaManual.Preview"); PumpUntil(() => Enabled("JavaManual.Install"));
            AssertTrue(pending.Token.IsCancellationRequested); AssertFalse(current!.Id == oldPreview);
            AssertEqual(17, previewRequests[^1].Major);
            var lateReceipt = new JavaManualReceipt(oldPreview, JavaManualInstallStatus.Installed, 1, 2, 2,
                "retired-manual-java-receipt", "/retired/runtime/bin/java", "retired-version", null, null);
            AssertTrue(pending.Completion.TrySetResult(XsrResult.Success(lateReceipt)));
            for (int frame = 0; frame < 8; frame++) scene = fixture.Shell.Render(new(1000, 3000));
            AssertTrue(Enabled("JavaManual.Install"));
            AssertFalse(scene.Nodes.Any(node => node.Text is "retired-manual-java-receipt" or "/retired/runtime/bin/java" or "retired-version"));
            AssertFalse(Has("JavaManual.Refresh"));
        }
    }
}
