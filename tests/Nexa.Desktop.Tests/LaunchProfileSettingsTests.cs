using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void LaunchProfileSettingsEditNamedAndTemporaryLayers()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        string instance = Path.GetFullPath("desktop-profile-instance");
        var policy = fixture.Foundation.Host.SettingsPolicy;
        var queries = new XsrQueryRouterBuilder();
        Forward<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery);
        Forward<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery);
        AssertTrue(fixture.Foundation.Queries.TryResolve(SettingsLaunchProfileContract.Query, out var profilesRoute));
        TaskCompletionSource<XsrResult<SettingsLaunchProfilesSnapshot>>? pendingProfiles = null;
        int pausedProfileReads = 0;
        queries.Register<SettingsLaunchProfilesQuery, SettingsLaunchProfilesSnapshot>(SettingsLaunchProfileContract.Query, (query, token) =>
        {
            if (pendingProfiles is { } pending) { pausedProfileReads++; return new(pending.Task); }
            return fixture.Foundation.Queries.QueryAsync<SettingsLaunchProfilesQuery, SettingsLaunchProfilesSnapshot>(profilesRoute,
                query, cancellationToken: token);
        });
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, queries.Build(new NoopDispatchObserver()),
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback, () => instance);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        fixture.Shell.Render(new(1200, 900));
        Emit(fixture.Intents, "ui.settings.section", Find("SettingsNav.game"));
        WaitReady("LaunchProfileCreate");
        fixture.Shell.Renderer.SetTextInputValue(Find("LaunchProfileName"), "Named fixture");
        Emit(fixture.Intents, "ui.settings.launch-profile.action", Find("LaunchProfileCreate"));
        Wait(() => Profiles().Profiles.Count == 1);
        string profileId = Profiles().Profiles[0].Id;
        WaitReady("LaunchProfileSelect." + profileId);
        Emit(fixture.Intents, "ui.settings.launch-profile.action", Find("LaunchProfileSelect." + profileId));
        Wait(() => Profiles().SelectedProfileId == profileId && Enabled("LaunchProfileSave"));
        string draftDirectory = Path.GetFullPath("unsaved-profile-config");
        fixture.Shell.Renderer.SetTextInputValue(Find("LaunchProfileName"), "Uncommitted name");
        fixture.Shell.Renderer.SetTextInputValue(Find("LaunchOverlayConfig"), draftDirectory);
        fixture.Shell.Renderer.Focus(Find("LaunchProfileName"), showIndicator: false);
        pendingProfiles = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ChangeWidth("1024");
        Wait(() => pausedProfileReads == 1);
        AssertFalse(Enabled("LaunchTemporaryBegin"));
        ReleaseProfiles();
        WaitReady("LaunchTemporaryBegin");
        Wait(() => ReadDraft("LaunchProfileName") == "Uncommitted name" && ReadDraft("LaunchOverlayConfig") == draftDirectory);
        AssertEqual(Find("LaunchProfileName"), fixture.Shell.Renderer.Focused);
        AssertEqual("Named fixture", Profiles().Profiles.Single(profile => profile.Id == profileId).Name);
        AssertEqual(null, Profiles().Profiles.Single(profile => profile.Id == profileId).Overlay.ConfigSource);
        AssertEqual(SettingsLayer.Profile, Width().Source); AssertEqual("1024", Width().Value.Value);
        AssertEqual(null, Profiles().TemporaryId);
        Emit(fixture.Intents, "ui.settings.launch-profile.action", Find("LaunchTemporaryBegin"));
        Wait(() => Profiles().TemporaryId is not null && Enabled("LaunchTemporaryEnd"));
        ChangeWidth("1280");
        WaitReady("LaunchTemporaryEnd");
        AssertEqual(SettingsLayer.Temporary, Width().Source);
        AssertEqual("1024", Profiles().Profiles.Single(profile => profile.Id == profileId).Values["game.width"].Value);
        pendingProfiles = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Emit(fixture.Intents, "ui.settings.launch-profile.action", Find("LaunchTemporaryEnd"));
        Wait(() => Profiles().TemporaryId is null && Width().Value.Value == "1024");
        Wait(() => pausedProfileReads == 2 && !settings.SettingsWritePending);
        AssertFalse(Enabled("LaunchProfileBase"));
        AssertFalse(Enabled("LaunchTemporaryEnd"));
        long revision = Profiles().Revision;
        var retiredBase = Find("LaunchProfileBase");
        // A service receipt alone cannot admit another write through controls from the old layer.
        Emit(fixture.Intents, "ui.settings.launch-profile.action", retiredBase);
        Emit(fixture.Intents, "ui.settings.launch-profile.action", Find("LaunchTemporaryEnd"));
        fixture.Shell.Render(new(1200, 900));
        AssertEqual(revision, Profiles().Revision);
        AssertEqual(profileId, Profiles().SelectedProfileId); AssertEqual(null, Profiles().TemporaryId);
        fixture.Shell.Renderer.SetTextInputValue(Find("SettingsInput.game.width"), "1536");
        Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.width"));
        Wait(() => !settings.SettingsWritePending && fixture.Feedback.Snapshot().Notifications.Any(notification =>
            notification.Message == "设置未保存：启动配置正在更新，请稍后重试。"));
        AssertEqual(revision, Profiles().Revision); AssertEqual(null, Profiles().TemporaryId);
        AssertEqual("1024", Width().Value.Value); AssertEqual(SettingsLayer.Profile, Width().Source);
        ReleaseProfiles();
        WaitReady("LaunchProfileBase");
        AssertFalse(fixture.Shell.Tree.IsAlive(retiredBase));
        Emit(fixture.Intents, "ui.settings.launch-profile.action", retiredBase);
        fixture.Shell.Render(new(1200, 900));
        AssertEqual(revision, Profiles().Revision); AssertEqual(profileId, Profiles().SelectedProfileId);
        Emit(fixture.Intents, "ui.settings.launch-profile.action", Find("LaunchProfileBase"));
        Wait(() => Profiles().SelectedProfileId is null);
        AssertEqual("854", Width().Value.Value);

        SettingsLaunchProfilesSnapshot Profiles() => policy.ReadLaunchProfiles(new(instance)).Value!;
        void Forward<TQuery, TResponse>(XsrSemanticId semantic) where TQuery : notnull
        {
            AssertTrue(fixture.Foundation.Queries.TryResolve(semantic, out var route));
            queries.Register<TQuery, TResponse>(semantic,
                (query, token) => fixture.Foundation.Queries.QueryAsync<TQuery, TResponse>(route, query, cancellationToken: token));
        }
        void ReleaseProfiles()
        {
            var pending = pendingProfiles!; pendingProfiles = null;
            pending.SetResult(XsrResult.Success(Profiles()));
        }
        bool Enabled(string name)
        {
            var entity = Find(name, required: false);
            return entity.IsAssigned && fixture.Shell.Tree.GetComponent<XsrUiInput>(entity)?.Enabled == true;
        }
        void WaitReady(string name) => Wait(() => !settings.SettingsWritePending && Enabled(name));
        string ReadDraft(string name) => fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find(name))!.ReadDraft();
        SettingsEffectiveValue Width() => policy.Read(new(instance)).Value!.Values.Single(value => value.Key == "game.width");
        void ChangeWidth(string value)
        {
            fixture.Shell.Renderer.SetTextInputValue(Find("SettingsInput.game.width"), value);
            Emit(fixture.Intents, "ui.settings.edit", Find("SettingsEdit.game.width"));
            Wait(() => !settings.SettingsWritePending && Width().Value.Value == value);
        }
        void Wait(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() =>
        { fixture.Shell.Render(new(1200, 900)); return condition(); }, TimeSpan.FromSeconds(5)));
        XsrUiEntityId Find(string name, bool required = true)
        {
            XsrUiEntityId found = default;
            fixture.Shell.Tree.Walk(settings.Page, entity => { if (fixture.Shell.Tree.Name(entity) == name) found = entity; return true; });
            if (required) AssertTrue(found.IsAssigned); return found;
        }
    }
}
