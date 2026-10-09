using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexa.Core.Media;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using SkiaSharp;

namespace Nexa.Desktop.Tests;

// Separate native process: actual settings controller, scene and window, with bounded
// typed screenshot fixtures. Markers let an external recorder capture actual screen pixels.
internal static partial class Program
{
    private static int RunNativeScreenshotPreview(string theme, string outputDirectory) =>
        RunNativeScreenshotPreviewAsync(theme, Path.GetFullPath(outputDirectory)).GetAwaiter().GetResult();

    private static async Task<int> RunNativeScreenshotPreviewAsync(string theme, string outputDirectory)
    {
        if (theme is not ("light" or "dark")) throw new ArgumentException("Expected light or dark.", nameof(theme));
        Directory.CreateDirectory(outputDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        await using var startup = await AvaloniaUiStartupSession.StartWithAppearanceAsync([], disableHardwareAcceleration: true,
            new AvaloniaUiStartupAppearance(theme == "dark" ? XsrUiThemeMode.Dark : XsrUiThemeMode.Light, ReducedMotion: true),
            cancellationToken: timeout.Token);
        LaunchPageFixture? fixture = null;
        SettingsPageController? settings = null;
        DesktopAppearanceSession? appearance = null;
        AvaloniaUiShellWindow? window = null;
        AvaloniaUiSceneSurface? surface = null;
        Task<int>? lifetime = null;
        var actions = new AvaloniaUiPlatformActions();
        actions.SetDesktopPolicy(new(TrayEnabled: false));
        int outcome = 1;
        try
        {
            await startup.InvokeAsync(() =>
            {
                fixture = new(new ImmediateInstanceSource([]));
                var configuredFixture = fixture;
                AssertTrue(configuredFixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global,
                    new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
                AssertTrue(configuredFixture.Foundation.Host.SettingsPolicy.Set(new("appearance.theme-mode", SettingsLayer.Global,
                    new(SettingsOverrideMode.Custom, theme == "dark" ? "1" : "0"))).IsSuccess);
                appearance = new(configuredFixture.Shell, configuredFixture.Store, actions);
                string instance = Path.Combine(outputDirectory, "fixture-instance");
                string directory = Path.Combine(instance, "screenshots");
                Directory.CreateDirectory(directory);
                Dictionary<string, PngImage> images = new(StringComparer.Ordinal);
                List<InstanceContentEntry> entries = [];
                (int Width, int Height)[] shapes = [(960, 540), (500, 840), (640, 640), (1200, 360), (800, 600), (520, 780)];
                for (int index = 0; index < 24; index++)
                {
                    var shape = shapes[index % shapes.Length];
                    string name = "2026-10-09_" + (18 - index / 12).ToString("00", CultureInfo.InvariantCulture)
                        + "-" + (index % 12 * 5).ToString("00", CultureInfo.InvariantCulture) + "-00.png";
                    var image = CreateScreenshotPreviewFixture(shape.Width, shape.Height, index);
                    string path = Path.Combine(directory, name);
                    File.WriteAllBytes(path, image.Bytes.ToArray());
                    File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc).AddMinutes(-index * 5));
                    images.Add(name, image);
                    entries.Add(new(name, false, image.Bytes.Length)
                    {
                        ModifiedUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
                        ImageWidth = shape.Width,
                        ImageHeight = shape.Height,
                        // Alternate encoded and header-only metadata to exercise visible reads.
                        Icon = index % 2 == 0 ? image : null,
                    });
                }
                var queries = new XsrQueryRouterBuilder();
                configuredFixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog);
                configuredFixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
                queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
                    (q, ct) => configuredFixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, q, cancellationToken: ct));
                queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
                    (q, ct) => configuredFixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, q, cancellationToken: ct));
                queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (q, _) =>
                    ValueTask.FromResult(XsrResult.Success(new InstanceManagementSnapshot(q.InstanceDirectory, instance, "1.21.1", [],
                        [new("overview", "总览"), new("game", "游戏设置"), new("screenshots", "截图", directory)], true, "")
                    { Contents = [new("screenshots", entries.AsReadOnly(), true, null)] })));
                queries.Register<InstanceScreenshotQuery, InstanceScreenshot>(InstanceScreenshotContract.Read, (q, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = entries.Single(item => item.Name == q.Name);
                    AssertEqual(instance, q.InstanceDirectory);
                    AssertEqual(entry.Size!.Value, q.ExpectedSize);
                    AssertEqual(entry.ModifiedUtcTicks, q.ExpectedModifiedUtcTicks);
                    return ValueTask.FromResult(XsrResult.Success(new InstanceScreenshot(Path.Combine(directory, q.Name), images[q.Name])));
                });
                var commands = new XsrCommandRouterBuilder();
                commands.Register<InstanceScreenshotCropCommand>(InstanceScreenshotContract.Crop, (command, _) =>
                    ValueTask.FromResult(XsrResult.Success()));
                commands.Register<InstanceContentRemoveCommand>(InstanceManagementContract.RemoveContent, (command, _) =>
                    ValueTask.FromResult(XsrResult.Success()));
                settings = new(configuredFixture.Shell, configuredFixture.Intents, queries.Build(new NoopDispatchObserver()), commands.Build(new NoopDispatchObserver()),
                    configuredFixture.Store, configuredFixture.Feedback, () => instance)
                {
                    CopyScreenshotAsync = _ => Task.CompletedTask,
                    ShareScreenshotAsync = (_, _, _) => Task.CompletedTask,
                    OpenManagementDirectory = _ => { },
                };
                configuredFixture.Controller.SettingsPage = settings.Page;
                Emit(configuredFixture.Intents, "ui.navigation.settings");
                configuredFixture.Shell.Render(new(1000, 700));
            }, timeout.Token);
            var f = fixture!;
            await startup.PrepareShellAsync(f.Shell, actions, timeout.Token);
            await startup.WarmUpShellAsync(f.Shell, timeout.Token);
            lifetime = Task.Run(() => AvaloniaUiShellHost.Run(f.Shell, platformActions: actions));
            await Eventually(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow is not AvaloniaUiShellWindow candidate || !candidate.IsVisible) return false;
                window = candidate;
                surface = candidate.GetVisualDescendants().OfType<AvaloniaUiSceneSurface>().Single();
                candidate.Width = 1080; candidate.Height = 740; candidate.Position = new(100, 30);
                candidate.UpdateLayout(); surface.CommitScene();
                return Has("SettingsNav.screenshots");
            });
            await startup.InvokeAsync(() => AssertEqual(theme == "dark", f.Shell.Renderer.ColorScheme.IsDark), timeout.Token);
            await Click("SettingsNav.screenshots", "ui.settings.section");
            await Eventually(() => surface!.Scene!.Nodes.Any(node =>
                f.Shell.Tree.Name(node.Entity).StartsWith("ManagementScreenshot.", StringComparison.Ordinal)));
            await Capture("gallery");
            double offset = 0;
            await startup.InvokeAsync(() =>
            {
                var sections = Find("SettingsSections");
                AssertTrue(f.Shell.Renderer.PointerScroll(new(sections.Rect.X + 30, sections.Rect.Y + 40), 360));
                surface!.CommitScene();
                offset = Find("SettingsSections").Scroll!.Value.OffsetY;
                AssertTrue(offset > 0);
            }, timeout.Token);
            await Capture("scrolled");
            await startup.InvokeAsync(() =>
            {
                var card = surface!.Scene!.Nodes.First(node => f.Shell.Tree.Name(node.Entity)
                    .StartsWith("ManagementScreenshot.", StringComparison.Ordinal) && node.IsClickable
                    && (node.ClipRect is null || node.ClipRect.Value.Contains(new(node.Rect.X + node.Rect.Width / 2, node.Rect.Y + node.Rect.Height / 2))));
                AssertTrue(f.Shell.Renderer.Activate(card.Entity));
            }, timeout.Token);
            await Eventually(() => Has("ScreenshotPreviewImage") && Has("ScreenshotPreview.Copy") && Has("ScreenshotPreview.Crop"));
            await Capture("enlarged");
            await startup.InvokeAsync(() => AssertFalse(Has("ManagementContentDetail")), timeout.Token);
            await Click("ScreenshotPreview.Crop", "ui.settings.screenshot-preview.action");
            await Eventually(() => Has("ScreenshotCropWidth") && Has("ScreenshotPreview.SaveCrop"));
            await Capture("crop");
            await Click("ScreenshotPreview.Close", "ui.settings.screenshot-preview.action");
            await Eventually(() => !Has("ScreenshotPreviewImage"));
            await startup.InvokeAsync(() => AssertClose(offset, Find("SettingsSections").Scroll!.Value.OffsetY), timeout.Token);
            await Capture("back");

            nint handle = 0;
            await startup.InvokeAsync(() => { handle = window!.TryGetPlatformHandle()!.Handle; f.Shell.Renderer.ReducedMotion = false; }, timeout.Token);
            await Capture("tray-before");
            await startup.InvokeAsync(actions.HideWindow, timeout.Token);
            await Eventually(() => !window!.IsVisible);
            await Capture("tray-hidden");
            await startup.InvokeAsync(actions.RestoreWindow, timeout.Token);
            await Eventually(() => window!.IsVisible && AvaloniaUiRuntimeDiagnostics.Capture().ActiveMotionTracks == 0);
            await startup.InvokeAsync(() => AssertEqual(handle, window!.TryGetPlatformHandle()!.Handle), timeout.Token);
            await Capture("tray-restored");
            outcome = 0;

            bool Has(string key) => surface?.Scene?.Nodes.Any(node => f.Shell.Tree.Name(node.Entity) == key) == true;
            XsrUiSceneNode Find(string key) => FindByKey(f.Shell, surface!.Scene!, key);
            async Task Click(string key, string intent)
            {
                await startup.InvokeAsync(() => { Emit(f.Intents, intent, Find(key).Entity); surface!.CommitScene(); }, timeout.Token);
            }
            async Task Eventually(Func<bool> ready)
            {
                while (true)
                {
                    bool done = false;
                    await startup.InvokeAsync(() => { surface?.CommitScene(); done = ready(); }, timeout.Token);
                    if (done) return;
                    await Task.Delay(10, timeout.Token);
                }
            }
            async Task Capture(string stage)
            {
                string? marker = null;
                await startup.InvokeAsync(() =>
                {
                    if (window!.IsVisible) { window.UpdateLayout(); surface!.CommitScene(); }
                    var origin = window.PointToScreen(default);
                    marker = string.Join('\t', stage, origin.X.ToString(CultureInfo.InvariantCulture), origin.Y.ToString(CultureInfo.InvariantCulture),
                        ((int)Math.Ceiling(window.ClientSize.Width * window.RenderScaling)).ToString(CultureInfo.InvariantCulture),
                        ((int)Math.Ceiling(window.ClientSize.Height * window.RenderScaling)).ToString(CultureInfo.InvariantCulture), theme) + "\n";
                }, timeout.Token);
                string path = Path.Combine(outputDirectory, "screenshot-preview.marker.tsv");
                await File.WriteAllTextAsync(path + ".tmp", marker!, timeout.Token);
                File.Move(path + ".tmp", path, overwrite: true);
                string acknowledgement = Path.Combine(outputDirectory, stage + ".capture-ok");
                while (!File.Exists(acknowledgement)) await Task.Delay(20, timeout.Token);
                await Task.Delay(500, timeout.Token); // Capture hold, not a functional timing assertion.
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { Console.Error.WriteLine("FAIL: native screenshot gallery and tray preview\n" + error); }
        finally
        {
            if (!startup.Completion.IsCompleted)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (fixture is not null) fixture.Shell.Renderer.ReducedMotion = true;
                    window?.Close(); settings?.Dispose(); appearance?.Dispose(); fixture?.Dispose();
                });
            else { settings?.Dispose(); appearance?.Dispose(); fixture?.Dispose(); }
            if (lifetime is not null) AssertEqual(0, await lifetime.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        if (outcome == 0) Console.WriteLine("PASS: actual native screenshot waterfall, enlarged actions, crop toggle, preserved gallery scroll and animated same-window tray hide/restore.");
        return outcome;
    }

    private static PngImage CreateScreenshotPreviewFixture(int width, int height, int index)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        using var sky = new SKPaint { Color = new SKColor((byte)(40 + index * 5 % 90), (byte)(120 + index * 3 % 70), 192) };
        using var grass = new SKPaint { Color = new SKColor(58, (byte)(100 + index * 4 % 60), 72) };
        using var stone = new SKPaint { Color = new SKColor(67, 83, 96) };
        using var cloud = new SKPaint { Color = new SKColor(222, 238, 245) };
        canvas.Clear(sky.Color);
        canvas.DrawRect(0, height * .65f, width, height * .35f, grass);
        for (int block = 0; block < 6; block++)
        {
            float top = height * (.37f + .055f * ((block + index) % 4));
            canvas.DrawRect(width * block / 6f, top, width / 6f + 1, height * .65f - top, stone);
        }
        canvas.DrawRect(width * .14f, height * .18f, width * .3f, height * .05f, cloud);
        canvas.DrawRect(width * .7f, height * .1f, width * .2f, height * .07f, cloud);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return PngImage.TryCreatePreview(encoded.AsSpan()) ?? throw new InvalidDataException("Invalid native preview fixture.");
    }
}
