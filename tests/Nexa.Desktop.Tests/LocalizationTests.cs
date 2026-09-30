using System.Xml.Linq;
using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void LanguageCatalogCoversLocalesTemplatesAndFallbacks()
    {
        UiLocalizationCatalog catalog = new();
        foreach (string locale in new[] { "zh-TW", "zh_HK", "zh-MO", "zh-Hant" }) AssertEqual("zh-Hant", UiLocalizationCatalog.ResolveLanguage("auto", locale));
        foreach (string locale in new[] { "zh-CN", "zh_SG", "zh-Hans" }) AssertEqual("zh-Hans", UiLocalizationCatalog.ResolveLanguage("auto", locale));
        AssertEqual("en", UiLocalizationCatalog.ResolveLanguage("auto", "fr-FR"));
        catalog.SetLanguage("en");
        AssertEqual("Settings", catalog.Translate("设置"));
        AssertEqual("1 version", catalog.Translate("1 个版本"));
        AssertEqual("8 versions", catalog.Translate("8 个版本"));
        AssertEqual("Show Fabric installation options", catalog.Translate("显示 Fabric 安装选项"));
        AssertEqual("not in the catalog", catalog.Translate("not in the catalog"));
        const string username = "用户 {0} / My profile";
        // Captured arguments stay literal even when they contain braces or translated captions.
        AssertEqual("Select " + username, catalog.Translate("选择 " + username));
        catalog.SetLanguage("zh-Hant");
        AssertEqual("設定", catalog.Translate("设置"));
        AssertEqual("官方資料夾", catalog.Translate("官方文件夹"));
        AssertEqual("8 個版本", catalog.Translate("8 个版本"));
        catalog.SetLanguage("zh-Hans");
        AssertEqual("设置", catalog.Translate("设置"));
        using var stream = typeof(UiLocalizationCatalog).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.Localization.messages.json")!;
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            string source = entry.GetProperty("source").GetString()!;
            string traditional = entry.GetProperty("zh-Hant").GetString()!;
            AssertFalse(traditional.Contains('?') && !source.Contains('?'));
            AssertTrue(entry.GetProperty("en").GetString()!.Length > 0);
            catalog.SetLanguage("en"); AssertEqual(entry.GetProperty("en").GetString(), catalog.Translate(source));
            catalog.SetLanguage("zh-Hant"); AssertEqual(traditional, catalog.Translate(source));
        }
        catalog.SetLanguage("en");
        foreach (string resource in typeof(UiLocalizationCatalog).Assembly.GetManifestResourceNames().Where(name => name.EndsWith(".pxml", StringComparison.Ordinal)))
        {
            using var markup = typeof(UiLocalizationCatalog).Assembly.GetManifestResourceStream(resource)!;
            var document = System.Xml.Linq.XDocument.Load(markup);
            foreach (var attribute in document.Descendants().Attributes().Where(attribute => attribute.Name.LocalName is "Content" or "Label" or "Placeholder"))
            {
                string source = attribute.Value;
                if (source.Any(character => character is >= '\u4e00' and <= '\u9fff'))
                    AssertFalse(catalog.Translate(source) == source);
            }
        }
    }

    private static void LanguageSettingSwitchesLiveAndPreservesControls()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var language = new DesktopLanguageSession(fixture.Shell, fixture.Store, "zh-CN");
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries, fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true;
        fixture.Controller.SettingsPage = settings.Page;
        Emit(fixture.Intents, "ui.navigation.settings");
        var scene = fixture.Shell.Render(new(1000, 650));
        var english = FindByKey(fixture.Shell, scene, "SettingsOption.general.language.en").Entity;
        var traditional = FindByKey(fixture.Shell, scene, "SettingsOption.general.language.zh-Hant").Entity;
        var simplified = FindByKey(fixture.Shell, scene, "SettingsOption.general.language.zh-Hans").Entity;
        Emit(fixture.Intents, "ui.settings.choice", english);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<string>("UiLanguage").Value == "en", TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("General", FindByKey(fixture.Shell, scene, "SettingsNav.general").Text);
        AssertEqual(english, FindByKey(fixture.Shell, scene, "SettingsOption.general.language.en").Entity);
        AssertEqual("简体中文", FindByKey(fixture.Shell, scene, "SettingsOption.general.language.zh-Hans").Text);
        AssertEqual("en", fixture.Store.Read<string>(fixture.Store.Resolve(Nexa.Xsr.XsrSemanticId.Parse("UiLanguage"))).Value);
        var navigation = FindByKey(fixture.Shell, scene, "SettingsNav.network");
        AssertTrue(navigation.Rect.Width >= "Downloads & Network".Length * 7);
        Emit(fixture.Intents, "ui.settings.choice", traditional);
        fixture.Shell.Render(new(760, 500));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<string>("UiLanguage").Value == "zh-Hant", TimeSpan.FromSeconds(5)));
        fixture.Shell.Render(new(760, 500)); scene = fixture.Shell.Render(new(760, 500));
        AssertEqual("通用", FindByKey(fixture.Shell, scene, "SettingsNav.general").Text);
        AssertEqual("zh-Hant", fixture.Store.Read<string>(fixture.Store.Resolve(Nexa.Xsr.XsrSemanticId.Parse("UiLanguage"))).Value);
        Emit(fixture.Intents, "ui.settings.choice", simplified);
        fixture.Shell.Render(new(1000, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<string>("UiLanguage").Value == "zh-Hans", TimeSpan.FromSeconds(5)));
        fixture.Shell.Render(new(1000, 650)); scene = fixture.Shell.Render(new(1000, 650));
        AssertEqual("zh-Hans", fixture.Store.Read<string>(fixture.Store.Resolve(Nexa.Xsr.XsrSemanticId.Parse("UiLanguage"))).Value);
        AssertEqual(english, FindByKey(fixture.Shell, scene, "SettingsOption.general.language.en").Entity);
    }

    private static void FirstRunUsesSystemLanguageWithoutSettingsState()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var state = new XsrStateStoreBuilder().Build();
        using var language = new DesktopLanguageSession(fixture.Shell, state, "en-US");
        AssertEqual("Settings", fixture.Shell.Renderer.LocalizeText("设置"));
    }
}
