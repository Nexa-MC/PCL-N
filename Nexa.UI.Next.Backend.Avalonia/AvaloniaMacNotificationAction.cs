using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>NSUserNotificationCenter native action adapter with an owned, reversible delegate lease.</summary>
internal static partial class AvaloniaMacNotificationAction
{
    private static readonly ConcurrentDictionary<nint, Action> Actions = new();
    internal static async Task<bool> ShowAsync(string title, string message, string actionLabel, Action activate,
        Action<Action> registerClose, Action<Action> unregisterClose, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        Lease? lease = null;
        Action? onClose = null;
        using var closed = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, closed.Token);
        try
        {
            lease = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var created = Install(title, message, actionLabel, activate);
                if (created is not null)
                {
                    onClose = () => { closed.Cancel(); created.Dispose(); };
                    registerClose(onClose);
                }
                return created;
            }, DispatcherPriority.Normal, token);
            if (lease is null) return false;
            await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or OperationCanceledException)
        { return false; }
        finally
        {
            try
            {
                if (lease is not null && !lease.IsDisposed)
                    await Dispatcher.UIThread.InvokeAsync(lease.Dispose, DispatcherPriority.Normal, closed.Token);
            }
            catch (OperationCanceledException) { }
            finally { if (onClose is not null) unregisterClose(onClose); }
        }
    }
    private static unsafe Lease? Install(string title, string message, string actionLabel, Action activate)
    {
        nint notificationClass = GetClass("NSUserNotification"), centerClass = GetClass("NSUserNotificationCenter");
        if (notificationClass == 0 || centerClass == 0) return null;
        nint delegateClass = GetClass("NexaCLNotificationDelegate");
        if (delegateClass == 0)
        {
            delegateClass = AllocateClass(GetClass("NSObject"), "NexaCLNotificationDelegate", 0);
            if (delegateClass == 0) return null;
            if (AddMethod(delegateClass, Selector("userNotificationCenter:didActivateNotification:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&Activated, "v@:@@") == 0
                || AddMethod(delegateClass, Selector("userNotificationCenter:shouldPresentNotification:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, byte>)&ShouldPresent, "B@:@@") == 0)
                throw new InvalidOperationException("Notification delegate ABI registration failed.");
            RegisterClass(delegateClass);
        }
        nint center = Message(centerClass, Selector("defaultUserNotificationCenter"));
        nint previous = Message(center, Selector("delegate"));
        if (previous != 0) previous = Message(previous, Selector("retain"));
        nint target = Message(delegateClass, Selector("new"));
        nint notification = Message(notificationClass, Selector("new"));
        if (center == 0 || target == 0 || notification == 0) throw new InvalidOperationException("Notification native objects are unavailable.");
        Actions[notification] = activate;
        SetObject(notification, Selector("setTitle:"), StringMessage(GetClass("NSString"), Selector("stringWithUTF8String:"), title[..Math.Min(63, title.Length)]));
        SetObject(notification, Selector("setInformativeText:"), StringMessage(GetClass("NSString"), Selector("stringWithUTF8String:"), message[..Math.Min(255, message.Length)]));
        SetBoolean(notification, Selector("setHasActionButton:"), 1);
        SetObject(notification, Selector("setActionButtonTitle:"), StringMessage(GetClass("NSString"), Selector("stringWithUTF8String:"), actionLabel));
        SetObject(center, Selector("setDelegate:"), target);
        SetObject(center, Selector("deliverNotification:"), notification);
        return new(center, target, previous, notification);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Activated(nint self, nint selector, nint center, nint notification)
    { try { if (Actions.TryRemove(notification, out var activate)) activate(); } catch (Exception) { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ShouldPresent(nint self, nint selector, nint center, nint notification) => 1;
    private sealed class Lease(nint center, nint target, nint previous, nint notification) : IDisposable
    {
        private int _disposed;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Actions.TryRemove(notification, out _);
            SetObject(center, Selector("removeDeliveredNotification:"), notification);
            if (Message(center, Selector("delegate")) == target) SetObject(center, Selector("setDelegate:"), previous);
            SendVoid(notification, Selector("release")); SendVoid(target, Selector("release"));
            if (previous != 0) SendVoid(previous, Selector("release"));
        }
    }
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjectiveC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetClass(string name);
    [LibraryImport(ObjectiveC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Selector(string name);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nint extra);
    [LibraryImport(ObjectiveC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string signature);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial nint Message(nint receiver, nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SendVoid(nint receiver, nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SetObject(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SetBoolean(nint receiver, nint selector, byte argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint StringMessage(nint receiver, nint selector, string argument);
}
