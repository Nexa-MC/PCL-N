using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexa.Services.Resources;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;

namespace Nexa.Desktop.Tests;

// Run only through --native-resource-scroll-smoke in a separate process; the ordinary
// executable suite must not initialize an Avalonia application or a native dispatcher.
internal static partial class Program
{
    private static readonly int[] NativeResourceExpectedPages = [0, 1, 2, 2];
    private static int RunNativeResourceScrollSmoke() => RunNativeResourceScrollSmokeAsync().GetAwaiter().GetResult();

    private static async Task<int> RunNativeResourceScrollSmokeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var startup = await AvaloniaUiStartupSession.StartWithAppearanceAsync([], disableHardwareAcceleration: true,
            new AvaloniaUiStartupAppearance(XsrUiThemeMode.Light, ReducedMotion: true), cancellationToken: timeout.Token);
        ContinuousResourceHarness? fixture = null;
        AvaloniaUiShellWindow? window = null;
        AvaloniaUiSceneSurface? surface = null;
        Task<int>? nativeLifetime = null;
        int outcome = 1;
        try
        {
            // Compose the retained controller on its eventual owner thread. There is no
            // simultaneous headless harness pumping once the native surface owns rendering.
            await startup.InvokeAsync(() =>
            {
                fixture = new();
                fixture.Complete(0, ContinuousProjects("First", 20), hasMore: true);
            }, timeout.Token);
            var f = fixture!;
            await startup.PrepareShellAsync(f.Fixture.Shell, token: timeout.Token);
            await startup.WarmUpShellAsync(f.Fixture.Shell, timeout.Token);
            nativeLifetime = Task.Run(() => AvaloniaUiShellHost.Run(f.Fixture.Shell));
            await Eventually(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow is not AvaloniaUiShellWindow candidate || !candidate.IsVisible) return false;
                window = candidate;
                surface = candidate.GetVisualDescendants().OfType<AvaloniaUiSceneSurface>().Single();
                window.UpdateLayout(); surface.CommitScene();
                return surface.Scene is { Count: > 0 } && f.Reads.Count == 1 && f.Rows.Length > 0;
            });
            var nativeSurface = surface!;
            var nativeWindow = window!;
            XsrUiEntityId list = f.Page.Find("ResourceList");
            XsrUiEntityId firstAnchor = default;
            XsrUiEntityId secondAnchor = default;
            double firstOffset = 0, secondOffset = 0, initialHeight = 0, secondHeight = 0;
            Pointer? nativePointer = null;
            await startup.InvokeAsync(() => nativePointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true), timeout.Token);
            using var pointer = nativePointer!;

            await startup.InvokeAsync(() =>
            {
                AssertTrue(nativeWindow.IsVisible && !nativeLifetime!.IsCompleted && startup.ShellReadyElapsed.HasValue);
                initialHeight = Snapshot().ContentHeight;
                RaiseWheel(-1000000, requireHandled: true);
            }, timeout.Token);
            await Eventually(() => f.Reads.Count == 2 && f.Rows.Any(row => f.Fixture.Shell.Tree.Name(row) == "ResourceProject.First19"));
            await startup.InvokeAsync(() =>
            {
                firstAnchor = f.Row("First19"); firstOffset = Snapshot().OffsetY;
                AssertTrue(firstOffset > 0);
                AssertEqual(f.Reads[0].Query with { Page = 1 }, f.Reads[1].Query);
                AssertEqual(1, f.Reads[1].Query.Page);
                AssertTrue(nativeSurface.Scene!.Nodes.Single(node => node.Entity == firstAnchor).SuppressEntryAnimation);
                // Keep the next request unresolved while the actual backend receives more
                // wheel events. It must not admit a second copy of the same batch.
                for (int pass = 0; pass < 6; pass++) { RaiseWheel(-1, requireHandled: false); nativeSurface.CommitScene(); }
                AssertEqual(2, f.Reads.Count);
                f.Complete(1, ContinuousProjects("Second", 20), hasMore: true);
            }, timeout.Token);
            await Eventually(() => Snapshot().ContentHeight > initialHeight);
            await startup.InvokeAsync(() =>
            {
                AssertEqual(firstAnchor, f.Row("First19")); AssertClose(firstOffset, Snapshot().OffsetY);
                AssertEqual(2, f.Reads.Count);
                RaiseWheel(-1000000, requireHandled: true);
            }, timeout.Token);
            await Eventually(() => f.Reads.Count == 3 && f.Rows.Any(row => f.Fixture.Shell.Tree.Name(row) == "ResourceProject.Second19"));
            await startup.InvokeAsync(() =>
            {
                secondAnchor = f.Row("Second19"); secondOffset = Snapshot().OffsetY; secondHeight = Snapshot().ContentHeight;
                AssertEqual(2, f.Reads[2].Query.Page);
                f.Reads[2].Reply.SetResult(XsrResult.Failure<ResourceSearchResult>(new(XsrErrorKind.Rejected,
                    XsrSemanticId.Parse("fixture.native.resource.offline"), "Provider unavailable")));
            }, timeout.Token);
            await Eventually(() => f.NextEnabled);
            await startup.InvokeAsync(() =>
            {
                AssertEqual(secondAnchor, f.Row("Second19")); AssertClose(secondOffset, Snapshot().OffsetY);
                for (int pass = 0; pass < 6; pass++) nativeSurface.CommitScene();
                AssertEqual(3, f.Reads.Count); // Failure requires the explicit retry action.
                RaiseClick(f.Page.Find("ResourceNext"));
            }, timeout.Token);
            await Eventually(() => f.Reads.Count == 4);
            await startup.InvokeAsync(() =>
            {
                AssertEqual(f.Reads[2].Query, f.Reads[3].Query);
                AssertEqual(2, f.Reads[3].Query.Page);
                f.Complete(3, ContinuousProjects("Third", 20), hasMore: false);
            }, timeout.Token);
            await Eventually(() => Snapshot().ContentHeight > secondHeight && !f.NextEnabled);
            await startup.InvokeAsync(() =>
            {
                AssertEqual(secondAnchor, f.Row("Second19")); AssertClose(secondOffset, Snapshot().OffsetY);
                AssertTrue(f.Reads.Select(read => read.Query.Page).SequenceEqual(NativeResourceExpectedPages));
                AssertTrue(f.Rows.Length <= 48);
                AssertTrue(nativeSurface.Scene!.Nodes.Single(node => node.Entity == secondAnchor).SuppressEntryAnimation);
            }, timeout.Token);
            outcome = 0;

            XsrUiScrollSnapshot Snapshot()
            {
                AssertTrue(f.Fixture.Shell.Renderer.TryGetScrollSnapshot(list, out var snapshot));
                return snapshot;
            }

            void RaiseWheel(double delta, bool requireHandled)
            {
                var bounds = nativeSurface.Scene!.Nodes.Single(node => node.Entity == list).Rect;
                var position = nativeSurface.TranslatePoint(new Point(bounds.X + 20, bounds.Y + 20), nativeWindow)!.Value;
                var args = new PointerWheelEventArgs(nativeSurface, pointer, nativeWindow, position, 0,
                    default, KeyModifiers.None, new Vector(0, delta));
                nativeSurface.RaiseEvent(args);
                if (requireHandled) AssertTrue(args.Handled);
            }

            void RaiseClick(XsrUiEntityId button)
            {
                var bounds = nativeSurface.Scene!.Nodes.Single(node => node.Entity == button).Rect;
                var position = nativeSurface.TranslatePoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), nativeWindow)!.Value;
                var press = new PointerPressedEventArgs(nativeSurface, pointer, nativeWindow, position, 0,
                    new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
                nativeSurface.RaiseEvent(press); AssertTrue(press.Handled);
                var release = new PointerReleasedEventArgs(nativeSurface, pointer, nativeWindow, position, 1,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left);
                nativeSurface.RaiseEvent(release); AssertTrue(release.Handled);
            }

            async Task Eventually(Func<bool> condition)
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    bool ready = false;
                    await startup.InvokeAsync(() =>
                    {
                        surface?.CommitScene();
                        ready = condition();
                    }, timeout.Token);
                    if (ready) return;
                    // This is deadline-bound dispatcher polling, not a timing assumption.
                    await Task.Delay(10, timeout.Token);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Console.Error.WriteLine("FAIL: native resource scrolling smoke\n" + error);
            outcome = 1;
        }
        finally
        {
            // Close while the dispatcher still owns the native tree; then dispose the
            // fixture on that same thread. No InvokeAsync is attempted after lifetime exit.
            if (!startup.Completion.IsCompleted)
                await Dispatcher.UIThread.InvokeAsync(() => { window?.Close(); fixture?.Dispose(); });
            else fixture?.Dispose();
            if (nativeLifetime is not null) AssertEqual(0, await nativeLifetime.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        if (outcome == 0) Console.WriteLine("PASS: native resource wheel routing appends once, retains entity/offset, and explicitly retries the same failed page with a clean native exit.");
        return outcome;
    }
}
