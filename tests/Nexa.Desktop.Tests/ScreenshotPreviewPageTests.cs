using System.Buffers.Binary;
using Nexa.Core.Media;
using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static PngImage GalleryPng(int width, int height)
    {
        byte[] bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), 13); "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return PngImage.TryCreatePreview(bytes)!;
    }

    private static void ScreenshotGalleryRealizesVisibleWaterfallAndKeepsModalScroll()
    {
        var entries = Enumerable.Range(0, 20_000).Select(index => new InstanceContentEntry($"{index:D5}.png", false, 33)
        { ModifiedUtcTicks = 100 + index, ImageWidth = index % 2 == 0 ? 400 : 1600, ImageHeight = 900 }).ToArray();
        using var page = new ScreenshotPageFixture(entries);
        var tree = page.Fixture.Shell.Tree;
        var list = page.Find("ManagementContentList").Entity;
        var sections = tree.Parent(list);
        var scrolling = tree.GetComponent<XsrUiScroll>(sections)!;
        void AssertBounded()
        {
            var cards = page.Scene.Nodes.Where(node => tree.Name(node.Entity).StartsWith("ManagementScreenshot.", StringComparison.Ordinal)).ToArray();
            AssertTrue(cards.Length is > 0 and <= 1024);
            AssertFalse(page.Scene.Nodes.Any(node => tree.Name(node.Entity) == "ManagementContentDetail"));
        }
        AssertBounded();
        var initialCards = page.Scene.Nodes.Where(node => tree.Name(node.Entity).StartsWith("ManagementScreenshot.", StringComparison.Ordinal)).ToArray();
        AssertTrue(initialCards.Max(card => card.Rect.Height) > initialCards.Min(card => card.Rect.Height) * 2);
        page.Render(520, 650);
        AssertEqual(1, tree.Children(page.Find("ScreenshotWaterfall").Entity).Count);
        page.Render(1100, 650);
        AssertEqual(4, tree.Children(page.Find("ScreenshotWaterfall").Entity).Count);
        scrolling.OffsetY = 500_000; tree.MarkDirty(sections, XsrUiDirtyKinds.Layout); page.Render(); page.Render(); AssertBounded();
        double before = scrolling.OffsetY;
        var card = page.Scene.Nodes.First(node => tree.Name(node.Entity).StartsWith("ManagementScreenshot.", StringComparison.Ordinal)
            && node.Rect.Y + node.Rect.Height > 200 && node.Rect.Y < 550);
        page.Fixture.Shell.Renderer.Focus(card.Entity, showIndicator: true);
        page.Emit("ui.settings.management.action", card.Entity);
        AssertEqual(before, scrolling.OffsetY);
        AssertTrue(page.Scene.Nodes.Any(node => tree.Name(node.Entity) == "ScreenshotPreview"));
        AssertFalse(page.Scene.Nodes.Any(node => tree.Name(node.Entity) == "ManagementContentDetail"));
        AssertTrue(page.Scene.Nodes.Where(node => tree.Name(node.Entity).StartsWith("ManagementScreenshot.", StringComparison.Ordinal)).All(node => !node.IsAccessible));
        AssertTrue(page.Fixture.Shell.Renderer.HandleKey(XsrUiKey.Escape)); page.Render();
        AssertEqual(before, scrolling.OffsetY); AssertEqual(card.Entity, page.Fixture.Shell.Renderer.Focused);
        AssertTrue(tree.GetComponent<XsrUiInput>(card.Entity)!.IsFocusVisible);
        scrolling.OffsetY = 100_000_000; tree.MarkDirty(sections, XsrUiDirtyKinds.Layout); page.Render(); page.Render(); AssertBounded();
        AssertTrue(page.Scene.Nodes.Any(node => tree.Name(node.Entity) == "ManagementScreenshot.19999.png"));
        scrolling.OffsetY = 0; tree.MarkDirty(sections, XsrUiDirtyKinds.Layout); page.Render();
        var search = page.Find("ManagementContentSearch").Entity;
        page.Fixture.Shell.Renderer.SetTextInputValue(search, "19999"); page.Render();
        AssertEqual(1, page.Scene.Nodes.Count(node => tree.Name(node.Entity).StartsWith("ManagementScreenshot.", StringComparison.Ordinal)));
    }

    private static void ScreenshotPreviewRoutesIdentityRetiresReadsAndPreservesCropDrafts()
    {
        var a = GalleryPng(800, 450); var b = GalleryPng(400, 900);
        var entries = new[] { new InstanceContentEntry("a.png", false, 33) { Icon = a, ModifiedUtcTicks = 10 }, new InstanceContentEntry("b.png", false, 33) { Icon = b, ModifiedUtcTicks = 20 } };
        var pendingA = new TaskCompletionSource<XsrResult<InstanceScreenshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new List<InstanceScreenshotQuery>(); var tokens = new List<CancellationToken>();
        InstanceScreenshotCropCommand? crop = null; InstanceContentRemoveCommand? removal = null;
        var commands = new XsrCommandRouterBuilder();
        commands.Register<InstanceScreenshotCropCommand>(InstanceScreenshotContract.Crop, (command, _) => { crop = command; return ValueTask.FromResult(XsrResult.Success()); });
        commands.Register<InstanceContentRemoveCommand>(InstanceManagementContract.RemoveContent, (command, _) => { removal = command; return ValueTask.FromResult(XsrResult.Success()); });
        using var page = new ScreenshotPageFixture(entries, (query, token) =>
        {
            reads.Add(query); tokens.Add(token);
            return query.Name == "a.png" ? new(pendingA.Task) : ValueTask.FromResult(XsrResult.Success(new InstanceScreenshot("b-path.png", b)));
        }, commands.Build(new NoopDispatchObserver()));
        int copies = 0; (string Path, long Size, long Ticks)? shared = null; string? folder = null;
        page.Settings.CopyScreenshotAsync = bytes => { AssertEqual(b.Key, PngImage.TryCreatePreview(bytes.Span)!.Key); copies++; return Task.CompletedTask; };
        page.Settings.ShareScreenshotAsync = (path, size, ticks) => { shared = (path, size, ticks); return Task.CompletedTask; };
        page.Settings.OpenManagementDirectory = path => folder = path;
        page.Emit("ui.settings.management.action", "ManagementScreenshot.a.png");
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Copy");
        AssertEqual(2, reads.Count); AssertTrue(reads.All(query => query.Name == "a.png" && query.ExpectedSize == 33 && query.ExpectedModifiedUtcTicks == 10));
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Close"); AssertTrue(tokens.All(token => token.IsCancellationRequested));
        page.Emit("ui.settings.management.action", "ManagementScreenshot.b.png");
        pendingA.SetResult(XsrResult.Success(new InstanceScreenshot("stale-a-path.png", a))); page.Render();
        AssertEqual(0, copies); AssertEqual("b.png", page.Find("ScreenshotPreviewTitle").Text);
        AssertClose(16, page.Find("ScreenshotPreviewTitle").Rect.X - page.Find("ScreenshotPreview").Rect.X);
        AssertClose(16, page.Find("ScreenshotPreviewImage").Rect.X - page.Find("ScreenshotPreview").Rect.X);
        AssertClose(16, page.Find("ScreenshotPreview.Copy").Rect.X - page.Find("ScreenshotPreview").Rect.X);
        AssertEqual(b.Key, page.Find("ScreenshotPreviewImage").RasterImage!.Image.Key);
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Copy"); page.Pump(() => copies == 1);
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Share"); page.Pump(() => shared is not null);
        AssertEqual(("b-path.png", 33L, 20L), shared!.Value);
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Folder"); AssertEqual("instance-A/screenshots", folder);
        AssertFalse(page.Scene.Nodes.Any(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ScreenshotCropWidth"));
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Crop");
        var width = page.Find("ScreenshotCropWidth").Entity;
        AssertClose(16, page.Find("ScreenshotCropFields").Rect.X - page.Find("ScreenshotPreview").Rect.X);
        page.Fixture.Shell.Renderer.SetTextInputValue(width, "120"); page.Fixture.Shell.Renderer.Focus(width, true);
        page.Render(900, 700);
        width = page.Find("ScreenshotCropWidth").Entity;
        AssertEqual("120", page.Fixture.Shell.Tree.GetComponent<XsrUiTextInput>(width)!.ReadDraft());
        AssertEqual(width, page.Fixture.Shell.Renderer.Focused);
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.SaveCrop"); page.Pump(() => crop is not null);
        AssertEqual("b.png", crop!.File.Name); AssertEqual(33L, crop.File.ExpectedSize); AssertEqual(20L, crop.File.ExpectedModifiedUtcTicks); AssertEqual(120, crop.Width);
        page.Pump(() => page.Scene.Nodes.Any(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ManagementScreenshot.b.png"));
        page.Emit("ui.settings.management.action", "ManagementScreenshot.b.png");
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Remove");
        var confirmation = page.Fixture.Feedback.Snapshot().Dialog!; AssertEqual("content.remove", confirmation.Key); AssertTrue(removal is null);
        page.Instance = "instance-B"; page.Render();
        AssertFalse(page.Fixture.Feedback.ResolveDialog(confirmation.Id, true)); AssertTrue(removal is null);
        AssertFalse(page.Scene.Nodes.Any(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ScreenshotPreview"));
        AssertEqual("overview", page.Settings.SelectedSection);
    }

    private static void ScreenshotVisibleReadsAreBoundedCachedAndRetired()
    {
        var png = GalleryPng(800, 450);
        var entries = Enumerable.Range(0, 100).Select(index => new InstanceContentEntry($"{index:D3}.png", false, 33)
        { ModifiedUtcTicks = 100 + index, ImageWidth = 800, ImageHeight = 450 }).ToArray();
        var pending = new Dictionary<string, TaskCompletionSource<XsrResult<InstanceScreenshot>>>(); var tokens = new Dictionary<string, CancellationToken>();
        using var page = new ScreenshotPageFixture(entries, (query, token) =>
        {
            AssertFalse(pending.ContainsKey(query.Name));
            var source = new TaskCompletionSource<XsrResult<InstanceScreenshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[query.Name] = source; tokens[query.Name] = token; return new(source.Task);
        });
        page.Render(); AssertEqual(2, pending.Count);
        string first = pending.Keys.First(); pending[first].SetResult(XsrResult.Success(new InstanceScreenshot(first, png)));
        page.Pump(() => pending.Count == 3);
        var icon = page.Scene.Nodes.First(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ManagementContentIcon" && node.RasterImage is not null);
        AssertEqual(png.Key, icon.RasterImage!.Image.Key);
        var section = page.Fixture.Shell.Tree.Parent(page.Find("ManagementContentList").Entity);
        var scroll = page.Fixture.Shell.Tree.GetComponent<XsrUiScroll>(section)!;
        scroll.OffsetY = 2000; page.Fixture.Shell.Tree.MarkDirty(section, XsrUiDirtyKinds.Layout); page.Render();
        scroll.OffsetY = 0; page.Fixture.Shell.Tree.MarkDirty(section, XsrUiDirtyKinds.Layout); page.Render();
        AssertEqual(3, pending.Count);
        page.Emit("ui.settings.section", "SettingsNav.overview");
        AssertTrue(tokens.Values.All(token => token.IsCancellationRequested));
        foreach (var source in pending.Values) source.TrySetResult(XsrResult.Success(new InstanceScreenshot("stale", png)));
        page.Render(); AssertEqual("overview", page.Settings.SelectedSection);
        AssertFalse(page.Scene.Nodes.Any(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ScreenshotWaterfall"));
    }

    private static void ScreenshotPreviewWaitsForOriginalBeforeCropAndRetiresRefresh()
    {
        var image = GalleryPng(400, 900);
        var entry = new InstanceContentEntry("waiting.png", false, 33) { ImageWidth = 400, ImageHeight = 900, ModifiedUtcTicks = 20 };
        var completion = new TaskCompletionSource<XsrResult<InstanceScreenshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new XsrCommandRouterBuilder();
        commands.Register<InstanceScreenshotCropCommand>(InstanceScreenshotContract.Crop, (_, _) => ValueTask.FromResult(XsrResult.Success()));
        using var page = new ScreenshotPageFixture([entry], (_, _) => new(completion.Task), commands.Build(new NoopDispatchObserver()));
        page.Emit("ui.settings.management.action", "ManagementScreenshot.waiting.png");
        AssertFalse(page.Find("ScreenshotPreview.Crop").IsEnabled);
        AssertTrue(page.Scene.Nodes.Any(node => node.Text?.StartsWith("400 × 900", StringComparison.Ordinal) == true));
        completion.SetResult(XsrResult.Success(new InstanceScreenshot("waiting.png", image)));
        page.Pump(() => page.Find("ScreenshotPreview.Crop").IsEnabled);
        page.Emit("ui.settings.screenshot-preview.action", "ScreenshotPreview.Crop");
        AssertEqual("400", page.Fixture.Shell.Tree.GetComponent<XsrUiTextInput>(page.Find("ScreenshotCropWidth").Entity)!.ReadDraft());
        var retired = page.Find("ScreenshotPreview.Close").Entity;
        page.Emit("ui.settings.management.action", "Management.刷新");
        page.Pump(() => !page.Scene.Nodes.Any(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ScreenshotPreview"));
        page.Emit("ui.settings.screenshot-preview.action", retired);
        AssertFalse(page.Scene.Nodes.Any(node => page.Fixture.Shell.Tree.Name(node.Entity) == "ScreenshotPreview"));
    }

    private static void ScreenshotVisibleCarriersSurviveLruEvictionWithoutRepeatedReads()
    {
        var image = GalleryPng(4096, 1);
        var entries = Enumerable.Range(0, 100).Select(index => new InstanceContentEntry($"{index:D3}.png", false, 33)
        { ImageWidth = 4096, ImageHeight = 1, ModifiedUtcTicks = index + 100 }).ToArray();
        var counts = new Dictionary<string, int>();
        using var page = new ScreenshotPageFixture(entries, (query, _) =>
        {
            counts[query.Name] = counts.GetValueOrDefault(query.Name) + 1;
            return ValueTask.FromResult(XsrResult.Success(new InstanceScreenshot(query.Name, image)));
        });
        page.Render(1100, 1500);
        int Realized(bool loadedOnly)
        {
            int count = 0;
            page.Fixture.Shell.Tree.Walk(page.Settings.Page, entity =>
            {
                string name = page.Fixture.Shell.Tree.Name(entity);
                if (loadedOnly ? name == "ManagementContentIcon" && page.Fixture.Shell.Tree.GetComponent<XsrUiImage>(entity)?.Raster is not null
                    : name.StartsWith("ManagementScreenshot.", StringComparison.Ordinal)) count++;
                return true;
            });
            return count;
        }
        // Include overscan cards retained outside the scene's clip, then require stable IO.
        page.Pump(() => Realized(loadedOnly: true) == Realized(loadedOnly: false));
        AssertTrue(counts.Count > 32); AssertTrue(counts.Values.All(count => count == 1));
        int reads = counts.Values.Sum();
        for (int frame = 0; frame < 8; frame++) page.Render();
        AssertEqual(reads, counts.Values.Sum());
    }

    private static void ScreenshotThumbnailQuotaIncludesRealizedImagesAndReadReservations()
    {
        const int imageBytes = 16 * 1024 * 1024;
        const long budget = 64L * 1024 * 1024;
        var header = GalleryPng(800, 450);
        var entries = Enumerable.Range(0, 40).Select(index => new InstanceContentEntry($"large-{index:D2}.png", false, imageBytes)
        { ImageWidth = 800, ImageHeight = 450, ModifiedUtcTicks = index + 100 }).ToArray();
        var pending = new List<(InstanceScreenshotQuery File, TaskCompletionSource<XsrResult<InstanceScreenshot>> Completion)>();
        using var page = new ScreenshotPageFixture(entries, (query, _) =>
        {
            var completion = new TaskCompletionSource<XsrResult<InstanceScreenshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((query, completion)); return new(completion.Task);
        });
        void AssertQuota() => AssertTrue(page.Settings.ScreenshotThumbnailOwnedBytes + page.Settings.ScreenshotThumbnailReservedBytes <= budget);
        AssertEqual(2, pending.Count); AssertEqual(2L * imageBytes, page.Settings.ScreenshotThumbnailReservedBytes); AssertQuota();
        for (int index = 0; index < 4; index++)
        {
            page.Pump(() => pending.Count > index);
            // Encoded-carrier accounting test; native PNG decode is covered elsewhere.
            byte[] bytes = new byte[imageBytes]; header.Bytes.Span.CopyTo(bytes); bytes[^1] = (byte)index;
            var image = PngImage.TryCreatePreview(bytes)!;
            pending[index].Completion.SetResult(XsrResult.Success(new InstanceScreenshot(pending[index].File.Name, image)));
            int completed = index + 1;
            page.Pump(() => page.Settings.ScreenshotThumbnailOwnedBytes == completed * (long)imageBytes);
            AssertQuota();
        }
        AssertEqual(4, pending.Count); AssertEqual(budget, page.Settings.ScreenshotThumbnailOwnedBytes);
        AssertEqual(0L, page.Settings.ScreenshotThumbnailReservedBytes);
        for (int frame = 0; frame < 8; frame++) { page.Render(); AssertQuota(); }
        AssertEqual(4, pending.Count);
        var sections = page.Fixture.Shell.Tree.Parent(page.Find("ManagementContentList").Entity);
        page.Fixture.Shell.Tree.GetComponent<XsrUiScroll>(sections)!.OffsetY = 100_000;
        page.Fixture.Shell.Tree.MarkDirty(sections, XsrUiDirtyKinds.Layout); page.Render();
        page.Pump(() => pending.Count > 4);
        AssertQuota();
        AssertTrue(page.Settings.ScreenshotThumbnailReservedBytes > 0);
        AssertTrue(pending.Skip(4).All(read => pending.Take(4).All(old => old.File.Name != read.File.Name)));
    }

    private sealed class ScreenshotPageFixture : IDisposable
    {
        internal LaunchPageFixture Fixture { get; } = new(new ImmediateInstanceSource([]));
        internal SettingsPageController Settings { get; }
        internal XsrUiScene Scene { get; private set; }
        internal string Instance { get; set; } = "instance-A";
        private XsrUiSize _size = new(1100, 650);
        internal ScreenshotPageFixture(IReadOnlyList<InstanceContentEntry> entries,
            Func<InstanceScreenshotQuery, CancellationToken, ValueTask<XsrResult<InstanceScreenshot>>>? read = null, XsrCommandRouter? commands = null)
        {
            AssertTrue(Fixture.Foundation.Host.SettingsPolicy.Set(new("general.language", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "zh-Hans"))).IsSuccess);
            var queries = new XsrQueryRouterBuilder();
            Fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.CatalogQuery, out var catalog); Fixture.Foundation.Queries.TryResolve(SettingsPolicyContract.EffectiveQuery, out var effective);
            queries.Register<SettingsCatalogQuery, SettingsCatalogSnapshot>(SettingsPolicyContract.CatalogQuery,
                (query, token) => Fixture.Foundation.Queries.QueryAsync<SettingsCatalogQuery, SettingsCatalogSnapshot>(catalog, query, cancellationToken: token));
            queries.Register<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(SettingsPolicyContract.EffectiveQuery,
                (query, token) => Fixture.Foundation.Queries.QueryAsync<SettingsEffectiveQuery, SettingsEffectiveSnapshot>(effective, query, cancellationToken: token));
            queries.Register<InstanceManagementQuery, InstanceManagementSnapshot>(InstanceManagementContract.Query, (query, _) => ValueTask.FromResult(XsrResult.Success(
                new InstanceManagementSnapshot(query.InstanceDirectory, query.InstanceDirectory, "1.21.1", [],
                    [new("overview", "总览"), new("game", "游戏设置"), new("screenshots", "截图", query.InstanceDirectory + "/screenshots")], true, "")
                { Contents = [new("screenshots", entries, true, null)] })));
            if (read is not null) queries.Register<InstanceScreenshotQuery, InstanceScreenshot>(InstanceScreenshotContract.Read, (query, token) => read(query, token));
            Settings = new(Fixture.Shell, Fixture.Intents, queries.Build(new NoopDispatchObserver()), commands ?? Fixture.Foundation.Commands,
                Fixture.Store, Fixture.Feedback, () => Instance);
            Fixture.Shell.Renderer.ReducedMotion = true; Fixture.Shell.Stage.Navigation.Replace(Settings.Page);
            Scene = Fixture.Shell.Render(_size);
            Pump(() => Scene.Nodes.Any(node => Fixture.Shell.Tree.Name(node.Entity) == "SettingsNav.screenshots"));
            Emit("ui.settings.section", "SettingsNav.screenshots");
            Pump(() => Scene.Nodes.Any(node => Fixture.Shell.Tree.Name(node.Entity) == "ScreenshotWaterfall"));
        }
        internal XsrUiSceneNode Find(string name) => FindByKey(Fixture.Shell, Scene, name);
        internal void Emit(string command, string name) => Emit(command, Find(name).Entity);
        internal void Emit(string command, XsrUiEntityId entity) { Program.Emit(Fixture.Intents, command, entity); Render(); }
        internal void Render(double? width = null, double? height = null) { if (width is not null || height is not null) _size = new(width ?? _size.Width, height ?? _size.Height); Scene = Fixture.Shell.Render(_size); }
        internal void Pump(Func<bool> condition) => AssertTrue(SpinWait.SpinUntil(() => { Render(); return condition(); }, TimeSpan.FromSeconds(5)));
        public void Dispose() { Settings.Dispose(); Fixture.Dispose(); }
    }
}
