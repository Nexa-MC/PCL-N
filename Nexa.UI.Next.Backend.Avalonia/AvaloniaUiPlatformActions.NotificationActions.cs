using System.Diagnostics;
using System.Text;
using Avalonia.Threading;

namespace Nexa.UI.Next.Backend.Avalonia;

public sealed partial class AvaloniaUiPlatformActions
{
    /// <summary>Linux notification actions return through a native callback; other adapters retain delivery.</summary>
    public async Task<bool> NotifyNativeWithActionAsync(string title, string message, Action activate, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(activate);
        string actionLabel;
        (title, message, actionLabel) = await Dispatcher.UIThread.InvokeAsync(() =>
            (LocalizeDesktopText?.Invoke(title) ?? title, LocalizeDesktopText?.Invoke(message) ?? message,
                LocalizeDesktopText?.Invoke("打开 NexaCL") ?? "打开 NexaCL"), DispatcherPriority.Normal, token);
        if (OperatingSystem.IsWindows()) return await WindowsNotificationWithActionAsync(title, message, activate, token).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS())
        {
            if (await AvaloniaMacNotificationAction.ShowAsync(title, message, actionLabel, () => PostToWindow(activate),
                action => WindowClosed += action, action => WindowClosed -= action, token).ConfigureAwait(false)) return true;
            return await NotifyNativeAsync(title, message, token).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsLinux()) return await NotifyNativeAsync(title, message, token).ConfigureAwait(false);
        ArgumentException.ThrowIfNullOrWhiteSpace(title); ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var start = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "--app-name=NexaCL", "--action=nexacl=" + actionLabel, "--wait", "--expire-time=10000", "--",
            title[..Math.Min(63, title.Length)], message[..Math.Min(255, message.Length)] }) start.ArgumentList.Add(argument);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using Process process = Process.Start(start) ?? throw new IOException("Native notification tool did not start.");
            try
            {
                byte[] response = new byte[65]; int count = 0;
                while (count < response.Length)
                {
                    int read = await process.StandardOutput.BaseStream.ReadAsync(response.AsMemory(count), budget.Token).ConfigureAwait(false);
                    if (read == 0) break; count += read;
                }
                if (count == response.Length) { process.Kill(); return false; }
                await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
                if (process.ExitCode != 0) return false;
                if (Encoding.UTF8.GetString(response, 0, count).Trim() == "nexacl") PostToWindow(activate);
                return true;
            }
            catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or OperationCanceledException) { return false; }
    }
}
