using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexa.Desktop.Ui;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.Desktop.Tests;

// Separate process: actual product PXML, settings routes/controller and native backend.
// Only its temporary local data root and empty instance inventory are fixtures.
internal static partial class Program
{
    private static int RunNativeSettingsPreview(string theme, string outputDirectory)
    {
        if (theme is not ("light" or "dark"))
        {
            Console.Error.WriteLine("Expected --native-settings-preview light|dark outputDirectory.");
            return 2;
        }
        return RunNativeSettingsPreviewAsync(theme, Path.GetFullPath(outputDirectory)).GetAwaiter().GetResult();
    }

    private static async Task<int> RunNativeSettingsPreviewAsync(string theme, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        bool dark = theme == "dark";
        await using var startup = await AvaloniaUiStartupSession.StartWithAppearanceAsync([], disableHardwareAcceleration: true,
            new AvaloniaUiStartupAppearance(dark ? XsrUiThemeMode.Dark : XsrUiThemeMode.Light, ReducedMotion: true),
            cancellationToken: timeout.Token);
        LaunchPageFixture? fixture = null;
        SettingsPageController? settings = null;
        DesktopAppearanceSession? appearance = null;
        DesktopPresentationSession? presentation = null;
        AvaloniaUiShellWindow? window = null;
        AvaloniaUiSceneSurface? surface = null;
        Task<int>? lifetime = null;
        var actions = new AvaloniaUiPlatformActions();
        int outcome = 1;
        try
        {
            await startup.InvokeAsync(async () =>
            {
                fixture = new(new ImmediateInstanceSource([]));
                AssertTrue(fixture.Foundation.Host.Settings.SetValue("UiDarkMode", dark ? 1 : 0).IsSuccess);
                appearance = new(fixture.Shell, fixture.Store, actions);
                presentation = new(fixture.Shell, fixture.Store, actions.SetWindowResizeEnabled,
                    actions.SetAnimationFrameRate, fixture.Foundation.Queries, action => Dispatcher.UIThread.Post(action));
                settings = new(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
                    fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
                fixture.Controller.SettingsPage = settings.Page;
                await settings.PrepareStartupAsync(timeout.Token);
                Emit(fixture.Intents, "ui.navigation.settings");
                fixture.Shell.Render(new(850, 500));
            }, timeout.Token);
            await presentation!.InitialReady.WaitAsync(timeout.Token);
            var f = fixture!;
            var controller = settings!;
            await startup.PrepareShellAsync(f.Shell, actions, timeout.Token);
            await startup.WarmUpShellAsync(f.Shell, timeout.Token);
            lifetime = Task.Run(() => AvaloniaUiShellHost.Run(f.Shell, platformActions: actions));
            await Eventually(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow is not AvaloniaUiShellWindow candidate || !candidate.IsVisible) return false;
                window = candidate;
                surface = candidate.GetVisualDescendants().OfType<AvaloniaUiSceneSurface>().Single();
                candidate.Width = 1040; candidate.Height = 740;
                if (candidate.Screens.Primary is { } screen)
                    candidate.Position = new(screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - (int)(1040 * candidate.RenderScaling)) / 2),
                        screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - (int)(740 * candidate.RenderScaling)) / 2));
                candidate.UpdateLayout(); surface.CommitScene();
                return surface.Scene is { Count: > 0 };
            });
            await SelectCategory("appearance");
            await Eventually(() => !controller.SettingsWritePending && f.Shell.Renderer.ColorScheme.IsDark == dark
                && AvaloniaUiRuntimeDiagnostics.Capture().ActiveMotionTracks == 0);
            await startup.InvokeAsync(() =>
            {
                bool hasRetiredSetting = false;
                f.Shell.Tree.Walk(controller.Page, entity =>
                {
                    string? text = f.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content;
                    if (text?.Contains("超低功耗", StringComparison.Ordinal) == true
                        || text?.Contains("低功耗模式", StringComparison.Ordinal) == true) hasRetiredSetting = true;
                    return true;
                });
                AssertFalse(hasRetiredSetting);
                Capture("top");
            }, timeout.Token);
            await Task.Delay(1200, timeout.Token); // Display hold for the external X11 recording.

            // Real pointer-scroll admission and live viewport, rather than a replacement layout.
            for (int step = 0; step < 128; step++)
            {
                bool ready = false, atEnd = false;
                await startup.InvokeAsync(() =>
                {
                    surface!.CommitScene();
                    var body = FindByKey(f.Shell, surface.Scene!, "SettingsSections");
                    bool Visible(string label) => surface.Scene!.Nodes.Any(node => node.Text == label
                        && node.Rect.Y >= body.Rect.Y && node.Rect.Y + node.Rect.Height <= body.Rect.Y + body.Rect.Height);
                    ready = Visible("UI 动画帧率") && Visible("减少动态效果");
                    atEnd = body.Scroll is { } scroll && scroll.OffsetY >= scroll.MaximumOffsetY;
                    if (!ready && !atEnd)
                    {
                        AssertTrue(f.Shell.Renderer.PointerScroll(new(body.Rect.X + 30, body.Rect.Y + 30), 80));
                        surface.CommitScene();
                    }
                }, timeout.Token);
                if (ready) break;
                if (atEnd || step == 127) throw new InvalidOperationException("The actual animation settings did not enter the preview viewport.");
                await Task.Delay(40, timeout.Token);
            }
            await startup.InvokeAsync(() => Capture("motion"), timeout.Token);
            await Task.Delay(1200, timeout.Token);
            await startup.InvokeAsync(() =>
            {
                var body = FindByKey(f.Shell, surface!.Scene!, "SettingsSections");
                var scroll = f.Shell.Tree.GetComponent<XsrUiScroll>(body.Entity)!;
                scroll.OffsetY = body.Scroll!.Value.MaximumOffsetY;
                f.Shell.Tree.MarkDirty(body.Entity, XsrUiDirtyKinds.Layout);
                surface.CommitScene(); Capture("bottom");
            }, timeout.Token);
            await Task.Delay(1200, timeout.Token);
            await startup.InvokeAsync(() =>
            {
                var body = FindByKey(f.Shell, surface!.Scene!, "SettingsSections");
                f.Shell.Tree.GetComponent<XsrUiScroll>(body.Entity)!.OffsetY = 0;
                f.Shell.Tree.MarkDirty(body.Entity, XsrUiDirtyKinds.Layout);
                surface.CommitScene();
                var option = FindByKey(f.Shell, surface.Scene!, "SettingsOption.appearance.theme-mode." + (dark ? "0" : "1"));
                Emit(f.Intents, "ui.settings.choice", option.Entity);
                surface.CommitScene();
            }, timeout.Token);
            await Eventually(() => !controller.SettingsWritePending && f.Shell.Renderer.ColorScheme.IsDark != dark
                && surface!.Scene!.Nodes.Any(node => f.Shell.Tree.Name(node.Entity)
                    == "SettingsOption.appearance.theme-mode." + (dark ? "0" : "1") && node.IsSelected));
            await startup.InvokeAsync(() => Capture("theme-switch"), timeout.Token);
            await Task.Delay(1200, timeout.Token);
            await SelectCategory("general");
            await Task.Delay(600, timeout.Token);
            await SelectCategory("appearance");
            await Task.Delay(1000, timeout.Token);
            outcome = 0;

            async Task SelectCategory(string id)
            {
                await startup.InvokeAsync(() =>
                {
                    surface!.CommitScene();
                    Emit(f.Intents, "ui.settings.section", FindByKey(f.Shell, surface.Scene!, "SettingsNav." + id).Entity);
                    surface.CommitScene();
                }, timeout.Token);
                await Eventually(() => controller.SelectedSection == id);
            }

            async Task Eventually(Func<bool> condition)
            {
                while (true)
                {
                    bool ready = false;
                    await startup.InvokeAsync(() => { surface?.CommitScene(); ready = condition(); }, timeout.Token);
                    if (ready) return;
                    await Task.Delay(10, timeout.Token);
                }
            }

            void Capture(string phase)
            {
                window!.UpdateLayout(); surface!.CommitScene();
                double scale = window.RenderScaling;
                using var frame = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                    (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                frame.Render(window);
                string stem = "settings-" + theme + "-" + phase;
                frame.Save(Path.Combine(outputDirectory, stem + ".png"), PngBitmapEncoderOptions.Default);
                var controls = new JsonArray();
                foreach (var node in surface.Scene!.Nodes.Where(node => node.IsClickable
                    || f.Shell.Tree.Name(node.Entity) == "SettingsSections"))
                {
                    PixelPoint point = surface.PointToScreen(new(node.Rect.X + node.Rect.Width / 2,
                        node.Rect.Y + node.Rect.Height / 2));
                    JsonNode control = new JsonObject
                    {
                        ["key"] = f.Shell.Tree.Name(node.Entity),
                        ["label"] = node.Label,
                        ["x"] = point.X,
                        ["y"] = point.Y,
                        ["visible"] = node.ClipRect is null
                            || node.ClipRect.Value.Contains(new(node.Rect.X + node.Rect.Width / 2, node.Rect.Y + node.Rect.Height / 2))
                    };
                    controls.Add(control);
                }
                PixelPoint origin = window.PointToScreen(default);
                var metadata = new JsonObject
                {
                    ["page"] = "SettingsAppearance",
                    ["phase"] = phase,
                    ["theme"] = f.Shell.Renderer.ColorScheme.IsDark ? "dark" : "light",
                    ["data"] = "temporary local settings; empty instance inventory",
                    ["renderer"] = "actual product PXML/controller and software Avalonia native window",
                    ["version"] = f.Shell.Version,
                    ["x"] = origin.X,
                    ["y"] = origin.Y,
                    ["width"] = frame.PixelSize.Width,
                    ["height"] = frame.PixelSize.Height,
                    ["controls"] = controls
                };
                File.WriteAllText(Path.Combine(outputDirectory, stem + ".json"), metadata.ToJsonString());
                File.WriteAllText(Path.Combine(outputDirectory, stem + ".ready"), stem);
                Console.WriteLine("PREVIEW READY: " + stem);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Console.Error.WriteLine("FAIL: native settings preview\n" + error);
        }
        finally
        {
            void Cleanup()
            {
                presentation?.Dispose(); appearance?.Dispose(); settings?.Dispose();
                if (window is not null) { fixture!.Shell.Renderer.ReducedMotion = true; window.Close(); }
                fixture?.Dispose();
            }
            if (!startup.Completion.IsCompleted) await Dispatcher.UIThread.InvokeAsync(Cleanup);
            else Cleanup();
            if (lifetime is not null) AssertEqual(0, await lifetime.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        if (outcome == 0) Console.WriteLine("PASS: native settings previews capture actual appearance/motion settings and confirmed theme/category transitions.");
        return outcome;
    }
}
