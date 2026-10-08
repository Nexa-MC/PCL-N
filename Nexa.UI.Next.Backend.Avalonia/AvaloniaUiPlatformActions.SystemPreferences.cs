using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Native window effects and opt-in OS integrations without product/service dependencies.</summary>
public sealed partial class AvaloniaUiPlatformActions
{
    private double _windowOpacity = 1;
    private bool _windowBlur;
    private Window? _transparencyOwner;
    private IReadOnlyList<WindowTransparencyLevel>? _originalTransparency;
    private int _windowActive;
    public event Action? WindowActivated;
    public event Action<bool>? WindowActivityChanged;
    public event Action<string>? MediaControlRequested;
    public bool IsWindowActive => Volatile.Read(ref _windowActive) == 1;
    private void PublishWindowActivity(bool active)
    {
        Volatile.Write(ref _windowActive, active ? 1 : 0);
        WindowActivityChanged?.Invoke(active);
        if (active) WindowActivated?.Invoke();
    }
    private void OnMediaKey(object? sender, KeyEventArgs args)
    {
        string? command = args.Key switch
        {
            Key.MediaPlayPause => "play-pause",
            Key.MediaStop => "stop",
            Key.MediaNextTrack => "next",
            Key.VolumeMute => "mute",
            _ => null
        };
        if (command is not null && MediaControlRequested is not null) { MediaControlRequested(command); args.Handled = true; }
    }

    public void SetWindowAppearance(int opacityPercent, bool blur)
    {
        if (opacityPercent is < 40 or > 100) throw new ArgumentOutOfRangeException(nameof(opacityPercent));
        _windowOpacity = opacityPercent / 100.0;
        _windowBlur = blur;
        if (_owner is null) return;
        if (Dispatcher.UIThread.CheckAccess()) ApplyWindowAppearance();
        else Dispatcher.UIThread.Post(ApplyWindowAppearance);
    }

    private void ApplyWindowAppearance()
    {
        if (_owner is not Window window) return;
        if (!ReferenceEquals(_transparencyOwner, window))
        { _transparencyOwner = window; _originalTransparency = window.TransparencyLevelHint.ToArray(); }
        window.Opacity = _windowOpacity;
        window.TransparencyLevelHint = _windowBlur
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent]
            : _originalTransparency!;
    }

    public Task<string?> ReadClipboardTextAsync(CancellationToken token = default) => Dispatcher.UIThread.CheckAccess()
        ? ReadClipboardOnUiThreadAsync(token) : Dispatcher.UIThread.InvokeAsync(() => ReadClipboardOnUiThreadAsync(token));

    private async Task<string?> ReadClipboardOnUiThreadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IClipboard? clipboard = _owner?.Clipboard;
        if (clipboard is null) return null;
        string? text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        return text is { Length: <= 2048 } ? text : null;
    }

    public async Task<bool> NotifyNativeAsync(string title, string message, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        (title, message) = await Dispatcher.UIThread.InvokeAsync(() =>
            (LocalizeDesktopText?.Invoke(title) ?? title, LocalizeDesktopText?.Invoke(message) ?? message), DispatcherPriority.Normal, token);
        title = title[..Math.Min(title.Length, 63)]; message = message[..Math.Min(message.Length, 255)];
        if (OperatingSystem.IsWindows())
        {
            nint handle = await Dispatcher.UIThread.InvokeAsync(() => (_owner as Window)?.TryGetPlatformHandle()?.Handle ?? 0);
            if (handle == 0) return false;
            return await WindowsBalloonAsync(handle, title, message, token).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        var start = new ProcessStartInfo(OperatingSystem.IsLinux() ? "notify-send" : "/usr/bin/osascript")
        { UseShellExecute = false, CreateNoWindow = true };
        string[] arguments = OperatingSystem.IsLinux() ? ["--app-name=NexaCL", "--", title, message]
            : ["-e", "on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run", "--", title, message];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using Process process = Process.Start(start)!;
            try { await process.WaitForExitAsync(budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
            return process.ExitCode == 0;
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        { return false; }
    }

    private static async Task<bool> WindowsBalloonAsync(nint handle, string title, string message, CancellationToken token)
    {
        var data = CreateBalloon(handle, title, message);
        if (!ShellNotifyIcon(0, in data)) return false;
        try { await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { ShellNotifyIcon(2, in data); }
        return true;
    }

    private static unsafe NotificationData CreateBalloon(nint handle, string title, string message)
    {
        NotificationData data = new()
        {
            Size = (uint)sizeof(NotificationData),
            Window = handle,
            Id = 0x4E455841,
            Flags = 2 | 4 | 16,
            Icon = LoadIcon(0, 32516),
            Timeout = 10000,
            InfoFlags = 1
        };
        for (int index = 0; index < title.Length; index++) { data.InfoTitle[index] = title[index]; data.Tip[index] = title[index]; }
        for (int index = 0; index < message.Length; index++) data.Info[index] = message[index];
        return data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotificationData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        internal fixed char Tip[128];
        internal uint State, StateMask;
        internal fixed char Info[256];
        internal uint Timeout;
        internal fixed char InfoTitle[64];
        internal uint InfoFlags;
        internal Guid Item;
        internal nint BalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, in NotificationData data);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIcon(nint instance, nint icon);
}
