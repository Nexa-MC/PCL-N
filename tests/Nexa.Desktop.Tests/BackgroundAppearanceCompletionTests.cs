using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void BackgroundPresentationPreservesVideoPriorityAndRoutedAppearance()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        var shell = new XsrUiShell(fixture.Store);
        var presentation = DesktopBackgroundPresentation.GetOrCreate(shell.Tree, shell.Content);
        XsrUiColor originalColor = shell.Tree.GetComponent<XsrUiVisualStyle>(shell.Content)!.Background;
        var first = PlayerImageHeader(20, 10);
        var video = PlayerImageHeader(40, 20);
        var replacement = PlayerImageHeader(30, 10);
        void Set(string key, string value) => AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(
            new(key, SettingsLayer.Global, new(SettingsOverrideMode.Custom, value))).IsSuccess);
        DesktopBackgroundAppearance ReadCommitted()
        {
            var settings = CommittedSettingsRead.QueryAsync(fixture.Foundation.Queries, default).AsTask().GetAwaiter().GetResult();
            AssertTrue(settings is not null);
            return DesktopBackgroundAppearance.Read(settings!.Values.ToDictionary(item => item.Key, item => item.Value.Value));
        }
        XsrUiRasterImage Recipe() => shell.Tree.GetComponent<XsrUiRasterImage>(shell.Content)!;
        AssertEqual(XsrUiImageFitMode.Cover, ReadCommitted().FitMode);
        Set("appearance.background-fit", "contain");
        Set("appearance.background-color", "#010203");
        Set("appearance.background-opacity", "25");
        var configured = ReadCommitted();
        shell.Render(new(1000, 650)); // The next color-only update must invalidate an already retained scene.
        presentation.SetAppearance(configured);
        var colorScene = shell.Render(new(1000, 650));
        AssertEqual(new XsrUiColor(1, 2, 3), colorScene.Nodes.Single(node => node.Entity == shell.Content).VisualStyle.Background);
        presentation.SetStatic(first, configured);
        AssertEqual(first.Key, Recipe().Image.Key);
        AssertEqual(XsrUiImageFitMode.Contain, Recipe().FitMode);
        AssertEqual(.25, Recipe().ImageOpacity);
        AssertEqual(new XsrUiColor(1, 2, 3), shell.Tree.GetComponent<XsrUiVisualStyle>(shell.Content)!.Background);
        presentation.SetVideo(video, configured);
        presentation.SetStatic(replacement, configured); // A late PNG load cannot cover a playing video.
        AssertEqual(video.Key, Recipe().Image.Key);
        Set("appearance.background-fit", "stretch");
        Set("appearance.background-opacity", "60");
        presentation.SetAppearance(ReadCommitted());
        AssertEqual(video.Key, Recipe().Image.Key);
        AssertEqual(XsrUiImageFitMode.Stretch, Recipe().FitMode);
        AssertEqual(.6, Recipe().ImageOpacity);
        presentation.ClearVideo();
        AssertEqual(replacement.Key, Recipe().Image.Key); // Restore the latest static carrier and current policy.
        AssertEqual(.6, Recipe().ImageOpacity);
        Set("appearance.background-color", "auto");
        presentation.SetAppearance(ReadCommitted());
        AssertEqual(originalColor, shell.Tree.GetComponent<XsrUiVisualStyle>(shell.Content)!.Background);
        presentation.ResetStatic();
        AssertTrue(shell.Tree.GetComponent<XsrUiRasterImage>(shell.Content) is null);
        foreach (string invalid in new[] { "red", "#11223344", "#12 456", "#GGGGGG", " auto", "AUTO", "" })
        {
            bool rejected = false;
            try { DesktopBackgroundAppearance.ParseColor(invalid); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected);
        }
    }
}
