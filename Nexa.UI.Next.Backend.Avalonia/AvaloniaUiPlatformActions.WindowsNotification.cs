using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions
{
    private const uint NotificationCallbackMessage = 0x8E4A;
    private const nuint NotificationSubclassId = 0x4E45584E;
    private async Task<bool> WindowsNotificationWithActionAsync(string title, string message, Action activate, CancellationToken token)
    {
        var target = GCHandle.Alloc((Action)(() => PostToWindow(activate)));
        nint window = 0;
        bool subclass = false, delivered = false;
        int cleaned = 0;
        using var closed = new CancellationTokenSource();
        void Cleanup()
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
            if (subclass) RemoveNotificationSubclass(window);
            target.Free();
        }
        void OnClosed() { closed.Cancel(); Cleanup(); }
        WindowClosed += OnClosed;
        var data = CreateBalloon(0, title[..Math.Min(63, title.Length)], message[..Math.Min(255, message.Length)]);
        try
        {
            (window, subclass) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                nint handle = (_owner as Window)?.TryGetPlatformHandle()?.Handle ?? 0;
                return (handle, handle != 0 && AddNotificationSubclass(handle, GCHandle.ToIntPtr(target)));
            }, DispatcherPriority.Normal, token);
            if (!subclass) return false;
            data.Window = window; data.Flags |= 1; data.Callback = NotificationCallbackMessage;
            delivered = ShellNotifyIcon(0, in data);
            if (!delivered) return false;
            await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return delivered; }
        finally
        {
            if (delivered) _ = ShellNotifyIcon(2, in data);
            try
            {
                if (Volatile.Read(ref cleaned) == 0 && subclass)
                    await Dispatcher.UIThread.InvokeAsync(Cleanup, DispatcherPriority.Normal, closed.Token);
                else Cleanup();
            }
            catch (OperationCanceledException) { }
            finally { WindowClosed -= OnClosed; }
        }
    }
    private static unsafe bool AddNotificationSubclass(nint window, nint target) => SetWindowSubclass(window,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>)&NotificationSubclass,
        NotificationSubclassId, (nuint)target);
    private static unsafe void RemoveNotificationSubclass(nint window)
        => _ = RemoveWindowSubclass(window, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint>)&NotificationSubclass, NotificationSubclassId);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint NotificationSubclass(nint window, uint message, nuint wparam, nint lparam, nuint id, nuint target)
    {
        if (message == NotificationCallbackMessage && wparam == 0x4E455841 && lparam == 0x405)
        {
            try { if (GCHandle.FromIntPtr((nint)target).Target is Action action) action(); } catch (Exception) { }
        }
        if (message == 0x82) RemoveNotificationSubclass(window);
        return DefSubclassProc(window, message, wparam, lparam);
    }
    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowSubclass(nint window, nint callback, nuint id, nuint data);
    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool RemoveWindowSubclass(nint window, nint callback, nuint id);
    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")] private static partial nint DefSubclassProc(nint window, uint message, nuint wparam, nint lparam);
}
