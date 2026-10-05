using System.Globalization;
using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.Services.Composition;
using Nexa.Services.Foundation;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void DeveloperDiagnosticsAreReadOnlyAndRequireCommittedDeveloperMode()
    {
        foreach (string id in new[] { "global.advanced.01c0534c8b37", "global.advanced.8d723d8dcdfb" })
        {
            var entry = SettingsCatalog.Read(new(true)).Entries.Single(item => item.Id == id);
            AssertEqual(SettingsCatalogEntryKind.State, entry.Kind);
            AssertEqual(SettingsCapabilityAvailability.Available, entry.Availability);
            AssertTrue(entry.DeveloperOnly && entry.SettingKey is null && entry.Definition is null);
            AssertFalse(SettingsCatalog.Read(new()).Entries.Any(item => item.Id == id));
        }
        using var fixture = new DeveloperDiagnosticsFixture();
        fixture.Select("advanced");
        AssertFalse(fixture.Has("SettingsDiagnostics.State"));
        AssertFalse(fixture.Has("SettingsDiagnostics.Renderer"));
        fixture.SetDeveloper(true);
        AssertTrue(fixture.Has("SettingsDiagnostics.State"));
        AssertTrue(fixture.Has("SettingsDiagnostics.Renderer"));
        var refresh = fixture.Find("SettingsDiagnostics.State.Refresh");
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiInput>(refresh) is { Focusable: true, Clickable: true });
        long revision = fixture.Host.SettingsPolicy.Read(new()).Value!.Revision;
        fixture.Click("ui.settings.diagnostics.renderer.refresh", "SettingsDiagnostics.Renderer.Refresh");
        fixture.Click("ui.settings.diagnostics.state.refresh", "SettingsDiagnostics.State.Refresh");
        fixture.Click("ui.settings.diagnostics.state.next", "SettingsDiagnostics.State.Next");
        AssertEqual(revision, fixture.Host.SettingsPolicy.Read(new()).Value!.Revision);
        foreach (string card in new[] { "SettingsDiagnostics.State", "SettingsDiagnostics.Renderer" })
            fixture.Shell.Tree.Walk(fixture.Find(card), entity =>
            {
                AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiTextInput>(entity) is null);
                AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSemantic>(entity)?.Role is not (XsrUiSemanticRole.Switch or XsrUiSemanticRole.CheckBox));
                return true;
            });
        fixture.SetDeveloper(false);
        AssertFalse(fixture.Has("SettingsDiagnostics.State"));
        AssertFalse(fixture.Has("SettingsDiagnostics.Renderer"));
        revision = fixture.Host.SettingsPolicy.Read(new()).Value!.Revision;
        Emit(fixture.Intents, "ui.settings.diagnostics.state.refresh", refresh);
        fixture.Pump();
        AssertFalse(fixture.Has("SettingsDiagnostics.State"));
        AssertEqual(revision, fixture.Host.SettingsPolicy.Read(new()).Value!.Revision);
    }

    private static void DeveloperStateInspectorPagesSnapshotAndRejectsRetiredActions()
    {
        using var fixture = new DeveloperDiagnosticsFixture();
        fixture.SetDeveloper(true);
        fixture.Select("advanced");
        const string key = "AAADiagnostic040";
        long originalRevision = fixture.Store.Read<DeveloperDiagnosticSecret>(fixture.Store.Resolve(XsrSemanticId.Parse(key))).Revision;
        AssertEqual(32, fixture.VisibleStateRows());
        AssertTrue(fixture.Has("SettingsDiagnostics.State.Entry.AAADiagnostic000.Id"));
        AssertFalse(fixture.Has("SettingsDiagnostics.State.Entry." + key + ".Id"));
        fixture.Store.Publish(fixture.Store.Resolve(XsrSemanticId.Parse(key)), new DeveloperDiagnosticSecret());
        long currentRevision = fixture.Store.Read<DeveloperDiagnosticSecret>(fixture.Store.Resolve(XsrSemanticId.Parse(key))).Revision;
        AssertTrue(currentRevision != originalRevision);
        fixture.Pump(); fixture.Pump();
        fixture.Click("ui.settings.diagnostics.state.next", "SettingsDiagnostics.State.Next");
        AssertEqual(32, fixture.VisibleStateRows());
        AssertTrue(fixture.Text("SettingsDiagnostics.State.Summary").StartsWith("第 2 / ", StringComparison.Ordinal));
        AssertTrue(fixture.Text("SettingsDiagnostics.State.Entry." + key + ".Metadata")
            .Contains(" · 修订：" + originalRevision.ToString(CultureInfo.InvariantCulture) + " · ", StringComparison.Ordinal));
        fixture.Click("ui.settings.diagnostics.state.refresh", "SettingsDiagnostics.State.Refresh");
        AssertTrue(fixture.Text("SettingsDiagnostics.State.Summary").StartsWith("第 1 / ", StringComparison.Ordinal));
        fixture.Click("ui.settings.diagnostics.state.next", "SettingsDiagnostics.State.Next");
        AssertTrue(fixture.Text("SettingsDiagnostics.State.Entry." + key + ".Metadata")
            .Contains(" · 修订：" + currentRevision.ToString(CultureInfo.InvariantCulture) + " · ", StringComparison.Ordinal));
        var previous = fixture.Find("SettingsDiagnostics.State.Previous");
        var staleRefresh = fixture.Find("SettingsDiagnostics.State.Refresh");
        fixture.Select("general");
        Emit(fixture.Intents, "ui.settings.diagnostics.state.refresh", staleRefresh);
        fixture.Pump();
        AssertFalse(fixture.Has("SettingsDiagnostics.State"));
        fixture.Select("advanced");
        AssertTrue(fixture.Text("SettingsDiagnostics.State.Summary").StartsWith("第 1 / ", StringComparison.Ordinal));
        fixture.Click("ui.settings.diagnostics.state.next", "SettingsDiagnostics.State.Next");
        Emit(fixture.Intents, "ui.settings.diagnostics.state.previous", previous);
        fixture.Pump();
        AssertTrue(fixture.Text("SettingsDiagnostics.State.Summary").StartsWith("第 2 / ", StringComparison.Ordinal));
        AssertEqual(0, fixture.Secret.FormatCalls);
        fixture.Shell.Tree.Walk(fixture.Settings.Page, entity =>
        {
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content.Contains("diagnostic-secret", StringComparison.Ordinal) == true);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiSemantic>(entity)?.Label?.Contains("diagnostic-secret", StringComparison.Ordinal) == true);
            return true;
        });
    }

    private static void DeveloperRendererDiagnosticsRefreshOnlyOnDemandAndResumeOnReturn()
    {
        using var fixture = new DeveloperDiagnosticsFixture();
        fixture.SetDeveloper(true);
        fixture.Select("advanced");
        string scene = fixture.Text("SettingsDiagnostics.Renderer.SceneVersion.Value");
        string scheme = fixture.Text("SettingsDiagnostics.Renderer.ColorScheme.Value");
        string policy = fixture.Text("SettingsDiagnostics.Renderer.MotionPolicy.Value");
        fixture.Shell.Renderer.ColorScheme = new(true, XsrUiAccent.Purple);
        fixture.Shell.Renderer.ReducedMotion = false;
        fixture.Shell.Renderer.OptionalMotionSuspended = true;
        fixture.Pump(); fixture.Pump();
        AssertEqual(scene, fixture.Text("SettingsDiagnostics.Renderer.SceneVersion.Value"));
        AssertEqual(scheme, fixture.Text("SettingsDiagnostics.Renderer.ColorScheme.Value"));
        AssertEqual(policy, fixture.Text("SettingsDiagnostics.Renderer.MotionPolicy.Value"));
        long expectedScene = fixture.Shell.Renderer.SceneVersion;
        int expectedVisits = fixture.Shell.Renderer.LastLayoutVisits;
        int expectedCount = fixture.Shell.Tree.Count;
        fixture.Click("ui.settings.diagnostics.renderer.refresh", "SettingsDiagnostics.Renderer.Refresh");
        AssertEqual(expectedScene.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsDiagnostics.Renderer.SceneVersion.Value"));
        AssertEqual(expectedVisits.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsDiagnostics.Renderer.LayoutVisits.Value"));
        AssertEqual(expectedCount.ToString(CultureInfo.InvariantCulture), fixture.Text("SettingsDiagnostics.Renderer.TreeCount.Value"));
        AssertEqual("深色 · Purple", fixture.Text("SettingsDiagnostics.Renderer.ColorScheme.Value"));
        AssertEqual("用户减少动态：否 · 暂停可选动态：是", fixture.Text("SettingsDiagnostics.Renderer.MotionPolicy.Value"));
        fixture.Select("general");
        fixture.Shell.Renderer.ColorScheme = new(false, XsrUiAccent.Green);
        fixture.Select("advanced");
        AssertEqual("浅色 · Green", fixture.Text("SettingsDiagnostics.Renderer.ColorScheme.Value"));
        var staleRefresh = fixture.Find("SettingsDiagnostics.Renderer.Refresh");
        var destination = fixture.Shell.Tree.Create("DeveloperDiagnosticsAlternateDestination");
        fixture.Shell.Tree.SetComponent(destination, new XsrUiElement());
        fixture.Shell.Stage.Navigation.Replace(destination);
        fixture.Pump();
        fixture.Shell.Renderer.ColorScheme = new(true, XsrUiAccent.Orange);
        Emit(fixture.Intents, "ui.settings.diagnostics.renderer.refresh", staleRefresh);
        fixture.Pump();
        fixture.Shell.Stage.Navigation.Replace(fixture.Settings.Page);
        fixture.Pump();
        AssertEqual("深色 · Orange", fixture.Text("SettingsDiagnostics.Renderer.ColorScheme.Value"));
        scene = fixture.Text("SettingsDiagnostics.Renderer.SceneVersion.Value");
        Emit(fixture.Intents, "ui.settings.diagnostics.renderer.refresh", staleRefresh);
        fixture.Pump();
        AssertEqual(scene, fixture.Text("SettingsDiagnostics.Renderer.SceneVersion.Value"));
        var retained = fixture.Shell.Render(new(1000, 650));
        AssertTrue(ReferenceEquals(retained, fixture.Shell.Render(new(1000, 650))));
    }

    private static void DeveloperStateMetadataProjectionBoundsRowsAndNeverFormatsValues()
    {
        var builder = new XsrStateStoreBuilder();
        for (int i = 0; i < 2052; i++) builder.Cell<DeveloperDiagnosticSecret>(XsrSemanticId.Parse("diagnostics.test.s" + i.ToString("D4", CultureInfo.InvariantCulture)), "DeveloperDiagnosticTests");
        var store = builder.Build();
        var secret = new DeveloperDiagnosticSecret();
        for (int i = 0; i < 2052; i++) store.Publish(store.Resolve(XsrSemanticId.Parse("diagnostics.test.s" + i.ToString("D4", CultureInfo.InvariantCulture))), secret);
        var snapshot = SettingsPageController.CaptureDeveloperStateMetadata(store);
        AssertEqual(2052, snapshot.Total);
        AssertEqual(2048, snapshot.Rows.Count);
        AssertEqual("diagnostics.test.s0000", snapshot.Rows[0].SemanticId);
        AssertEqual("diagnostics.test.s2047", snapshot.Rows[^1].SemanticId);
        AssertTrue(snapshot.Rows.All(row => row.Owner == "DeveloperDiagnosticTests" && row.Kind == "Cell" && row.Availability == "Available"));
        AssertEqual(0, secret.FormatCalls);
        long captured = snapshot.Rows[0].Revision;
        store.MarkAvailability(store.Resolve(XsrSemanticId.Parse("diagnostics.test.s0000")), XsrStateAvailability.Stale);
        AssertEqual(captured, snapshot.Rows[0].Revision);
        AssertEqual("Available", snapshot.Rows[0].Availability);
        AssertEqual("Stale", SettingsPageController.CaptureDeveloperStateMetadata(store).Rows[0].Availability);
        AssertEqual(0, secret.FormatCalls);
    }

    private sealed class DeveloperDiagnosticSecret
    {
        internal int FormatCalls { get; private set; }
        public override string ToString()
        {
            FormatCalls++;
            throw new InvalidOperationException("diagnostic-secret must never be formatted");
        }
    }

    private sealed class DeveloperDiagnosticsFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nexa-developer-diagnostics-tests", Guid.NewGuid().ToString("N"));
        internal FoundationHost Host { get; }
        internal XsrStateStore Store => Host.StateStore;
        internal DesktopUiIntentSink Intents { get; } = new();
        internal XsrUiShell Shell { get; }
        internal SettingsPageController Settings { get; }
        internal DesktopFeedbackService Feedback { get; } = new();
        internal DeveloperDiagnosticSecret Secret { get; } = new();

        internal DeveloperDiagnosticsFixture()
        {
            Directory.CreateDirectory(_directory);
            var ui = new XsrUiRuntimeContext();
            var schema = LauncherDefaults.CreateSchema();
            Host = FoundationComposer.Compose(new InMemorySettingsPort(), schema,
                new LaunchProfileFilePort(Path.Combine(_directory, "profiles.json")), observer: ui.StateBridge,
                declareHostState: builder =>
                {
                    LaunchPageState.DeclareState(builder);
                    for (int i = 0; i < 96; i++) builder.Cell<DeveloperDiagnosticSecret>(XsrSemanticId.Parse("AAADiagnostic" + i.ToString("D3", CultureInfo.InvariantCulture)), "DeveloperDiagnosticTests");
                });
            for (int i = 0; i < 96; i++) Store.Publish(Store.Resolve(XsrSemanticId.Parse("AAADiagnostic" + i.ToString("D3", CultureInfo.InvariantCulture))), Secret);
            var foundation = FoundationRuntimeComposer.Compose(Host);
            Shell = PxmlShellComposer.Compose(Store, ui, intentSink: Intents);
            Shell.Renderer.ReducedMotion = true;
            Settings = new(Shell, Intents, foundation.Queries, foundation.Commands, Store, Feedback);
            Shell.Stage.Navigation.Replace(Settings.Page);
            Pump();
        }

        internal void Pump() => Shell.Render(new(1000, 650));
        internal void Select(string page) { Emit(Intents, "ui.settings.section", Find("SettingsNav." + page)); Pump(); }
        internal void Click(string command, string key) { Emit(Intents, command, Find(key)); Pump(); }
        internal string Text(string key) => Shell.Tree.GetComponent<XsrUiText>(Find(key))!.Content;
        internal void SetDeveloper(bool enabled)
        {
            AssertTrue(Host.SettingsPolicy.Set(new("developer.enabled", SettingsLayer.Global, new(SettingsOverrideMode.Custom, enabled ? "true" : "false"))).IsSuccess);
            AssertTrue(SpinWait.SpinUntil(() => { Pump(); return Has("SettingsDiagnostics.State") == (enabled && Settings.SelectedSection == "advanced"); }, TimeSpan.FromSeconds(5)));
        }
        internal XsrUiEntityId Find(string key)
        {
            var entity = FindOrDefault(key);
            AssertTrue(entity.IsAssigned);
            return entity;
        }
        internal bool Has(string key) => FindOrDefault(key).IsAssigned;
        private XsrUiEntityId FindOrDefault(string key)
        {
            XsrUiEntityId found = default;
            Shell.Tree.Walk(Settings.Page, entity => { if (Shell.Tree.Name(entity) == key) found = entity; return true; });
            return found;
        }
        internal int VisibleStateRows()
        {
            int count = 0;
            Shell.Tree.Walk(Find("SettingsDiagnostics.State.Body"), entity =>
            {
                string name = Shell.Tree.Name(entity);
                if (name.StartsWith("SettingsDiagnostics.State.Entry.", StringComparison.Ordinal) && name.EndsWith(".Id", StringComparison.Ordinal)) count++;
                return true;
            });
            return count;
        }
        public void Dispose()
        {
            Settings.Dispose(); Feedback.Dispose(); Host.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
