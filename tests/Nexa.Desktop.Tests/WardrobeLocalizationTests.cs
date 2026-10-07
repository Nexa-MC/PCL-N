using Nexa.Desktop.Ui;
using Nexa.Services.Accounts;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void WardrobeLocalesTranslateDynamicCaptionsAndPreserveArguments()
    {
        UiLocalizationCatalog catalog = new();
        catalog.SetLanguage("en");
        AssertEqual("1 item", catalog.Translate("1 项"));
        AssertEqual("37 items", catalog.Translate("37 项"));
        AssertEqual("Page 3", catalog.Translate("第 3 页"));
        AssertEqual("Page 3 · 37 items · Blessing Skin 皮肤 {0}", catalog.Translate("第 3 页 · 37 项 · Blessing Skin 皮肤 {0}"));
        AssertEqual("Uploaded by 皮肤 {0} / 搜索皮肤", catalog.Translate("由 皮肤 {0} / 搜索皮肤 上传"));
        AssertEqual("♥ 42 · Cape · HD", catalog.Translate("♥ 42 · 披风 · HD"));
        AssertEqual("Front · Change view", catalog.Translate("Front · 切换视角"));
        AssertEqual("The texture card has expired. Refresh the wardrobe.", catalog.Translate("材质卡片已失效，请刷新更衣橱。"));
        AssertEqual("The skin site did not provide a valid texture preview.", catalog.Translate("皮肤站未提供有效的纹理预览。"));
        catalog.SetLanguage("zh-Hant");
        AssertEqual("37 項", catalog.Translate("37 项"));
        AssertEqual("第 3 頁", catalog.Translate("第 3 页"));
        AssertEqual("第 3 頁 · 37 項 · Blessing Skin 皮肤 {0}", catalog.Translate("第 3 页 · 37 项 · Blessing Skin 皮肤 {0}"));
        AssertEqual("由 皮肤 {0} / 搜索皮肤 上傳", catalog.Translate("由 皮肤 {0} / 搜索皮肤 上传"));
        AssertEqual("♥ 42 · 披風 · HD", catalog.Translate("♥ 42 · 披风 · HD"));
        AssertEqual("正面 · 切換視角", catalog.Translate("正面 · 切换视角"));
        AssertEqual("材質卡片已失效，請重新整理更衣櫃。", catalog.Translate("材质卡片已失效，请刷新更衣橱。"));
        catalog.SetLanguage("zh-Hans");
        AssertEqual("37 项", catalog.Translate("37 项"));
    }

    private static void WardrobeScenesLocalizeControlsAndKeepProviderNamesLiteral()
    {
        foreach (string locale in new[] { "en", "zh-Hant" })
        {
            using LaunchPageFixture fixture = new(new ImmediateInstanceSource([]));
            fixture.Service.ConfigureRegionalPolicy(new("CN"));
            AssertTrue(fixture.Service.AddProfile(new LaunchProfile
            { Username = "皮肤", Info = "皮肤", Kind = LaunchProfileKind.Microsoft, Uuid = "literal-user" }).IsSuccess);
            UiLocalizationCatalog catalog = new();
            catalog.SetLanguage(locale);
            fixture.Shell.Renderer.TextLocalizer = catalog.Translate;
            var current = new AccountWardrobeResolvedTextures("https://textures.minecraft.net/current", null, false, WardrobeDesktopSkin(), null);
            var queries = new XsrQueryRouterBuilder();
            queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, (_, _) =>
                ValueTask.FromResult(XsrResult.Success(WardrobeDesktopSnapshot(fixture, "皮肤") with
                {
                    Current = current,
                    Skins = [new("literal", "搜索皮肤", "此前使用", current, true), new("other", "皮肤", "其他档案", current, true)]
                })));
            queries.Register<WardrobeCatalogSitesQuery, IReadOnlyList<WardrobeCatalogSite>>(WardrobeCatalogContract.Sites, (_, _) =>
                ValueTask.FromResult(XsrResult.Success<IReadOnlyList<WardrobeCatalogSite>>([
                    new("littleskin", "设置", new("https://littleskin.cn/"), new("https://docs.littleskin.cn/"), "lucide/palette", true)])));
            queries.Register<WardrobeCatalogQuery, WardrobeCatalogPage>(WardrobeCatalogContract.Read, (query, _) =>
                ValueTask.FromResult(XsrResult.Success(new WardrobeCatalogPage(query.SiteId, "设置", "皮肤 {0}", query.Page, false, false,
                    [LibraryItem(901, query.Kind) with { Name = "搜索皮肤", Uploader = "皮肤 {0}" }]))));
            var router = queries.Build(new NoopDispatchObserver());
            var commands = new XsrCommandRouterBuilder().Build(new NoopDispatchObserver());
            using var wardrobe = new WardrobePageController(fixture.Shell, fixture.Intents, router, commands, fixture.Store, fixture.Feedback);
            wardrobe.ConfigureFilePicker(_ => Task.FromResult<string?>(null));
            fixture.Shell.Stage.Navigation.Replace(wardrobe.Page);
            WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeSkinCount") is "2 项" or "2 items" or "2 項");
            var scene = fixture.Shell.Render(new(1200, 1200));
            AssertEqual("皮肤", FindByKey(fixture.Shell, scene, "WardrobeIdentity").Text);
            AssertEqual("皮肤", FindByKey(fixture.Shell, scene, "WardrobeProfileKind").Text);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, "WardrobeProfileKind"))!.Localize);
            AssertEqual("搜索皮肤", FindByKey(fixture.Shell, scene, "WardrobeSkinTitle.literal").Text);
            AssertEqual(locale == "en" ? "2 items" : "2 項", FindByKey(fixture.Shell, scene, "WardrobeSkinCount").Text);
            AssertEqual(locale == "en" ? "Other profiles" : "其他檔案", FindByKey(fixture.Shell, scene, "WardrobeSkinSource.other").Text);
            AssertEqual(locale == "en" ? "Front · Change view" : "正面 · 切換視角", FindByKey(fixture.Shell, scene, "WardrobeCurrentView").Text);
            AssertEqual(locale == "en" ? "Open the skin library" : "打開皮膚庫", FindByKey(fixture.Shell, scene, "WardrobeLibrary").Text);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, "WardrobeIdentity"))!.Localize);
            using var library = new WardrobeLibraryPageController(fixture.Shell, fixture.Intents, router, commands, fixture.Store, fixture.Feedback);
            fixture.Shell.Stage.Navigation.Replace(library.Page);
            LibraryPump(fixture, () => LibraryVisible(fixture, "WardrobeLibraryItems"));
            scene = fixture.Shell.Render(new(1200, 1200));
            AssertEqual("设置", FindByKey(fixture.Shell, scene, "WardrobeLibrarySiteName").Text);
            AssertEqual("搜索皮肤", FindByKey(fixture.Shell, scene, "WardrobeLibraryCard.901.Name").Text);
            AssertEqual(locale == "en" ? "Uploaded by 皮肤 {0}" : "由 皮肤 {0} 上傳", FindByKey(fixture.Shell, scene, "WardrobeLibraryCard.901.Uploader").Text);
            AssertEqual("♥ 42 · Slim · HD", FindByKey(fixture.Shell, scene, "WardrobeLibraryCard.901.Metadata").Text);
            AssertEqual(locale == "en" ? "Isometric · Change view" : "立體 · 切換視角", FindByKey(fixture.Shell, scene, "WardrobeLibraryCard.901.View").Text);
            AssertEqual(locale == "en" ? "Page 1 · 1 items · Blessing Skin 皮肤 {0}" : "第 1 頁 · 1 項 · Blessing Skin 皮肤 {0}", FindByKey(fixture.Shell, scene, "WardrobeLibrarySiteStatus").Text);
            foreach (string key in new[] { "WardrobeLibrarySiteName", "WardrobeLibraryCard.901.Name", "WardrobeLibraryCard.901.Uploader", "WardrobeLibrarySiteStatus" })
                AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, key))!.Localize);
            AssertTrue(fixture.Service.AddProfile(new LaunchProfile
            { Username = "Other", Info = "皮肤", Kind = LaunchProfileKind.ThirdParty, Uuid = "third-party-user", AuthServer = "https://skins.example/api/yggdrasil" }).IsSuccess);
            AssertTrue(fixture.Service.SelectProfile(1) is null);
            fixture.Shell.Stage.Navigation.Replace(wardrobe.Page);
            WardrobePump(fixture, () => WardrobeText(fixture, "WardrobeIdentity") == "Other");
            scene = fixture.Shell.Render(new(1200, 1200));
            AssertEqual(locale == "en" ? "Third-party · skins.example" : "第三方 · skins.example", FindByKey(fixture.Shell, scene, "WardrobeProfileKind").Text);
            AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiText>(FindEntity(fixture.Shell, "WardrobeProfileKind"))!.Localize);
        }
    }
}
