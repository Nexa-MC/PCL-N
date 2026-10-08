using Nexa.Desktop;
using Nexa.Desktop.Ui;
using Nexa.Services.Downloads;
using Nexa.Services.Network;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void StartupHintsConsumeCommittedChoiceAndKeepLaunchProgress()
    {
        var recording = new RecordingStartRoute { Hang = true };
        using var fixture = ComposeLaunchOverlayFixture(recording);
        AssertTrue(fixture.Foundation.Commands.TryResolve(SettingsPolicyContract.SetCommand, out var set));
        void Apply(bool value)
        {
            var dispatched = fixture.Foundation.Commands.Dispatch(set, new SettingsMutation("general.launch-hints", SettingsLayer.Global,
                new(SettingsOverrideMode.Custom, value ? "true" : "false")));
            AssertTrue(dispatched.Completion.GetAwaiter().GetResult().IsSuccess);
            var settings = CommittedSettingsRead.QueryAsync(fixture.Foundation.Queries, default).AsTask().GetAwaiter().GetResult()!;
            fixture.Controller.SetStartupHintsVisible(bool.Parse(settings.Values.Single(item => item.Key == "general.launch-hints").Value.Value!));
        }
        Apply(false); SelectFirstAccountAndLaunch(fixture, recording);
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(HasKey(fixture.Shell, scene, "LaunchingHintBox"));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchingPercentValue"));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchingCancelButton"));
        Apply(true); scene = fixture.Shell.Render(new(1000, 650));
        AssertTrue(HasKey(fixture.Shell, scene, "LaunchingHintBox"));
        var options = new (string Key, DesktopDestination Destination)[]
        {
            ("launch", DesktopDestination.Launch), ("install", DesktopDestination.Install), ("resources", DesktopDestination.Resources),
            ("settings", DesktopDestination.Settings), ("java", DesktopDestination.Java), ("storage", DesktopDestination.Storage),
            ("about", DesktopDestination.About), ("tasks", DesktopDestination.Tasks),
        };
        foreach (var option in options)
        {
            var result = fixture.Foundation.Commands.Dispatch(set, new SettingsMutation("general.startup-page", SettingsLayer.Global,
                new(SettingsOverrideMode.Custom, option.Key))).Completion.GetAwaiter().GetResult();
            AssertTrue(result.IsSuccess); AssertEqual(option.Destination, SystemPreferencesSession.StartupDestination(option.Key));
        }
        AssertFalse(fixture.Foundation.Commands.Dispatch(set, new SettingsMutation("general.startup-page", SettingsLayer.Global,
            new(SettingsOverrideMode.Custom, "https://arbitrary.example/"))).Completion.GetAwaiter().GetResult().IsSuccess);
        AssertEqual(DesktopDestination.Launch, SystemPreferencesSession.StartupDestination(null));
    }

    private static void ManualNetworkProbeUiCancelsAndKeepsObservedFactsLiteral()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var queries = new XsrQueryRouterBuilder();
        AssertTrue(fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog));
        AssertTrue(fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective));
        queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
        queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
            (query, token) => fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
        var at = new DateTimeOffset(2026, 10, 8, 12, 34, 56, TimeSpan.Zero);
        queries.Register<NetworkTraceQuery, IReadOnlyList<NetworkRequestTrace>>(NetworkDiagnosticsContract.Trace, (_, _) =>
            new(XsrResult.Success<IReadOnlyList<NetworkRequestTrace>>([new(1, at, "api.modrinth.com", "request", 403, 4, null)])));
        List<(CancellationToken Token, TaskCompletionSource<XsrResult<NetworkManualProbeSnapshot>> Reply)> reads = [];
        queries.Register<NetworkManualProbeQuery, NetworkManualProbeSnapshot>(NetworkDiagnosticsContract.Probe, (_, token) =>
        {
            var reply = new TaskCompletionSource<XsrResult<NetworkManualProbeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            reads.Add((token, reply)); return new(reply.Task);
        });
        using var page = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(page.Page);
        XsrUiScene scene = fixture.Shell.Render(new(1000, 1800));
        void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { scene = fixture.Shell.Render(new(1000, 1800)); return condition(); }, TimeSpan.FromSeconds(5)));
        Pump(() => HasKey(fixture.Shell, scene, "SettingsNav.network"));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.network").Entity);
        Pump(() => FindCompletionEntity(page.Page, "NetworkProbe.Start").IsAssigned);
        var start = FindCompletionEntity(page.Page, "NetworkProbe.Start");
        Emit(fixture.Intents, "ui.settings.network.probe.start", start);
        Pump(() => reads.Count == 1);
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiInput>(FindCompletionEntity(page.Page, "NetworkProbe.Start"))!.Enabled);
        Emit(fixture.Intents, "ui.settings.network.probe.start", start); fixture.Shell.Render(new(1000, 1800)); AssertEqual(1, reads.Count);
        reads[0].Reply.SetResult(XsrResult.Success(new NetworkManualProbeSnapshot(at, at,
            [new("api.modrinth.com", at, 404, 12.5, null)])));
        Pump(() => Texts(page.Page).Any(text => text.Contains("api.modrinth.com", StringComparison.Ordinal) && text.Contains("404", StringComparison.Ordinal)));
        var observed = FindCompletionText(page.Page, text => text.Contains("api.modrinth.com", StringComparison.Ordinal) && text.Contains("404", StringComparison.Ordinal));
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(observed)!.Localize);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiText>(observed)!.Content!.Contains("2026-10-08", StringComparison.Ordinal));
        Emit(fixture.Intents, "ui.settings.network.trace.refresh");
        Pump(() => Texts(page.Page).Any(text => text.Contains("api.modrinth.com", StringComparison.Ordinal) && text.Contains("403", StringComparison.Ordinal)));
        var transfers = fixture.Store.Resolve(DownloadStateContract.TransfersKey);
        long revision = fixture.Store.ReadCollection<DownloadTransferView>(transfers).Revision;
        fixture.Store.PublishDelta(transfers, new XsrCollectionDelta<DownloadTransferView, string>(revision,
            [new("private-local-path.jar", DownloadStage.Downloading, "https://cdn.modrinth.com/private/file.jar?token=secret", 64, 128, 32)], []));
        Pump(() => Texts(page.Page).Any(text => text.Contains("cdn.modrinth.com", StringComparison.Ordinal) && text.Contains("64/128 B", StringComparison.Ordinal)));
        var diagnostics = FindCompletionEntity(page.Page, "NetworkDownloadDiagnostics");
        AssertFalse(Texts(diagnostics).Any(text => text.Contains("secret", StringComparison.Ordinal) || text.Contains("file.jar", StringComparison.Ordinal)));
        revision = fixture.Store.ReadCollection<DownloadTransferView>(transfers).Revision;
        fixture.Store.PublishDelta(transfers, new XsrCollectionDelta<DownloadTransferView, string>(revision, [], ["private-local-path.jar"]));
        Pump(() => Texts(FindCompletionEntity(page.Page, "NetworkDownloadDiagnostics")).Contains("当前没有活动下载。", StringComparer.Ordinal));
        Emit(fixture.Intents, "ui.settings.network.probe.start", FindCompletionEntity(page.Page, "NetworkProbe.Start"));
        Pump(() => reads.Count == 2);
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.general").Entity);
        Pump(() => reads[1].Token.IsCancellationRequested);
        reads[1].Reply.SetResult(XsrResult.Success(new NetworkManualProbeSnapshot(at, at, [new("retired.example", at, 200, 0, null)])));
        fixture.Shell.Render(new(1000, 1800)); AssertFalse(Texts(page.Page).Any(text => text.Contains("retired.example", StringComparison.Ordinal)));
        AssertTrue(Texts(page.Page).Contains("文件修改、恢复与数据发送前保留确认。", StringComparer.Ordinal));
        var protocol = FindCompletionEntity(page.Page, "SettingsGeneral.Protocol"); AssertTrue(protocol.IsAssigned);
        var state = FindCompletionText(protocol, _ => true);
        AssertEqual(fixture.Shell.Tree.GetComponent<XsrUiText>(state)!.Content, fixture.Shell.Tree.GetComponent<XsrUiSemantic>(state)!.Label);
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiSemantic>(state)!.Localize);

        XsrUiEntityId FindCompletionEntity(XsrUiEntityId root, string name)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(root, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            return found;
        }
        XsrUiEntityId FindCompletionText(XsrUiEntityId root, Func<string, bool> condition)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(root, entity =>
            {
                if (fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content is { } text && condition(text)) found = entity;
                return true;
            });
            AssertTrue(found.IsAssigned); return found;
        }
        IReadOnlyList<string> Texts(XsrUiEntityId root)
        {
            List<string> values = [];
            fixture.Shell.Tree.Walk(root, entity => { if (fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content is { } text) values.Add(text); return true; });
            return values;
        }
    }

    private static void ResourceSourcePolicyDisablesLocalOverrideAndRestoresIt()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        AssertTrue(fixture.Foundation.Commands.TryResolve(SettingsPolicyContract.SetCommand, out var set));
        var queries = new XsrQueryRouterBuilder(); int searches = 0;
        queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search,
            (_, _) => { searches++; return new(XsrResult.Success(new ResourceSearchResult([], 0, 0))); });
        queries.Register<ResourceNetworkPolicyQuery, ResourceNetworkPolicySnapshot>(ResourceCatalogContract.NetworkPolicy, (_, _) =>
        {
            var values = fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values;
            string priority = values.Single(item => item.Key == "network.resource-source").Value.Value!;
            return new(XsrResult.Success(new ResourceNetworkPolicySnapshot(priority, true, null)));
        });
        using var page = new ResourcesPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()), fixture.Store, _ => { });
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(page.Page);
        var scene = fixture.Shell.Render(new(1000, 800));
        var sourceTrack = fixture.Shell.Tree.GetComponent<XsrUiSegmentedTrack>(page.Find("ResourceNetwork"))!;
        AssertEqual(page.Find("ResourceNetwork.0"), sourceTrack.Selected);
        bool Enabled() => fixture.Shell.Tree.GetComponent<XsrUiInput>(page.Find("ResourceNetwork.0"))!.Enabled;
        void Apply(string priority, bool expected)
        {
            AssertTrue(fixture.Foundation.Commands.Dispatch(set, new SettingsMutation("network.resource-source", SettingsLayer.Global,
                new(SettingsOverrideMode.Custom, priority))).Completion.GetAwaiter().GetResult().IsSuccess);
            AssertTrue(SpinWait.SpinUntil(() => { scene = fixture.Shell.Render(new(1000, 800)); return Enabled() == expected; }, TimeSpan.FromSeconds(5)));
        }
        AssertTrue(Enabled()); Apply("official-first", false);
        AssertTrue(scene.Nodes.Any(node => node.Text == "资源站使用全局官方优先。"));
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSelection>(page.Find("ResourceNetwork.1"))!.IsSelected);
        AssertEqual(page.Find("ResourceNetwork.1"), sourceTrack.Selected);
        int before = searches; Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceNetwork.0"));
        fixture.Shell.Render(new(1000, 800)); AssertEqual(before, searches);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSelection>(page.Find("ResourceNetwork.1"))!.IsSelected);
        AssertEqual(page.Find("ResourceNetwork.1"), sourceTrack.Selected);
        Apply("follow-request", true);
        AssertEqual(page.Find("ResourceNetwork.0"), sourceTrack.Selected);
        AssertFalse(scene.Nodes.Any(node => node.Text is "资源站使用全局官方优先。" or "资源站使用全局镜像优先。"));
        Emit(fixture.Intents, "ui.resources.action", page.Find("ResourceNetwork.1"));
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Shell.Render(new(1000, 800)); return searches > before; }, TimeSpan.FromSeconds(5)));
    }

    private static void MediaMenuDeduplicatesAndRetiresOnlyOwnedItems()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var foreign = new XsrUiContextMenuItem("Foreign", XsrSemanticId.Parse("ui.test.foreign"));
        fixture.Shell.Tree.SetComponent(fixture.Shell.Root, new XsrUiContextMenu([
            foreign, new("old media", XsrSemanticId.Parse("ui.media.next")), new("duplicate", XsrSemanticId.Parse("ui.media.next"))]));
        var session = new DesktopMediaSession(fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Shell,
            fixture.Intents, new AvaloniaUiPlatformActions(), _ => { });
        try
        {
            var menu = fixture.Shell.Tree.GetComponent<XsrUiContextMenu>(fixture.Shell.Root)!.Items;
            AssertEqual(5, menu.Count); AssertEqual(1, menu.Count(item => item.Command == XsrSemanticId.Parse("ui.media.next")));
            AssertTrue(menu.Contains(foreign));
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var remaining = fixture.Shell.Tree.GetComponent<XsrUiContextMenu>(fixture.Shell.Root)!.Items;
        AssertEqual(1, remaining.Count); AssertEqual(foreign, remaining[0]);
    }
}
