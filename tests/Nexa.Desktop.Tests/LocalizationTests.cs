using System.Globalization;
using System.Xml.Linq;
using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void RegionFormattingUsesStartupPolicyAndRestoresCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo? originalDefault = CultureInfo.DefaultThreadCurrentCulture;
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var policy = fixture.Foundation.Host.SettingsPolicy;
        void Set(string key, string value) => AssertTrue(policy.Set(new(key, SettingsLayer.Global, new(SettingsOverrideMode.Custom, value))).IsSuccess);
        Set("general.language", "en");
        using (var session = new DesktopLanguageSession(fixture.Shell, fixture.Store, "zh-CN", CultureInfo.GetCultureInfo("fr-FR")))
        {
            AssertEqual("fr-FR", CultureInfo.CurrentCulture.Name); // UI language and system format are independent.
            AssertEqual("Settings", fixture.Shell.Renderer.LocalizeText("设置"));
            Set("general.region", "zh-TW"); fixture.Shell.Render(new(1000, 650));
            AssertEqual("fr-FR", CultureInfo.CurrentCulture.Name); // Restart preference, not a live mutation.
        }
        AssertEqual(original, CultureInfo.CurrentCulture);
        AssertEqual(originalDefault, CultureInfo.DefaultThreadCurrentCulture);
        using (var session = new DesktopLanguageSession(fixture.Shell, fixture.Store, "zh-CN", CultureInfo.GetCultureInfo("fr-FR")))
        {
            AssertEqual("zh-TW", CultureInfo.CurrentCulture.Name);
            DateTime date = new(2026, 10, 3);
            AssertEqual(date.ToString("d", CultureInfo.GetCultureInfo("zh-TW")), date.ToString("d", CultureInfo.CurrentCulture));
        }
        foreach (string preference in new[] { "follow-language", "ui-language" })
        {
            Set("general.region", preference); Set("general.language", "zh-Hant");
            using var session = new DesktopLanguageSession(fixture.Shell, fixture.Store, "zh-CN", CultureInfo.GetCultureInfo("fr-FR"));
            AssertEqual("zh-TW", CultureInfo.CurrentCulture.Name);
            Set("general.language", "en"); fixture.Shell.Render(new(1000, 650));
            AssertEqual("en-US", CultureInfo.CurrentCulture.Name);
        }
        AssertEqual(original, CultureInfo.CurrentCulture);
        AssertEqual(originalDefault, CultureInfo.DefaultThreadCurrentCulture);
    }

    private static void RegionSelectorPreservesCustomCulturesAndSavesPresets()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.region", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "fr-FR"))).IsSuccess);
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1200, 650));
        var track = FindByKey(fixture.Shell, scene, "SettingsSelector.general.region");
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiScrollGesture>(track.Entity) is not null);
        var custom = FindByKey(fixture.Shell, scene, "SettingsOption.general.region.fr-FR");
        AssertEqual(CultureInfo.GetCultureInfo("fr-FR").NativeName, custom.Text);
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiSelection>(custom.Entity)!.IsSelected);
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(custom.Entity)!.Localize);
        var option = FindByKey(fixture.Shell, scene, "SettingsOption.general.region.en-US");
        Emit(fixture.Intents, "ui.settings.choice", option.Entity); fixture.Shell.Render(new(1200, 650));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Settings.GetValue<string>("UiFormatCulture").Value == "en-US", TimeSpan.FromSeconds(5)));
        scene = fixture.Shell.Render(new(760, 650));
        var constrained = FindByKey(fixture.Shell, scene, "SettingsSelector.general.region");
        AssertTrue(constrained.Rect.Width < track.Rect.Width);
    }

    private static void LanguageCatalogCoversLocalesTemplatesAndFallbacks()
    {
        UiLocalizationCatalog catalog = new();
        catalog.SetLanguage("en");
        AssertEqual("Since Minecraft last ran successfully:\nAdded 12 Mods\nmods/custom.jar",
            catalog.Translate("Minecraft 上一次成功运行以后：\n新增 12 项模组\nmods/custom.jar"));
        catalog.SetLanguage("zh-Hans");
        foreach (string locale in new[] { "zh-TW", "zh_HK", "zh-MO", "zh-Hant" }) AssertEqual("zh-Hant", UiLocalizationCatalog.ResolveLanguage("auto", locale));
        foreach (string locale in new[] { "zh-CN", "zh_SG", "zh-Hans" }) AssertEqual("zh-Hans", UiLocalizationCatalog.ResolveLanguage("auto", locale));
        AssertEqual("en", UiLocalizationCatalog.ResolveLanguage("auto", "fr-FR"));
        catalog.SetLanguage("en");
        AssertEqual("Settings", catalog.Translate("设置"));
        AssertEqual("Content dependencies", catalog.Translate("内容依赖"));
        AssertEqual("1 Mod identity", catalog.Translate("共 1 个模组身份"));
        AssertEqual("2000 Mod identities", catalog.Translate("共 2000 个模组身份"));
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
