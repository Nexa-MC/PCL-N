using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexa.Desktop.Ui;
using Nexa.Services.Resources;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static readonly string[] PreviewResourceNames = ["流畅渲染", "内存优化", "光照修复", "视距优化", "加载加速", "界面增强", "资源索引", "方块工具", "世界辅助", "性能观测"];
    private static readonly string[] PreviewResourceGames = ["1.21.1"], PreviewResourceLoaders = ["Fabric"];
    private static int RunNativeResourcePreview(string theme, string outputDirectory) =>
        RunNativeResourcePreviewAsync(theme, Path.GetFullPath(outputDirectory)).GetAwaiter().GetResult();

    private static async Task<int> RunNativeResourcePreviewAsync(string theme, string outputDirectory)
    {
        if (theme is not ("light" or "dark")) throw new ArgumentException("Preview theme must be light or dark.", nameof(theme));
        Directory.CreateDirectory(outputDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        XsrUiThemeMode mode = theme == "dark" ? XsrUiThemeMode.Dark : XsrUiThemeMode.Light;
        await using var startup = await AvaloniaUiStartupSession.StartWithAppearanceAsync([], disableHardwareAcceleration: true,
            new AvaloniaUiStartupAppearance(mode, ReducedMotion: true), cancellationToken: timeout.Token);
        NativeResourcePreviewHarness? harness = null;
        DesktopAppearanceSession? appearance = null;
        AvaloniaUiShellWindow? window = null;
        AvaloniaUiSceneSurface? surface = null;
        Task<int>? nativeLifetime = null;
        Pointer? pointer = null;
        var actions = new AvaloniaUiPlatformActions();
        actions.SetDesktopPolicy(new(TrayEnabled: false));
        int outcome = 1;
        try
        {
            await startup.InvokeAsync(() =>
            {
                harness = new();
                var policy = harness.Fixture.Foundation.Host.SettingsPolicy;
                AssertTrue(policy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
                AssertTrue(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, theme == "dark" ? "1" : "0"))).IsSuccess);
                appearance = new(harness.Fixture.Shell, harness.Fixture.Store, actions);
                pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
                harness.Complete(0, hasMore: true);
            }, timeout.Token);
            var h = harness!;
            await startup.PrepareShellAsync(h.Fixture.Shell, actions, timeout.Token);
            await startup.WarmUpShellAsync(h.Fixture.Shell, timeout.Token);
            nativeLifetime = Task.Run(() => AvaloniaUiShellHost.Run(h.Fixture.Shell, platformActions: actions));
            await Eventually(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow is not AvaloniaUiShellWindow nativeWindow || !nativeWindow.IsVisible) return false;
                window = nativeWindow;
                surface = nativeWindow.GetVisualDescendants().OfType<AvaloniaUiSceneSurface>().Single();
                nativeWindow.UpdateLayout(); surface.CommitScene();
                return surface.Scene is { Count: > 0 } && h.Rows.Length > 0;
            });
            var nativeSurface = surface!;
            var nativeWindow = window!;
            XsrUiEntityId list = h.Page.Find("ResourceList");
            await startup.InvokeAsync(() => AssertEqual(theme == "dark", h.Fixture.Shell.Renderer.ColorScheme.IsDark), timeout.Token);
            await CaptureStage("list-initial");
            await ScrollUntilPage(1);
            await CaptureStage("append-loading");
            double firstOffset = 0;
            XsrUiEntityId firstAnchor = default;
            await startup.InvokeAsync(() =>
            {
                firstOffset = Snapshot().OffsetY;
                firstAnchor = h.Rows.Last();
                AssertEqual(2, h.Reads.Count);
                h.Complete(1, hasMore: true);
            }, timeout.Token);
            await Eventually(() => h.Fixture.Shell.Tree.GetComponent<XsrUiInput>(h.Page.Find("ResourceNext"))!.Enabled);
            await startup.InvokeAsync(() =>
            {
                AssertTrue(h.Rows.Contains(firstAnchor)); AssertClose(firstOffset, Snapshot().OffsetY);
                AssertEqual(2, h.Reads.Count);
            }, timeout.Token);
            await CaptureStage("appended");
            await ScrollUntilPage(2);
            await CaptureStage("next-loading");
            double secondOffset = 0;
            XsrUiEntityId secondAnchor = default;
            await startup.InvokeAsync(() =>
            {
                secondOffset = Snapshot().OffsetY; secondAnchor = h.Rows.Last();
                h.Reads[2].Reply.SetResult(XsrResult.Failure<ResourceSearchResult>(new(XsrErrorKind.Rejected,
                    XsrSemanticId.Parse("fixture.preview.resource.offline"), "Controlled unavailable provider")));
            }, timeout.Token);
            await Eventually(() => h.Fixture.Shell.Tree.GetComponent<XsrUiInput>(h.Page.Find("ResourceNext"))!.Enabled);
            await CaptureStage("failed");
            await startup.InvokeAsync(() =>
            {
                AssertEqual(3, h.Reads.Count); AssertTrue(h.Rows.Contains(secondAnchor)); AssertClose(secondOffset, Snapshot().OffsetY);
                Click(h.Page.Find("ResourceNext"));
            }, timeout.Token);
            await Eventually(() => h.Reads.Count == 4);
            await CaptureStage("retry-loading");
            await startup.InvokeAsync(() =>
            {
                AssertEqual(h.Reads[2].Query, h.Reads[3].Query);
                h.Complete(3, hasMore: false);
            }, timeout.Token);
            await Eventually(() => !h.Fixture.Shell.Tree.GetComponent<XsrUiInput>(h.Page.Find("ResourceNext"))!.Enabled
                && Snapshot().ContentHeight > 4000);
            await CaptureStage("retry-success");
            await startup.InvokeAsync(() =>
            {
                AssertTrue(h.Rows.Contains(secondAnchor)); AssertClose(secondOffset, Snapshot().OffsetY);
                AssertTrue(h.Reads.Select(read => read.Query.Page).SequenceEqual(NativeResourceExpectedPages));
                var scene = nativeSurface.Scene!;
                var details = scene.Nodes.First(node => h.Fixture.Shell.Tree.Name(node.Entity).StartsWith("ResourceDetails.", StringComparison.Ordinal)
                    && node.Rect.Y >= scene.Nodes.Single(item => item.Entity == list).Rect.Y
                    && node.Rect.Y + node.Rect.Height <= scene.Nodes.Single(item => item.Entity == list).Rect.Y + Snapshot().ViewportHeight);
                Click(details.Entity);
            }, timeout.Token);
            await Eventually(() => h.Fixture.Shell.Stage.Navigation.Current == h.Page.DetailPage
                && nativeSurface.Scene!.Nodes.Any(node => node.Text?.Contains("MIT · 受控预览资料", StringComparison.Ordinal) == true));
            await CaptureStage("detail");
            await startup.InvokeAsync(() =>
            {
                var back = nativeSurface.Scene!.Nodes.Single(node => h.Fixture.Shell.Tree.Name(node.Entity) == "TitleBack");
                Click(back.Entity);
            }, timeout.Token);
            await Eventually(() => h.Fixture.Shell.Stage.Navigation.Current == h.Page.Page);
            await startup.InvokeAsync(() =>
            {
                AssertTrue(h.Rows.Contains(secondAnchor)); AssertClose(secondOffset, Snapshot().OffsetY);
                AssertEqual(4, h.Reads.Count);
            }, timeout.Token);
            await CaptureStage("back");
            outcome = 0;

            XsrUiScrollSnapshot Snapshot()
            {
                AssertTrue(h.Fixture.Shell.Renderer.TryGetScrollSnapshot(list, out var snapshot));
                return snapshot;
            }

            async Task ScrollUntilPage(int page)
            {
                // Intermediate committed frames show actual scrolling rather than a single jump.
                while (true)
                {
                    bool admitted = false;
                    await startup.InvokeAsync(() =>
                    {
                        var bounds = nativeSurface.Scene!.Nodes.Single(node => node.Entity == list).Rect;
                        var point = nativeSurface.TranslatePoint(new Point(bounds.X + 24, bounds.Y + 24), nativeWindow)!.Value;
                        nativeSurface.RaiseEvent(new PointerWheelEventArgs(nativeSurface, pointer!, nativeWindow, point, 0,
                            default, KeyModifiers.None, new Vector(0, -3)));
                        nativeSurface.CommitScene();
                        admitted = h.Reads.Any(read => read.Query.Page == page);
                    }, timeout.Token);
                    if (admitted) return;
                    await Task.Delay(55, timeout.Token);
                }
            }

            void Click(XsrUiEntityId entity)
            {
                var bounds = nativeSurface.Scene!.Nodes.Single(node => node.Entity == entity).Rect;
                var point = nativeSurface.TranslatePoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), nativeWindow)!.Value;
                var press = new PointerPressedEventArgs(nativeSurface, pointer!, nativeWindow, point, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
                nativeSurface.RaiseEvent(press); AssertTrue(press.Handled);
                var release = new PointerReleasedEventArgs(nativeSurface, pointer!, nativeWindow, point, 1,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left);
                nativeSurface.RaiseEvent(release); AssertTrue(release.Handled);
            }

            async Task CaptureStage(string stage)
            {
                string? marker = null;
                await startup.InvokeAsync(() =>
                {
                    nativeSurface.CommitScene(); nativeWindow.UpdateLayout();
                    PixelPoint origin = nativeWindow.PointToScreen(default);
                    int width = (int)Math.Ceiling(nativeWindow.ClientSize.Width * nativeWindow.RenderScaling);
                    int height = (int)Math.Ceiling(nativeWindow.ClientSize.Height * nativeWindow.RenderScaling);
                    marker = string.Join('\t', stage, origin.X.ToString(CultureInfo.InvariantCulture), origin.Y.ToString(CultureInfo.InvariantCulture),
                        width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture), theme) + "\n";
                }, timeout.Token);
                string path = Path.Combine(outputDirectory, "resource-preview.marker.tsv");
                string temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, marker!, timeout.Token); File.Move(temporary, path, overwrite: true);
                Console.WriteLine("RESOURCE_PREVIEW_STAGE\t" + marker!.TrimEnd());
                string acknowledgement = Path.Combine(outputDirectory, stage + ".capture-ok");
                while (!File.Exists(acknowledgement)) await Task.Delay(20, timeout.Token);
                // This is preview choreography after capture, not a test timing assertion.
                await Task.Delay(800, timeout.Token);
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
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Console.Error.WriteLine("FAIL: native resource preview\n" + error); outcome = 1;
        }
        finally
        {
            if (!startup.Completion.IsCompleted)
                await Dispatcher.UIThread.InvokeAsync(() => { window?.Close(); appearance?.Dispose(); harness?.Dispose(); pointer?.Dispose(); });
            else { appearance?.Dispose(); harness?.Dispose(); pointer?.Dispose(); }
            if (nativeLifetime is not null) AssertEqual(0, await nativeLifetime.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Console.WriteLine(outcome == 0 ? "PASS: native resource preview with controlled fixture data and screen-capture barriers." : "FAIL: resource preview did not complete.");
        return outcome;
    }

    private sealed class NativeResourcePreviewHarness : IDisposable
    {
        internal LaunchPageFixture Fixture { get; } = new(new ImmediateInstanceSource([]));
        internal List<ContinuousResourceRead> Reads { get; } = [];
        internal ResourcesPageController Page { get; }
        private readonly Dictionary<string, ResourceProject> _projects = new(StringComparer.Ordinal);
        internal XsrUiEntityId[] Rows => Fixture.Shell.Tree.Children(Page.Find("ResourceList"))
            .Where(entity => Fixture.Shell.Tree.Name(entity).StartsWith("ResourceProject.", StringComparison.Ordinal)).ToArray();

        internal NativeResourcePreviewHarness()
        {
            var queries = new XsrQueryRouterBuilder();
            queries.Register<ResourceSearchQuery, ResourceSearchResult>(ResourceCatalogContract.Search, async (query, token) =>
            {
                var read = new ContinuousResourceRead(query, token); Reads.Add(read); return await read.Reply.Task;
            });
            queries.Register<ResourceDetailQuery, ResourceDetail>(ResourceCatalogContract.Detail, (query, _) =>
            {
                var project = _projects[query.ProjectId];
                ResourceVersion[] versions = [new("preview-stable", "稳定预览版本 1.3.0", "1.3.0", "正式版", PreviewResourceGames, PreviewResourceLoaders, "2026-10-09", project.Website),
                    new("preview-beta", "测试预览版本 1.4.0", "1.4.0", "Beta", PreviewResourceGames, PreviewResourceLoaders, "2026-10-08", project.Website)];
                return ValueTask.FromResult(XsrResult.Success(new ResourceDetail(project, "MIT · 受控预览资料", versions)
                { Notice = "此处使用受控测试目录资料，不代表在线资源站响应。" }));
            });
            Page = new(Fixture.Shell, Fixture.Intents, queries.Build(new NoopDispatchObserver()), Fixture.Store, _ => { });
            Fixture.Controller.ResourcesPage = Page.Page;
            Fixture.Shell.Renderer.ReducedMotion = true;
            Emit(Fixture.Intents, "ui.navigation.community");
            Fixture.Shell.Render(new(1100, 700));
            AssertEqual(1, Reads.Count);
        }

        internal void Complete(int readIndex, bool hasMore)
        {
            int page = Reads[readIndex].Query.Page;
            var projects = Enumerable.Range(page * ResourceCatalogContract.PageSize, ResourceCatalogContract.PageSize).Select(index =>
            {
                string id = "Preview" + index.ToString("D2", CultureInfo.InvariantCulture);
                var project = new ResourceProject(id, PreviewResourceNames[index % PreviewResourceNames.Length] + " · " + (index + 1).ToString("D2", CultureInfo.InvariantCulture),
                    "受控目录示例：展示资源检索、连续追加和失败重试。实际内容需从资源站确认。", "界面预览数据", 42000 + index * 1731, "https://example.invalid/resource-preview/" + id)
                { Sources = [new(ResourceProvider.Modrinth, id)], Kind = ResourceKind.Mod };
                _projects[id] = project; return project;
            }).ToArray();
            Reads[readIndex].Reply.SetResult(XsrResult.Success(new ResourceSearchResult(projects, 60, page) { HasMore = hasMore }));
        }

        public void Dispose() { Page.Dispose(); Fixture.Dispose(); }
    }
}
