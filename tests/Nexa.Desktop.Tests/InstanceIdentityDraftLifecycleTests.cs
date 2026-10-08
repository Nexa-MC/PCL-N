using System.Globalization;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void InstanceIdentityDraftSurvivesScopedFactRebuildsAndRetiresOnNavigation()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instanceA = CreateInstance("draft-a"), instanceB = CreateInstance("draft-b"), selected = instanceA;
        Seed(instanceA, "Original A", string.Join('\n', Enumerable.Range(0, 40).Select(index => "Original note " + index.ToString(CultureInfo.InvariantCulture))));
        Seed(instanceB, "Original B", "B note");
        var queries = new XsrQueryRouterBuilder();
        Forward<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery);
        Forward<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery);
        Forward<InstanceIdentityQuery, InstanceIdentitySnapshot>(InstanceIdentityContract.Query);
        bool pauseManagement = false;
        int pausedManagementReads = 0, offlineReads = 0;
        var pendingManagement = new TaskCompletionSource<XsrResult<InstanceManagementSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingOffline = new TaskCompletionSource<XsrResult<InstanceOfflineReadinessReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
        {
            if (pauseManagement) { pausedManagementReads++; return new(pendingManagement.Task); }
            return ValueTask.FromResult(XsrResult.Success(Management(q.InstanceDirectory)));
        });
        queries.Register<InstanceOfflineReadinessQuery, InstanceOfflineReadinessReport>(InstanceOfflineReadinessContract.Query, (q, _) =>
        { AssertEqual(instanceA, q.InstanceDirectory); offlineReads++; return new(pendingOffline.Task); });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => selected);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        Wait(() => Draft("name", required: false) == "Original A" && Find("SettingsNav.servers", false).IsAssigned);
        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.name"), "Unsaved A");
        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.description"), "Draft description");
        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.tags"), "draft-one, draft-two");
        Click("InstanceIdentityStar", "ui.settings.instance-identity.action");
        Click("InstanceIdentityIsolation", "ui.settings.instance-identity.action");
        Click("InstanceNoteNext", "ui.settings.instance-identity.action");
        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceNoteInput.32"), "Unsaved second-page note");

        var retiredSave = Find("InstanceIdentitySave");
        Click("Management.检查本地离线依赖", "ui.settings.management.action");
        AssertEqual(1, offlineReads); AssertFalse(fixture.Shell.Tree.IsAlive(retiredSave)); AssertDraft();
        pendingOffline.SetResult(XsrResult.Success(new InstanceOfflineReadinessReport(instanceA, DateTimeOffset.UtcNow,
            true, true, "fixture-java", [new("client", "draft-a.jar", OfflineArtifactState.Verified, 1, "verified")])));
        Wait(() => HasText("本地离线依赖校验通过")); AssertDraft();

        pauseManagement = true;
        retiredSave = Find("InstanceIdentitySave");
        Click("Management.刷新", "ui.settings.management.action");
        Wait(() => pausedManagementReads == 1); AssertDraft();
        pauseManagement = false;
        pendingManagement.SetResult(XsrResult.Success(Management(instanceA)));
        Wait(() => !fixture.Shell.Tree.IsAlive(retiredSave)); AssertDraft();

        Click("InstanceIdentitySave", "ui.settings.instance-identity.action");
        Wait(() => Read(instanceA).Fields.DisplayName == "Unsaved A");
        Wait(() => Find("InstanceNoteInput.0", false).IsAssigned);
        var saved = Read(instanceA);
        AssertEqual("Draft description", saved.Fields.Description); AssertTrue(saved.Fields.Starred);
        AssertFalse(saved.Fields.InstanceIsolation); AssertTrue(saved.Fields.Tags.SequenceEqual(["draft-one", "draft-two"]));
        AssertEqual("Unsaved second-page note", saved.Fields.Notes.Split('\n')[32]);

        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.name"), "Discard on reload");
        Click("InstanceIdentityReload", "ui.settings.instance-identity.action");
        Wait(() => Draft("name", required: false) == "Unsaved A");
        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.name"), "Discard on navigation");
        Click("SettingsNav.servers", "ui.settings.section");
        Wait(() => Find("InstanceIdentityInput.game-version", false).IsAssigned);
        Click("SettingsNav.overview", "ui.settings.section");
        Wait(() => Draft("name", required: false) == "Unsaved A");
        fixture.Shell.Renderer.SetTextInputValue(Find("InstanceIdentityInput.name"), "Must not leak to B");
        selected = instanceB;
        Wait(() => Draft("name", required: false) == "Original B");
        AssertEqual("Original B", Read(instanceB).Fields.DisplayName); AssertEqual("Unsaved A", Read(instanceA).Fields.DisplayName);

        string CreateInstance(string id)
        {
            string path = Path.Combine(fixture.TemporaryDirectory, "minecraft", "versions", id); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, id + ".json"), "{\"id\":\"" + id + "\",\"mainClass\":\"fixture.Main\",\"_minecraftVersion\":\"1.20.1\",\"libraries\":[]}");
            return path;
        }
        void Seed(string instance, string name, string notes)
        {
            var snapshot = Read(instance);
            AssertTrue(InstanceIdentityService.SaveAsync(new(instance, snapshot.Revision,
                snapshot.Fields with { DisplayName = name, Notes = notes }, snapshot.Server)).GetAwaiter().GetResult().IsSuccess);
        }
        static InstanceIdentitySnapshot Read(string instance) => InstanceIdentityService.ReadAsync(new(instance)).GetAwaiter().GetResult();
        static InstanceManagementSnapshot Management(string instance) => new(instance, instance, "1.20.1", [],
            [new("overview", "总览"), new("game", "游戏设置"), new("servers", "服务器"), new("recovery", "快照与存储")], true, "");
        void Forward<TQuery, TResponse>(XsrSemanticId semantic) where TQuery : notnull
        {
            AssertTrue(fixture.Foundation.Queries.TryResolve(semantic, out var route));
            queries.Register<TQuery, TResponse>(semantic,
                (query, token) => fixture.Foundation.Queries.QueryAsync<TQuery, TResponse>(route, query, cancellationToken: token));
        }
        void Click(string name, string command)
        { Emit(fixture.Intents, command, Find(name)); fixture.Shell.Render(new(1400, 6000)); }
        void Wait(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { fixture.Shell.Render(new(1400, 6000)); return condition(); }, TimeSpan.FromSeconds(5)));
        XsrUiEntityId Find(string name, bool required = true)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            if (required) AssertTrue(found.IsAssigned); return found;
        }
        string? Draft(string key, bool required = true)
        { var input = Find("InstanceIdentityInput." + key, required); return input.IsAssigned ? fixture.Shell.Tree.GetComponent<XsrUiTextInput>(input)!.ReadDraft() : null; }
        bool HasText(string value)
        {
            bool found = false;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content == value) found = true; return true; });
            return found;
        }
        void AssertDraft()
        {
            AssertEqual("Unsaved A", Draft("name")); AssertEqual("Draft description", Draft("description"));
            AssertEqual("draft-one, draft-two", Draft("tags"));
            AssertEqual("Unsaved second-page note", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("InstanceNoteInput.32"))!.ReadDraft());
            AssertTrue(HasText("已收藏 · 点击取消")); AssertTrue(HasText("游戏目录：共享根目录（点击改为隔离）"));
            AssertEqual("Original A", Read(instanceA).Fields.DisplayName);
        }
    }
}
