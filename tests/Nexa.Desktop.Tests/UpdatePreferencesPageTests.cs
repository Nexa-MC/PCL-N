using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.Services.Updates;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void UpdatePreferencesCheckOutsideSettingsAndRetireOldChannels()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        var builder = new XsrQueryRouterBuilder();
        List<(NexaUpdateQuery Query, CancellationToken Token, TaskCompletionSource<XsrResult<NexaUpdateStatus>> Completion)> calls = [];
        builder.Register<NexaUpdateQuery, NexaUpdateStatus>(NexaUpdateContract.Check, (query, token) =>
        {
            var completion = new TaskCompletionSource<XsrResult<NexaUpdateStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Add((query, token, completion)); return new(completion.Task);
        });
        settings.ConfigureUpdates(builder.Build(new NoopDispatchObserver()), new("2.0.0.alpha.5", "win-x64", "alpha"), _ => { });
        fixture.Shell.Render(new(1000, 900));
        AssertEqual(1, calls.Count); AssertEqual("alpha", calls[0].Query.Channel);
        AssertFalse(fixture.Shell.Stage.Navigation.Current == settings.Page);
        var policy = fixture.Foundation.Host.SettingsPolicy;
        AssertTrue(policy.Set(new("updates.channel", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "beta"))).IsSuccess);
        fixture.Shell.Render(new(1000, 900)); AssertTrue(calls[0].Token.IsCancellationRequested);
        calls[0].Completion.SetResult(XsrResult.Success(new NexaUpdateStatus(new("2.0.0.alpha.6", "https://api.pcln.top/old", "https://api.pcln.top/old", "https://github.com/old"))));
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.advanced").Entity);
        scene = fixture.Shell.Render(new(1000, 900));
        AssertFalse(Has("SettingsDownloadUpdate"));
        Emit(fixture.Intents, "ui.settings.update.check", FindByKey(fixture.Shell, scene, "SettingsCheckUpdate").Entity);
        fixture.Shell.Render(new(1000, 900)); AssertEqual(2, calls.Count); AssertEqual("beta", calls[1].Query.Channel);
        calls[1].Completion.SetResult(XsrResult.Success(new NexaUpdateStatus(null)));
        fixture.Shell.Render(new(1000, 900)); fixture.Shell.Render(new(1000, 900)); AssertEqual(2, calls.Count);
        bool Has(string name)
        {
            bool found = false;
            fixture.Shell.Tree.Walk(settings.Page, entity => { found |= fixture.Shell.Tree.Name(entity) == name; return true; });
            return found;
        }
    }

    private static void DisabledStartupUpdatesKeepManualCheck()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("updates.auto-check", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false"))).IsSuccess);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        int calls = 0; var builder = new XsrQueryRouterBuilder();
        builder.Register<NexaUpdateQuery, NexaUpdateStatus>(NexaUpdateContract.Check, (query, _) =>
        { calls++; AssertEqual("stable", query.Channel); return new(XsrResult.Success(new NexaUpdateStatus(null))); });
        settings.ConfigureUpdates(builder.Build(new NoopDispatchObserver()), new("2.0.0", "win-x64", "stable"), _ => { });
        fixture.Shell.Render(new(1000, 900)); AssertEqual(0, calls);
        fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 900));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.advanced").Entity);
        scene = fixture.Shell.Render(new(1000, 900));
        Emit(fixture.Intents, "ui.settings.update.check", FindByKey(fixture.Shell, scene, "SettingsCheckUpdate").Entity);
        fixture.Shell.Render(new(1000, 900)); AssertEqual(1, calls);
    }
}
