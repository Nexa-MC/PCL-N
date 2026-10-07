using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nexa.UI.Next.Backend.Avalonia;

/// <summary>Linux may expose a tray API while no notification-area host is running.</summary>
internal static partial class AvaloniaTrayAvailability
{
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? display);
    [LibraryImport("libX11.so.6")]
    private static partial int XDefaultScreen(IntPtr display);
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint XInternAtom(IntPtr display, string name, int onlyIfExists);
    [LibraryImport("libX11.so.6")]
    private static partial nuint XGetSelectionOwner(IntPtr display, nuint atom);
    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(IntPtr display);

    private static bool HasXEmbedHost()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(null);
            if (display == IntPtr.Zero) return false;
            nuint atom = XInternAtom(display, "_NET_SYSTEM_TRAY_S" + XDefaultScreen(display).ToString(CultureInfo.InvariantCulture), 1);
            return atom != 0 && XGetSelectionOwner(display, atom) != 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally { if (display != IntPtr.Zero) _ = XCloseDisplay(display); }
    }

    internal static bool ParseBusOwner(string response) => response.Trim() is "(true,)" or "(true)";

    internal static async Task<bool> IsAvailableAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return true;
        if (HasXEmbedHost()) return true;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return false;
        foreach (string watcher in new[] { "org.kde.StatusNotifierWatcher", "org.freedesktop.StatusNotifierWatcher" })
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            var start = new ProcessStartInfo("gdbus")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.NameHasOwner", watcher })
                start.ArgumentList.Add(argument);
            try
            {
                using Process process = Process.Start(start)!;
                try
                {
                    char[] output = new char[128];
                    int length = await process.StandardOutput.ReadBlockAsync(output.AsMemory(), deadline.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    if (process.ExitCode == 0 && length < output.Length && ParseBusOwner(new string(output, 0, length))) return true;
                }
                finally
                {
                    if (!process.HasExited) try { process.Kill(); } catch (InvalidOperationException) { }
                }
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
            { if (token.IsCancellationRequested) return false; }
        }
        return false;
    }
}
