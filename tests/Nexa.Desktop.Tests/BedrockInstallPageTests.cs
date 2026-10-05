using Nexa.Desktop.Ui;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void BedrockInstallOfficialActionsRequireVisibleOwnedButtons()
    {
        int stores = 0;
        List<Uri> websites = [];
        using var fixture = new BedrockInstallFixture(() => stores++, websites.Add, supportsStore: true);
        AssertEqual(0, stores);
        AssertEqual(0, websites.Count);
        var scene = fixture.Render();
        var store = FindByKey(fixture.Shell, scene, "BedrockInstallStore").Entity;
        var website = FindByKey(fixture.Shell, scene, "BedrockInstallWebsite").Entity;
        for (int i = 0; i < 3; i++) fixture.Render();
        AssertEqual(0, stores);
        AssertEqual(0, websites.Count);

        // A semantic command alone, or the other button's entity, cannot authorize a native effect.
        Emit(fixture.Intents, "ui.bedrock.install.store");
        Emit(fixture.Intents, "ui.bedrock.install.website", store);
        Emit(fixture.Intents, "ui.bedrock.install.store", website);
        Emit(fixture.Intents, "ui.bedrock.install.store", fixture.Home);
        AssertEqual(0, stores);
        AssertEqual(0, websites.Count);
        var actions = FindByKey(fixture.Shell, scene, "BedrockInstallActions").Entity;
        fixture.Shell.Tree.GetComponent<XsrUiElement>(actions)!.IsVisible = false;
        Emit(fixture.Intents, "ui.bedrock.install.store", store);
        AssertEqual(0, stores);
        fixture.Shell.Tree.GetComponent<XsrUiElement>(actions)!.IsVisible = true;
        AssertTrue(fixture.Shell.Renderer.Activate(store));
        AssertEqual(1, stores);
        AssertTrue(fixture.Shell.Renderer.Activate(website));
        AssertEqual(1, websites.Count);
        AssertEqual("https://www.xbox.com/en-US/games/store/minecraft-for-windows/9NBLGGH2JHXJ",
            websites[0].AbsoluteUri);
        AssertEqual("https", websites[0].Scheme);
        AssertEqual("", websites[0].UserInfo);
        scene = fixture.Render();
        AssertTrue(FindByKey(fixture.Shell, scene, "BedrockInstallStatus").Text!.Contains("已请求打开", StringComparison.Ordinal));

        fixture.Shell.Stage.Navigation.Replace(fixture.Home);
        AssertFalse(fixture.Shell.Renderer.Activate(store));
        Emit(fixture.Intents, "ui.bedrock.install.store", store);
        Emit(fixture.Intents, "ui.bedrock.install.website", website);
        fixture.Render();
        AssertEqual(1, stores);
        AssertEqual(1, websites.Count);
        fixture.Shell.Stage.Navigation.Replace(fixture.Controller.Page);
        fixture.Render();
        AssertEqual(1, stores); // Returning does not replay retired commands.
        AssertEqual(1, websites.Count);
    }

    private static void BedrockInstallUnsupportedPlatformOnlyOpensOfficialWebsite()
    {
        int stores = 0;
        Uri? website = null;
        using var fixture = new BedrockInstallFixture(() => stores++, uri => website = uri, supportsStore: false);
        var scene = fixture.Render();
        var store = FindByKey(fixture.Shell, scene, "BedrockInstallStore");
        var browser = FindByKey(fixture.Shell, scene, "BedrockInstallWebsite");
        AssertFalse(store.IsClickable);
        AssertTrue(browser.IsClickable);
        AssertTrue(FindByKey(fixture.Shell, scene, "BedrockInstallStatus").Text!.Contains("不支持", StringComparison.Ordinal));
        AssertFalse(fixture.Shell.Renderer.Activate(store.Entity));
        Emit(fixture.Intents, "ui.bedrock.install.store", store.Entity);
        AssertEqual(0, stores);
        AssertEqual(0, fixture.Feedback.Snapshot().Notifications.Count);
        AssertTrue(fixture.Shell.Renderer.Activate(browser.Entity));
        AssertEqual("https://www.xbox.com/en-US/games/store/minecraft-for-windows/9NBLGGH2JHXJ", website!.AbsoluteUri);
        AssertEqual(0, stores);
        scene = fixture.Render();
        AssertTrue(FindByKey(fixture.Shell, scene, "BedrockInstallStatus").Text!.Contains("需要 Windows", StringComparison.Ordinal));
        AssertFalse(FindByKey(fixture.Shell, scene, "BedrockInstallStore").IsClickable);
    }

    private static void BedrockInstallNativeFaultsAllowRetryAndDisposedActionsRetire()
    {
        const string privateFailure = "fixture-private-native-path";
        int stores = 0, browsers = 0;
        bool fail = true;
        using var fixture = new BedrockInstallFixture(() =>
        {
            stores++;
            if (fail) throw new InvalidOperationException(privateFailure);
        }, _ => { browsers++; throw new IOException(privateFailure); }, supportsStore: true);
        var scene = fixture.Render();
        var store = FindByKey(fixture.Shell, scene, "BedrockInstallStore").Entity;
        var browser = FindByKey(fixture.Shell, scene, "BedrockInstallWebsite").Entity;
        AssertTrue(fixture.Shell.Renderer.Activate(store));
        scene = fixture.Render();
        AssertEqual(1, stores);
        AssertEqual("无法打开 Microsoft Store。请重试，或使用官方产品页。",
            FindByKey(fixture.Shell, scene, "BedrockInstallStatus").Text);
        AssertEqual(DesktopNotificationLevel.Error, fixture.Feedback.Snapshot().Notifications.Single().Level);
        AssertFalse(fixture.Feedback.Snapshot().Notifications.Single().Message.Contains(privateFailure, StringComparison.Ordinal));
        fail = false;
        AssertTrue(fixture.Shell.Renderer.Activate(store));
        AssertEqual(2, stores);
        scene = fixture.Render();
        AssertTrue(FindByKey(fixture.Shell, scene, "BedrockInstallStatus").Text!.Contains("请在商店中完成", StringComparison.Ordinal));
        AssertTrue(fixture.Shell.Renderer.Activate(browser));
        scene = fixture.Render();
        AssertEqual(1, browsers);
        AssertEqual("无法打开官方产品页。请检查默认浏览器后重试。",
            FindByKey(fixture.Shell, scene, "BedrockInstallStatus").Text);
        AssertFalse(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message.Contains(privateFailure, StringComparison.Ordinal)));
        int feedback = fixture.Feedback.Snapshot().Notifications.Count;

        fixture.Shell.Stage.Navigation.Replace(fixture.Home);
        fixture.Controller.Dispose();
        AssertFalse(fixture.Shell.Tree.IsAlive(store));
        Emit(fixture.Intents, "ui.bedrock.install.store", store);
        Emit(fixture.Intents, "ui.bedrock.install.website", browser);
        fixture.Render();
        AssertEqual(2, stores);
        AssertEqual(1, browsers);
        AssertEqual(feedback, fixture.Feedback.Snapshot().Notifications.Count);
    }

    private static void BedrockInstallEffectCannotPublishIntoRetiredPresentation()
    {
        int stores = 0;
        BedrockInstallFixture? fixture = null;
        try
        {
            fixture = new BedrockInstallFixture(() =>
            {
                stores++;
                fixture!.Shell.Stage.Navigation.Replace(fixture.Home);
                fixture.Controller.Dispose();
            }, _ => throw new InvalidOperationException("Unexpected browser dispatch."), supportsStore: true);
            var scene = fixture.Render();
            var button = FindByKey(fixture.Shell, scene, "BedrockInstallStore").Entity;
            AssertTrue(fixture.Shell.Renderer.Activate(button));
            AssertEqual(1, stores);
            AssertEqual(0, fixture.Feedback.Snapshot().Notifications.Count);
            fixture.Render(); // No frame handler touches the retired page after the effect returns.
            Emit(fixture.Intents, "ui.bedrock.install.store", button);
            AssertEqual(1, stores);
        }
        finally { fixture?.Dispose(); }
    }

    private sealed class BedrockInstallFixture : IDisposable
    {
        internal BedrockInstallFixture(Action openStore, Action<Uri> openBrowser, bool supportsStore)
        {
            var state = new XsrStateStoreBuilder().Build();
            Intents = new DesktopUiIntentSink();
            Shell = new XsrUiShell(state, intentSink: Intents);
            Shell.Renderer.ReducedMotion = true;
            Feedback = new DesktopFeedbackService();
            Home = Shell.Tree.Create("bedrock-test-home");
            Controller = new BedrockInstallPageController(Shell, Intents, state, Feedback, openStore, openBrowser, supportsStore);
            Shell.Stage.Navigation.Replace(Controller.Page);
        }

        internal DesktopUiIntentSink Intents { get; }
        internal XsrUiShell Shell { get; }
        internal DesktopFeedbackService Feedback { get; }
        internal BedrockInstallPageController Controller { get; }
        internal XsrUiEntityId Home { get; }
        internal XsrUiScene Render() => Shell.Render(new(900, 650));

        public void Dispose()
        {
            if (Shell.Stage.Navigation.Current == Controller.Page) Shell.Stage.Navigation.Replace(Home);
            Controller.Dispose();
            Feedback.Dispose();
        }
    }
}
