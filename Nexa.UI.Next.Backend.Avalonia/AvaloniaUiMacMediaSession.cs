using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>MPRemoteCommandCenter / MPNowPlayingInfoCenter through the Objective-C native ABI.</summary>
public sealed partial class AvaloniaUiMacMediaSession : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<nint, Action<string>> Targets = new();
    private static readonly string[] Commands = ["playCommand", "pauseCommand", "togglePlayPauseCommand", "stopCommand", "nextTrackCommand"];
    private static readonly string[] Handlers = ["nexaPlay:", "nexaPause:", "nexaToggle:", "nexaStop:", "nexaNext:"];
    private readonly Func<AvaloniaUiMediaState> _capture;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _control, _report;
    private readonly string _title;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private nint _center, _nowPlaying, _target, _framework;
    private AvaloniaUiMediaStatus? _published;
    private bool? _publishedEnabled;

    public AvaloniaUiMacMediaSession(Func<AvaloniaUiMediaState> capture, Func<bool> enabled,
        Action<string> control, Action<string> report, string title = "NexaCL 背景音乐")
    {
        _capture = capture; _enabled = enabled; _control = control; _report = report;
        _title = title;
        if (!OperatingSystem.IsMacOS()) { _run = Task.CompletedTask; report("RemoteCommandCenter: PlatformUnsupported."); return; }
        try { Initialize(); _run = MonitorAsync(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        { _run = Task.CompletedTask; report("RemoteCommandCenter: DependencyMissing (" + error.GetType().Name + ")."); }
    }
    private unsafe void Initialize()
    {
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Media registration requires the native UI dispatcher.");
        _framework = NativeLibrary.Load("/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer");
        _center = Message(GetClass("MPRemoteCommandCenter"), Selector("sharedCommandCenter"));
        _nowPlaying = Message(GetClass("MPNowPlayingInfoCenter"), Selector("defaultCenter"));
        if (_center == 0 || _nowPlaying == 0) throw new InvalidOperationException("MediaPlayer framework is unavailable.");
        nint type = GetClass("NexaCLMediaRemoteTarget");
        if (type == 0)
        {
            type = AllocateClass(GetClass("NSObject"), "NexaCLMediaRemoteTarget", 0);
            if (type == 0) throw new InvalidOperationException("Native media target allocation failed.");
            foreach (string handler in Handlers)
                if (AddMethod(type, Selector(handler), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&HandleCommand, "q@:@") == 0)
                    throw new InvalidOperationException("Native media handler registration failed.");
            RegisterClass(type);
        }
        _target = Message(type, Selector("new"));
        if (_target == 0) throw new InvalidOperationException("Native media target is unavailable.");
        Targets[_target] = _control;
        for (int index = 0; index < Commands.Length; index++)
            SendTwo(Message(_center, Selector(Commands[index])), Selector("addTarget:action:"), _target, Selector(Handlers[index]));
        Publish();
    }
    private async Task MonitorAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(200, _stop.Token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(Publish, DispatcherPriority.Normal, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private unsafe void Publish()
    {
        if (_target == 0 || _stop.IsCancellationRequested) return;
        bool enabled = _enabled(); AvaloniaUiMediaStatus status = _capture().Status;
        if (_published == status && _publishedEnabled == enabled) return;
        _published = status; _publishedEnabled = enabled;
        foreach (string command in Commands) SendBool(Message(_center, Selector(command)), Selector("setEnabled:"), enabled ? (byte)1 : (byte)0);
        nint[] keys = [Constant("MPMediaItemPropertyTitle"), Constant("MPNowPlayingInfoPropertyPlaybackRate")];
        nint[] values = [StringMessage(GetClass("NSString"), Selector("stringWithUTF8String:"), _title),
            DoubleMessage(GetClass("NSNumber"), Selector("numberWithDouble:"), status == AvaloniaUiMediaStatus.Playing ? 1 : 0)];
        fixed (nint* keyPointers = keys)
        fixed (nint* valuePointers = values)
        {
            nint dictionary = DictionaryMessage(GetClass("NSDictionary"), Selector("dictionaryWithObjects:forKeys:count:"), valuePointers, keyPointers, 2);
            SendOne(_nowPlaying, Selector("setNowPlayingInfo:"), enabled ? dictionary : 0);
        }
        SendInteger(_nowPlaying, Selector("setPlaybackState:"), enabled ? status switch
        { AvaloniaUiMediaStatus.Playing => 1, AvaloniaUiMediaStatus.Paused => 2, _ => 0 } : 0);
    }
    private nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(_framework, name));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint HandleCommand(nint target, nint selector, nint args)
    {
        try
        {
            string? name = Marshal.PtrToStringUTF8(SelectorName(selector));
            string? command = name switch
            {
                "nexaPlay:" => "play",
                "nexaPause:" => "pause",
                "nexaToggle:" => "play-pause",
                "nexaStop:" => "stop",
                "nexaNext:" => "next",
                _ => null
            };
            if (command is not null && Targets.TryGetValue(target, out var invoke)) { invoke(command); return 0; }
        }
        catch (Exception) { }
        return 200;
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false); await _run.ConfigureAwait(false);
        if (_target != 0) await Dispatcher.UIThread.InvokeAsync(CloseNativeOnUiThread);
        // MediaPlayer classes/selector IMPs are process runtime registrations; keep that system framework loaded.
        _stop.Dispose();
    }
    internal void CloseNativeOnUiThread()
    {
        _stop.Cancel();
        if (_target != 0)
        {
            foreach (string command in Commands) SendOne(Message(_center, Selector(command)), Selector("removeTarget:"), _target);
            Targets.TryRemove(_target, out _); SendVoid(_target, Selector("release")); _target = 0;
            SendOne(_nowPlaying, Selector("setNowPlayingInfo:"), 0);
        }
    }
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjectiveC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetClass(string name);
    [LibraryImport(ObjectiveC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Selector(string name);
    [LibraryImport(ObjectiveC, EntryPoint = "sel_getName")] private static partial nint SelectorName(nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nint extra);
    [LibraryImport(ObjectiveC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string signature);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial nint Message(nint receiver, nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SendVoid(nint receiver, nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SendOne(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SendTwo(nint receiver, nint selector, nint first, nint second);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SendBool(nint receiver, nint selector, byte argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial void SendInteger(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial nint DoubleMessage(nint receiver, nint selector, double argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial nint StringMessage(nint receiver, nint selector, string argument);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static unsafe partial nint DictionaryMessage(nint receiver, nint selector, nint* values, nint* keys, nuint count);
}
