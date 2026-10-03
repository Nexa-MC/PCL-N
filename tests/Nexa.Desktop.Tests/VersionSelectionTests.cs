using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void VersionSelectionBoundsLargeListsAndKeepsLogicalTransfers()
    {
        foreach (int total in new[] { 1000, 10000 })
        {
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource(
                [.. Enumerable.Range(0, total).Select(index => Instance($"fixture-{index:D5}"))]));
            fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
            var shell = fixture.Shell;
            XsrUiSize size = new(850, 500);
            shell.Renderer.ReducedMotion = true;
            shell.Renderer.Activate(FindByKey(shell, shell.Render(size), "InstanceListButton").Entity);
            XsrUiScene scene = shell.Render(size);
            XsrUiEntityId host = FindEntity(shell, "LibraryVersionRows");
            if (shell.Tree.Children(host).Count > 32)
                throw new InvalidOperationException($"Installed-version window materialized {shell.Tree.Children(host).Count} rows for {total} logical entries.");
            AssertTrue(shell.Tree.Count < 1200);
            AssertTrue(scene.Count < 800);
            var snapshot = fixture.Store.Read<MinecraftLibrarySnapshot>(fixture.Store.Resolve(MinecraftLibraryContract.StateKey)).Value;
            var logical = snapshot.Instances;
            XsrUiScroll scroll = shell.Tree.GetComponent<XsrUiScroll>(host)!;
            AssertClose(total * 80 - 8, FindByKey(shell, scene, "LibraryVersionRows").Scroll!.Value.ContentHeight);
            string Row(int index) => "LibraryRow:version:" + logical[index].Id;
            var first = FindByKey(shell, scene, Row(1)).Entity;
            Emit(fixture.Intents, "ui.versions.toggle-transfer", first);
            scene = shell.Render(size);
            var overlap = FindByKey(shell, scene, Row(2)).Entity;
            scroll.OffsetY = 80;
            shell.Tree.MarkDirty(host, XsrUiDirtyKinds.Layout);
            scene = shell.Render(size);
            AssertEqual(overlap, FindByKey(shell, scene, Row(2)).Entity);

            int middle = total / 2;
            scroll.OffsetY = middle * 80;
            shell.Tree.MarkDirty(host, XsrUiDirtyKinds.Layout);
            scene = shell.Render(size);
            AssertTrue(shell.Tree.Children(host).Count <= 32);
            Emit(fixture.Intents, "ui.versions.extend-transfer", FindByKey(shell, scene, Row(middle)).Entity);
            scene = shell.Render(size);
            var transfer = shell.Tree.GetComponent<XsrUiFileDrag>(FindByKey(shell, scene, Row(middle)).Entity)!;
            AssertTrue(transfer.Paths.SequenceEqual(logical.Skip(1).Take(middle).Select(item => item.DirectoryPath)));
            var sessions = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
            fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0,
                [new(Guid.NewGuid(), logical[2].Id, 123, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
                { InstanceDirectory = logical[2].DirectoryPath }], []));
            scene = shell.Render(size);
            AssertFalse(shell.Tree.GetComponent<XsrUiFileDrag>(FindByKey(shell, scene, Row(middle)).Entity)!
                .Effects.HasFlag(XsrUiFileDragEffects.Move));

            scroll.OffsetY = total * 80;
            shell.Tree.MarkDirty(host, XsrUiDirtyKinds.Layout);
            scene = shell.Render(size);
            AssertTrue(HasKey(shell, scene, Row(total - 1)));
            AssertTrue(shell.Tree.Children(host).Count <= 32);
            AssertClose(total * 80 - 8, FindByKey(shell, scene, "LibraryVersionRows").Scroll!.Value.ContentHeight);

            shell.Renderer.SetTextInputValue(FindByKey(shell, scene, "LibrarySearch").Entity, logical[middle].Id);
            scene = shell.Render(size);
            AssertEqual(logical[middle].DirectoryPath,
                shell.Tree.GetComponent<XsrUiFileDrag>(FindByKey(shell, scene, Row(middle)).Entity)!.Paths.Single());
            shell.Renderer.SetTextInputValue(FindByKey(shell, scene, "LibrarySearch").Entity, "");
            scroll.OffsetY = 0;
            shell.Tree.MarkDirty(host, XsrUiDirtyKinds.Layout);
            scene = shell.Render(size);
            shell.Renderer.Focus(FindByKey(shell, scene, Row(0)).Entity);
            scene = shell.Render(size);
            int furthest = 0;
            for (int tab = 0; tab < 100; tab++)
            {
                AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Tab));
                scene = shell.Render(size);
                XsrUiEntityId focused = shell.Renderer.Focused;
                while (focused.IsAssigned && shell.Tree.IsAlive(focused))
                {
                    string name = shell.Tree.Name(focused);
                    if (name.StartsWith("LibraryRow:version:", StringComparison.Ordinal))
                    {
                        string id = name["LibraryRow:version:".Length..];
                        for (int index = 0; index < logical.Count; index++)
                            if (logical[index].Id == id) { furthest = Math.Max(furthest, index); break; }
                        break;
                    }
                    focused = shell.Tree.Parent(focused);
                }
                AssertTrue(shell.Tree.Children(host).Count <= 32);
            }
            if (furthest < 20) throw new InvalidOperationException($"Keyboard focus only reached logical row {furthest}.");
            for (int tab = 0; tab < 100; tab++)
            {
                AssertTrue(shell.Renderer.FocusPrevious());
                scene = shell.Render(size);
                AssertTrue(shell.Tree.Children(host).Count <= 32);
            }
            AssertEqual(FindByKey(shell, scene, Row(0)).Entity, shell.Renderer.Focused);
            // Wheel scrolling preserves the focused row without forcing the viewport back.
            scroll.OffsetY = middle * 80;
            shell.Tree.MarkDirty(host, XsrUiDirtyKinds.Layout);
            scene = shell.Render(size);
            AssertClose(middle * 80, scroll.OffsetY);
            AssertTrue(shell.Tree.IsAlive(shell.Renderer.Focused));
            scene = shell.Render(new(850, 700));
            AssertTrue(HasKey(shell, scene, Row(middle)));
            AssertTrue(shell.Tree.Children(host).Count <= 40);
        }
    }

    private static void VersionTransferSelectionSupportsRanges()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("a"), Instance("b"), Instance("c"), Instance("d")]));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        var shell = fixture.Shell;
        var scene = shell.Render(new(850, 500));
        shell.Renderer.Activate(FindByKey(shell, scene, "InstanceListButton").Entity);
        scene = shell.Render(new(850, 500));
        string launchVersion = ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey);
        void Select(string id, string intent)
        {
            Emit(fixture.Intents, intent, FindByKey(shell, scene, "LibraryRow:version:" + id).Entity);
            scene = shell.Render(new(850, 500));
            AssertEqual(fixture.Controller.Versions.Page, shell.Stage.Navigation.Current);
            AssertEqual(launchVersion, ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        }
        string[] Paths(string id) => shell.Tree.GetComponent<XsrUiFileDrag>(FindByKey(shell, scene, "LibraryRow:version:" + id).Entity)!
            .Paths.Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path))).ToArray();
        Select("b", "ui.versions.toggle-transfer");
        Select("d", "ui.versions.extend-transfer");
        AssertEqual("b,c,d", string.Join(',', Paths("c")));
        AssertEqual("a", string.Join(',', Paths("a")));
        var sessions = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var rowB = FindByKey(shell, scene, "LibraryRow:version:b").Entity;
        var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "b", 123, MinecraftProcessState.Running, null,
            DateTimeOffset.UtcNow, null)
        { InstanceDirectory = shell.Tree.GetComponent<XsrUiFileDrag>(rowB)!.Paths[0] };
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
        scene = shell.Render(new(850, 500));
        AssertFalse(shell.Tree.GetComponent<XsrUiFileDrag>(FindByKey(shell, scene, "LibraryRow:version:d").Entity)!.Effects.HasFlag(XsrUiFileDragEffects.Move));
        AssertTrue(shell.Tree.GetComponent<XsrUiFileDrag>(FindByKey(shell, scene, "LibraryRow:version:a").Entity)!.Effects.HasFlag(XsrUiFileDragEffects.Move));
        Select("c", "ui.versions.toggle-transfer");
        AssertEqual("b,d", string.Join(',', Paths("b")));
        Select("a", "ui.versions.add-range");
        AssertEqual("a,b,c,d", string.Join(',', Paths("d")));
        shell.Renderer.SetTextInputValue(FindByKey(shell, scene, "LibrarySearch").Entity, "b");
        scene = shell.Render(new(850, 500));
        AssertEqual("b", string.Join(',', Paths("b")));
        shell.Renderer.SetTextInputValue(FindByKey(shell, scene, "LibrarySearch").Entity, "");
        scene = shell.Render(new(850, 500));
        AssertEqual("a", string.Join(',', Paths("a"))); // Hidden entries do not silently rejoin.
        Emit(fixture.Intents, "ui.page.back");
        scene = shell.Render(new(850, 500));
        shell.Renderer.Activate(FindByKey(shell, scene, "InstanceListButton").Entity);
        scene = shell.Render(new(850, 500));
        AssertEqual("d", string.Join(',', Paths("d")));
    }

    private static void VersionSelectionUsesDirectoryQualifiedLaunch()
    {
        RecordingStartRoute recording = new();
        using MinecraftRuntime runtime = CreateRecordingRuntime(recording);
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("same"), Instance("chosen")]),
            runtime, addProfile: true, ownsMinecraftRuntime: false);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiShell shell = fixture.Shell;
        XsrUiSize size = new(850, 500);
        XsrUiEntityId entry = FindByKey(shell, shell.Render(size), "InstanceListButton").Entity;
        AssertTrue(shell.Renderer.Focus(entry));
        AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
        XsrUiScene scene = shell.Render(size);
        AssertEqual("当前目录：", FindByKey(shell, scene, "LibraryDirectoryCaption").Text);
        XsrUiEntityId chosen = FindByKey(shell, scene, "LibraryRow:version:chosen").Entity;
        AssertTrue(shell.Renderer.Focus(chosen));
        AssertTrue(shell.Renderer.HandleKey(XsrUiKey.Enter));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        // Selecting a version immediately returns to the launch page; the entry regains focus.
        AssertTrue(SpinWait.SpinUntil(() => shell.Stage.Navigation.Current != fixture.Controller.Versions.Page, TimeSpan.FromSeconds(2)));
        AssertEqual("chosen", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual(entry, shell.Renderer.Focused);
        AssertEqual("chosen", FindByKey(shell, scene, "VersionName").Text);
        string original = ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey);
        string second = Path.Combine(fixture.TemporaryDirectory, "second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "keep.txt"), "keep");
        // Re-enter the version page. The browse entry opens the chooser with the manual path
        // editor (no native picker in the test host); directories later reopen the list.
        AssertTrue(shell.Renderer.Activate(entry));
        scene = shell.Render(size);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryAddDirectory").Entity));
        scene = shell.Render(size);
        shell.Renderer.SetTextInputValue(FindByKey(shell, scene, "LibraryDirectoryInput").Entity, second);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryAddPath").Entity));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        AssertEqual("second", FindByKey(shell, scene, "LibraryDirectoryName").Text);
        AssertEqual("same", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual(second, ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey));
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryChooseDirectory").Entity));
        scene = shell.Render(size);
        AssertTrue(FindByKey(shell, scene, "LibraryRow:directory:" + second).IsSelected);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryRow:directory:" + original).Entity));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        AssertEqual("chosen", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual(original, ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey));
        // Forgetting only removes registration; it never selects the removed row or deletes files.
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryChooseDirectory").Entity));
        scene = shell.Render(size);
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, scene, "LibraryDirectoryForget:directory:" + second).Entity));
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        scene = shell.Render(size);
        AssertFalse(HasKey(shell, scene, "LibraryRow:directory:" + second));
        AssertTrue(File.Exists(Path.Combine(second, "keep.txt")));
        Emit(fixture.Intents, "ui.page.back");
        AssertEqual(entry, shell.Renderer.Focused);
        scene = shell.Render(size);
        AssertEqual("chosen", FindByKey(shell, scene, "VersionName").Text);
        Emit(fixture.Intents, "ui.launch.primary");
        AssertTrue(SpinWait.SpinUntil(() => recording.LastCommand is not null, TimeSpan.FromSeconds(2)));
        AssertEqual("chosen", recording.LastCommand!.InstanceId);
        AssertEqual(original, recording.LastCommand.MinecraftRootDirectory);
    }

    private static void VersionListKeepsCompactGeometryAndIcons()
    {
        MinecraftInstanceDescriptor[] instances = [.. Enum.GetValues<MinecraftVersionKind>().Select(kind =>
        {
            MinecraftInstanceDescriptor instance = Instance(kind.ToString());
            return instance with { Version = instance.Version with { Kind = kind } };
        })];
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource(instances));
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiShell shell = fixture.Shell;
        AssertTrue(shell.Renderer.Activate(FindByKey(shell, shell.Render(new(850, 500)), "InstanceListButton").Entity));
        foreach (XsrUiSize size in new[] { new XsrUiSize(810, 470), new(850, 500), new(1280, 800) })
        {
            foreach (bool expanded in new[] { false, true })
            {
                shell.SetNavigationExpanded(expanded);
                XsrUiScene scene = shell.Render(size);
                XsrUiRect content = FindByKey(shell, scene, "LibraryContent").Rect;
                AssertContains(new(0, 0, size.Width, size.Height), content);
                AssertContains(content, FindByKey(shell, scene, "LibraryDirectoryBar").Rect);
                AssertEqual(40d, FindByKey(shell, scene, "LibraryDirectoryBar").Rect.Height);
                var refresh = FindByKey(shell, scene, "LibraryRefresh");
                var directoryBar = FindByKey(shell, scene, "LibraryDirectoryBar");
                AssertEqual(40d, refresh.Rect.Width);
                AssertTrue(string.IsNullOrEmpty(refresh.Text));
                AssertFalse(shell.Tree.GetComponent<XsrUiVisualStyle>(refresh.Entity)!.HoverExpand);
                AssertTrue(Math.Abs(refresh.Rect.X + refresh.Rect.Width - directoryBar.Rect.X - directoryBar.Rect.Width) < .01);
                AssertFalse(HasKey(shell, scene, "LibrarySelection"));
                AssertFalse(HasKey(shell, scene, "LibraryDirectoryHint"));
                AssertFalse(HasKey(shell, scene, "LibraryDirectoryPath"));
                XsrUiRect scrollRect = FindByKey(shell, scene, "LibraryVersionRows").Rect;
                AssertTrue(scrollRect.Height > 100);
                XsrUiScroll scroll = shell.Tree.GetComponent<XsrUiScroll>(FindByKey(shell, scene, "LibraryVersionRows").Entity)!;
                AssertTrue(FindByKey(shell, scene, "LibraryVersionRows").Scroll!.Value.CanScrollVertically);
                AssertTrue(shell.Renderer.PointerScroll(new(scrollRect.X + 50, scrollRect.Y + 40), 180));
                AssertTrue(scroll.OffsetY > 0);
                scroll.OffsetY = 0;
                shell.Tree.MarkDirty(FindByKey(shell, scene, "LibraryVersionRows").Entity, XsrUiDirtyKinds.Layout);
            }
        }
        XsrUiScene current = shell.Render(new(850, 500));
        foreach (MinecraftInstanceDescriptor instance in instances)
        {
            XsrUiEntityId icon = FindEntity(shell, "LibraryRowIcon:version:" + instance.Id);
            AssertEqual(VersionSelectionController.VersionIcon(instance.Version.Kind), shell.Tree.GetComponent<XsrUiImage>(icon)!.Source);
        }
        AssertEqual(12, instances.Select(instance => VersionSelectionController.VersionIcon(instance.Version.Kind)).Distinct().Count());
        string selection = ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey);
        shell.Renderer.SetTextInputValue(FindByKey(shell, current, "LibrarySearch").Entity, "Fabric");
        current = shell.Render(new(850, 500));
        AssertTrue(HasKey(shell, current, "LibraryRow:version:Fabric"));
        AssertFalse(HasKey(shell, current, "LibraryRow:version:Release"));
        AssertEqual(selection, ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        shell.Renderer.SetTextInputValue(FindByKey(shell, current, "LibrarySearch").Entity, "no such version");
        current = shell.Render(new(850, 500));
        AssertEqual("无匹配版本", FindByKey(shell, current, "LibraryEmpty").Text);
    }

    private static void VersionDirectoryPickerDiscardsLateResult()
    {
        DeferredDirectoryPicker effects = new();
        using LaunchPageFixture fixture = new(new ImmediateInstanceSource([Instance("same")]), directoryEffects: effects);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        XsrUiScene scene = fixture.Shell.Render(new(850, 500));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "InstanceListButton").Entity));
        scene = fixture.Shell.Render(new(850, 500));
        string original = ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey);
        string another = Path.Combine(fixture.TemporaryDirectory, "late");
        Directory.CreateDirectory(another);
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "LibraryAddDirectory").Entity));
        Emit(fixture.Intents, "ui.page.back");
        effects.Completion.SetResult(another);
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        AssertEqual(original, ReadCell(fixture.Store, LaunchPageState.InstanceDirectoryKey));
    }

    private static void VersionClickReturnsImmediatelyAndDoubleClickOpensDirectory()
    {
        var effects = new RecordingVersionDirectoryEffects();
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("first"), Instance("second")]), directoryEffects: effects);
        fixture.Controller.WaitUntilIdle().GetAwaiter().GetResult();
        var shell = fixture.Shell;
        var scene = shell.Render(new(850, 500));
        shell.Renderer.Activate(FindByKey(shell, scene, "InstanceListButton").Entity);
        scene = shell.Render(new(850, 500));
        var row = FindByKey(shell, scene, "LibraryRow:version:second");
        var drag = shell.Tree.GetComponent<XsrUiFileDrag>(row.Entity)!;
        AssertEqual(1, drag.Paths.Count);
        AssertTrue(drag.Paths[0].EndsWith("second", StringComparison.Ordinal));
        AssertTrue(drag.Effects.HasFlag(XsrUiFileDragEffects.Move));
        AssertEqual("ui.versions.refresh", drag.Completed.Value);
        var sessions = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "second", 123, MinecraftProcessState.Running,
            null, DateTimeOffset.UtcNow, null)
        { InstanceDirectory = drag.Paths[0] };
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
        shell.Render(new(850, 500));
        AssertFalse(shell.Tree.GetComponent<XsrUiFileDrag>(row.Entity)!.Effects.HasFlag(XsrUiFileDragEffects.Move));
        fixture.Store.PublishDelta(sessions, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(1,
            [running with { InstanceDirectory = Path.Combine(fixture.TemporaryDirectory, "another-root", "second") }], []));
        shell.Render(new(850, 500));
        AssertTrue(shell.Tree.GetComponent<XsrUiFileDrag>(row.Entity)!.Effects.HasFlag(XsrUiFileDragEffects.Move));
        Emit(fixture.Intents, "ui.versions.select", row.Entity);
        // No idle wait and no timer advancement: one render returns home.
        scene = shell.Render(new(850, 500));
        AssertTrue(shell.Stage.Navigation.Current != fixture.Controller.Versions.Page);
        AssertEqual("second", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
        AssertEqual("second", FindByKey(shell, scene, "VersionName").Text);
        AssertEqual(0, effects.Opened.Count);
        AssertTrue(effects.SecondClick is not null);
        effects.SecondClick!(); // Native input invokes this after the original row disappeared.
        fixture.Controller.Versions.WaitUntilIdle().GetAwaiter().GetResult();
        AssertEqual(1, effects.Opened.Count);
        AssertTrue(shell.Stage.Navigation.Current != fixture.Controller.Versions.Page);
        AssertEqual("second", ReadCell(fixture.Store, LaunchPageState.SelectedInstanceKey));
    }

    private sealed class RecordingVersionDirectoryEffects : IVersionDirectoryEffects
    {
        public List<string> Opened { get; } = [];
        public Action? SecondClick { get; private set; }
        public void ArmDirectoryDoubleClick(Action action) => SecondClick = action;
        public Task<string?> PickDirectoryAsync() => Task.FromResult<string?>(null);
        public Task OpenDirectoryAsync(string directory) { Opened.Add(directory); return Task.CompletedTask; }
    }
    private sealed class DeferredDirectoryPicker : IVersionDirectoryEffects
    {
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickDirectoryAsync() => Completion.Task;
    }
}
