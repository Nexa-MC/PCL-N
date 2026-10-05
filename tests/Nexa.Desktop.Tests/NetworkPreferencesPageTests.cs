using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void ProxyDraftsApplyAtomicallyRejectStaleRevisionsAndMaskCredentials()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents,
            fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        fixture.Shell.Render(new(1000, 650));
        Emit(fixture.Intents, "ui.settings.section", Find("SettingsNav.network")); Pump();
        var policy = fixture.Foundation.Host.SettingsPolicy;
        string Read(string key) => policy.Read(new()).Value!.Values.Single(item => item.Key == key).Value.Value!;
        void Draft(string key, string value) => fixture.Shell.Renderer.SetTextInputValue(Find("SettingsInput." + key), value);
        void Click(string command, string key) { Emit(fixture.Intents, command, Find(key)); Pump(); }

        Click("ui.settings.proxy.mode", "SettingsProxyMode.2");
        Draft("network.proxy-address", "http://localhost:8181");
        Draft("network.proxy-user", "proxy-user"); Draft("network.proxy-password", "private-password");
        AssertEqual("1", Read("network.proxy-mode"));
        AssertEqual("", Read("network.proxy-address"));
        var input = fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsInput.network.proxy-password"))!;
        AssertTrue(input.IsPassword);
        var scene = fixture.Shell.Render(new(1000, 650));
        AssertFalse(scene.Nodes.Any(node => node.Text?.Contains("private-password", StringComparison.Ordinal) == true));

        // An unrelated durable change makes the draft revision stale.
        AssertTrue(policy.Set(new("network.doh", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false"))).IsSuccess);
        Pump(); Click("ui.settings.proxy.apply", "SettingsProxyApply");
        AssertTrue(SpinWait.SpinUntil(() => { Pump(); return !settings.SettingsWritePending && fixture.Feedback.Snapshot().Notifications.Any(note => note.Message.StartsWith("代理配置未保存", StringComparison.Ordinal)); }, TimeSpan.FromSeconds(5)));
        AssertEqual("1", Read("network.proxy-mode")); AssertEqual("", Read("network.proxy-address"));
        AssertEqual("private-password", input.ReadDraft());

        Click("ui.settings.proxy.reload", "SettingsProxyReload");
        Click("ui.settings.proxy.mode", "SettingsProxyMode.2");
        Draft("network.proxy-address", "http://localhost:8181"); Draft("network.proxy-user", "proxy-user"); Draft("network.proxy-password", "private-password");
        Click("ui.settings.proxy.apply", "SettingsProxyApply");
        AssertTrue(SpinWait.SpinUntil(() => { Pump(); return !settings.SettingsWritePending && Read("network.proxy-mode") == "2"; }, TimeSpan.FromSeconds(5)));
        AssertEqual("http://localhost:8181", Read("network.proxy-address"));
        AssertEqual("proxy-user", Read("network.proxy-user")); AssertEqual("private-password", Read("network.proxy-password"));
        // Durable values become visible before the asynchronous save and UI query settle.
        // Reload until the controls confirm the committed batch before starting the next edit.
        AssertTrue(SpinWait.SpinUntil(() =>
        {
            Click("ui.settings.proxy.reload", "SettingsProxyReload");
            return !settings.SettingsWritePending
                && fixture.Shell.Tree.GetComponent<XsrUiSelection>(Find("SettingsProxyMode.2"))!.IsSelected
                && fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsInput.network.proxy-address"))!.ReadDraft() == "http://localhost:8181";
        }, TimeSpan.FromSeconds(5)));
        Draft("network.proxy-address", "http://localhost:8181/path"); Click("ui.settings.proxy.apply", "SettingsProxyApply");
        AssertTrue(SpinWait.SpinUntil(() => { Pump(); return !settings.SettingsWritePending && fixture.Feedback.Snapshot().Notifications.Count(note => note.Message.StartsWith("代理配置未保存", StringComparison.Ordinal)) >= 2; }, TimeSpan.FromSeconds(5)));
        AssertEqual("http://localhost:8181", Read("network.proxy-address"));
        AssertEqual("http://localhost:8181/path", fixture.Shell.Tree.GetComponent<XsrUiTextInput>(Find("SettingsInput.network.proxy-address"))!.ReadDraft());

        void Pump() => fixture.Shell.Render(new(1000, 650));
        XsrUiEntityId Find(string key)
        {
            XsrUiEntityId entity = default;
            fixture.Shell.Tree.Walk(settings.Page, item => { if (fixture.Shell.Tree.Name(item) == key) entity = item; return true; });
            AssertTrue(entity.IsAssigned); return entity;
        }
    }
}
