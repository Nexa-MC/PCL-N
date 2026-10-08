using System.Net;
using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Logging;
using Nexa.Services.Settings;
using Nexa.Services.Setup;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static async ValueTask DiagnosticAiWorkspaceRebuildsCommittedAdmissionAndCancelsRetiredSend()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        UiLocalizationCatalog localization = new(); localization.SetLanguage("en"); fixture.Shell.Renderer.TextLocalizer = localization.Translate;
        string directory = Path.Combine(fixture.TemporaryDirectory, "ai-workspace"); Directory.CreateDirectory(directory);
        using var storage = new StoragePreferencesService(new(directory), Path.Combine(fixture.TemporaryDirectory, "storage.json"));
        var backups = new ContentBackupService(Path.Combine(directory, "backups"));
        await using var history = new DurableDiagnosticHistorySink(Path.Combine(directory, "history"));
        using var handler = new DiagnosticAiWorkspaceCancellationHandler(); using var http = new HttpClient(handler);
        var ai = new DiagnosticAiService(http) { IsEnabled = () => fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(x => x.Key == "diagnostics.ai.enabled").Value.Value == "true" };
        var runtime = FoundationRuntimeComposer.ComposeWithStorageAndMedia(fixture.Foundation.Host, storage,
            static (pixels, _, _, _, _) => pixels.ToArray(),
            configureRoutes: (commands, queries) => ContentWorkspaceRuntime.Register(commands, queries, backups, new LegacyMigrationService(), history, ai));
        using var controller = new SettingsPageController(fixture.Shell, fixture.Intents, runtime.Queries, runtime.Commands, fixture.Store, fixture.Feedback);
        const string rawBody = "用户正文第一行\n返回\n设置";
        controller.ConfigureContentWorkspace(_ => Task.FromResult<string?>(directory), _ => Task.FromResult<string?>(null), static () => rawBody);
        controller.ConfigureDiagnosticAi(true);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(controller.Page);
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 650)); return HasKey(fixture.Shell, scene, "SettingsNav.storage"); }, TimeSpan.FromSeconds(10)));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.storage").Entity);
        XsrUiEntityId Get(string key)
        {
            XsrUiEntityId entity = default; fixture.Shell.Tree.Walk(controller.Page, current => { if (fixture.Shell.Tree.Name(current) == key) entity = current; return true; }); return entity;
        }
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Get("SettingsOption.diagnostics.ai.enabled.true").IsAssigned; }, TimeSpan.FromSeconds(10)));
        AssertFalse(Get("DiagnosticAi.endpoint").IsAssigned);
        var enabledSource = Get("SettingsOption.diagnostics.ai.enabled.true");
        Emit(fixture.Intents, "ui.settings.choice", enabledSource);
        bool opened = SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Get("DiagnosticAi.endpoint").IsAssigned; }, TimeSpan.FromSeconds(10));
        if (!opened)
        {
            var committed = fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!;
            string admission = committed.Values.Single(x => x.Key == "diagnostics.ai.enabled").Value.Value!;
            var currentTrue = Get("SettingsOption.diagnostics.ai.enabled.true");
            throw new InvalidOperationException("AI admission did not rebuild the provider fields after the typed choice: committed=" + admission
                + "; revision=" + committed.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "; storeRevision=" + fixture.Store.Read<long>(fixture.Store.Resolve(SettingsPolicyContract.RevisionKey)).Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "; clickedSourceAlive=" + fixture.Shell.Tree.IsAlive(enabledSource)
                + "; currentTrueSelected=" + (currentTrue.IsAssigned && fixture.Shell.Tree.GetComponent<XsrUiSelection>(currentTrue)?.IsSelected == true)
                + "; feedback=" + string.Join(" | ", fixture.Feedback.Snapshot().Notifications.Select(x => x.Message)));
        }
        AssertTrue(opened);
        fixture.Shell.Renderer.SetTextInputValue(Get("DiagnosticAi.endpoint"), "https://provider.example/v1/chat/completions");
        fixture.Shell.Renderer.SetTextInputValue(Get("DiagnosticAi.model"), "返回");
        fixture.Shell.Renderer.SetTextInputValue(Get("DiagnosticAi.key"), "ui-ephemeral-key");
        Emit(fixture.Intents, "ui.settings.choice", Get("SettingsOption.diagnostics.ai.reasoning.medium"));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(x => x.Key == "diagnostics.ai.reasoning").Value.Value == "medium"
                && fixture.Shell.Tree.GetComponent<XsrUiSelection>(Get("SettingsOption.diagnostics.ai.reasoning.medium"))!.IsSelected
                && Get("ContentWorkspace.AiFacts").IsAssigned && fixture.Shell.Tree.GetComponent<XsrUiInput>(Get("ContentWorkspace.AiFacts"))!.Enabled;
        }, TimeSpan.FromSeconds(10)));
        Emit(fixture.Intents, "ui.settings.storage.workspace", Get("ContentWorkspace.Raw"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(10)));
        var exportPreview = fixture.Feedback.Snapshot().Dialog!;
        AssertFalse(exportPreview.LocalizeMessage); AssertTrue(exportPreview.Message.StartsWith("Export ", StringComparison.Ordinal));
        AssertTrue(exportPreview.Message.Contains("bytes of redacted text", StringComparison.Ordinal)); AssertTrue(exportPreview.Message.EndsWith(rawBody, StringComparison.Ordinal));
        scene = fixture.Shell.Render(new(1000, 650));
        var exportMessage = FindByKey(fixture.Shell, scene, "DialogMessage");
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(exportMessage.Entity)!.Localize); AssertTrue(exportMessage.Text!.EndsWith(rawBody, StringComparison.Ordinal));
        fixture.Feedback.ResolveDialog(exportPreview.Id, false);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return fixture.Feedback.Snapshot().Dialog is null && Get("ContentWorkspace.AiRaw").IsAssigned
                && fixture.Shell.Tree.GetComponent<XsrUiInput>(Get("ContentWorkspace.AiRaw"))!.Enabled;
        }, TimeSpan.FromSeconds(10)));
        AssertFalse(Directory.EnumerateFiles(directory, "nexa-raw-*.txt").Any()); AssertEqual(0, handler.Calls);
        Emit(fixture.Intents, "ui.settings.storage.workspace", Get("ContentWorkspace.AiRaw"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(10)));
        var rawPreview = fixture.Feedback.Snapshot().Dialog!;
        AssertFalse(rawPreview.LocalizeMessage); AssertTrue(rawPreview.Message.Contains("Redacted raw log text", StringComparison.Ordinal));
        AssertTrue(rawPreview.Message.Contains("using model 返回", StringComparison.Ordinal)); AssertTrue(rawPreview.Message.EndsWith(rawBody, StringComparison.Ordinal));
        scene = fixture.Shell.Render(new(1000, 650));
        var renderedPreview = FindByKey(fixture.Shell, scene, "DialogMessage");
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(renderedPreview.Entity)!.Localize);
        AssertTrue(renderedPreview.Text!.EndsWith(rawBody, StringComparison.Ordinal)); AssertEqual(0, handler.Calls);
        fixture.Feedback.ResolveDialog(rawPreview.Id, false);
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return fixture.Feedback.Snapshot().Dialog is null && Get("ContentWorkspace.AiFacts").IsAssigned
                && fixture.Shell.Tree.GetComponent<XsrUiInput>(Get("ContentWorkspace.AiFacts"))!.Enabled;
        }, TimeSpan.FromSeconds(10)));
        Emit(fixture.Intents, "ui.settings.storage.workspace", Get("ContentWorkspace.AiFacts"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return fixture.Feedback.Snapshot().Dialog is not null; }, TimeSpan.FromSeconds(10)));
        AssertEqual(0, handler.Calls); var preview = fixture.Feedback.Snapshot().Dialog!;
        AssertFalse(preview.LocalizeMessage); AssertTrue(preview.Message.Contains("Structured operation facts without message bodies", StringComparison.Ordinal));
        AssertTrue(preview.Message.Contains("Medium", StringComparison.Ordinal)); AssertFalse(preview.Message.Contains("ui-ephemeral-key", StringComparison.Ordinal));
        fixture.Feedback.ResolveDialog(preview.Id, true);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return handler.Calls == 1; }, TimeSpan.FromSeconds(10)));
        Emit(fixture.Intents, "ui.settings.choice", Get("SettingsOption.diagnostics.ai.enabled.false"));
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            fixture.Shell.Render(new(1000, 650));
            return handler.Cancellations == 1 && !Get("DiagnosticAi.endpoint").IsAssigned;
        }, TimeSpan.FromSeconds(10)));
        Emit(fixture.Intents, "ui.settings.choice", Get("SettingsOption.diagnostics.ai.enabled.true"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 650)); return Get("DiagnosticAi.key").IsAssigned; }, TimeSpan.FromSeconds(10)));
        AssertEqual("", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Get("DiagnosticAi.key"))!.ReadDraft()); AssertEqual(1, handler.Calls);
    }

    private sealed class DiagnosticAiWorkspaceCancellationHandler : HttpMessageHandler
    {
        private int _calls, _cancellations;
        internal int Calls => Volatile.Read(ref _calls);
        internal int Cancellations => Volatile.Read(ref _cancellations);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Interlocked.Increment(ref _cancellations); throw; }
            return new(HttpStatusCode.OK);
        }
    }
}
