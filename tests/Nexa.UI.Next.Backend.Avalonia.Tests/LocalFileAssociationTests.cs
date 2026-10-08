using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void LocalFileAssociationAdmitsRegularPathsAndCapturesIndependentArguments()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-local-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "设置 ;$().json");
        File.WriteAllText(file, "{}");
        List<ProcessStartInfo> invocations = [];
        var actions = new AvaloniaUiPlatformActions { LocalFileInvocation = invocations.Add };
        Window? window = null;
        try
        {
            Reject<InvalidOperationException>(() => actions.OpenLocalFile(file));
            window = new Window(); actions.Attach(window);
            actions.OpenLocalFile(file);
            AssertEqual(1, invocations.Count);
            var invocation = invocations[0];
            AssertEqual("", invocation.Arguments);
            if (OperatingSystem.IsWindows())
            {
                AssertEqual(file, invocation.FileName); AssertTrue(invocation.UseShellExecute); AssertEqual(0, invocation.ArgumentList.Count);
            }
            else
            {
                AssertEqual(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open", invocation.FileName);
                AssertFalse(invocation.UseShellExecute); AssertEqual(1, invocation.ArgumentList.Count); AssertEqual(file, invocation.ArgumentList[0]);
            }
            Reject<ArgumentException>(() => actions.OpenLocalFile("settings.json"));
            Reject<ArgumentException>(() => actions.OpenLocalFile(file + "\n"));
            Reject<ArgumentException>(() => actions.OpenLocalFile("https://example.org/settings.json"));
            Reject<FileNotFoundException>(() => actions.OpenLocalFile(Path.Combine(directory, "missing.json")));
            Reject<FileNotFoundException>(() => actions.OpenLocalFile(directory));
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(directory, "link.json");
                File.CreateSymbolicLink(link, file);
                Reject<ArgumentException>(() => actions.OpenLocalFile(link));
            }
            if (OperatingSystem.IsLinux())
            {
                string fifo = Path.Combine(directory, "named-pipe.json");
                AssertEqual(0, CreateLocalFileFixturePipe(fifo, 0x180));
                AssertTrue(File.Exists(fifo)); // File.Exists alone does not establish a regular file on Unix.
                Reject<ArgumentException>(() => actions.OpenLocalFile(fifo));
                Reject<ArgumentException>(() => actions.OpenLocalFile("/dev/null"));
            }
            AssertEqual(1, invocations.Count); // No denied input reaches the native process edge.
        }
        finally { window?.Close(); Directory.Delete(directory, true); }

        static void Reject<T>(Action action) where T : Exception
        {
            bool rejected = false;
            try { action(); } catch (Exception error) when (error is T) { rejected = true; }
            AssertTrue(rejected);
        }
    }

    [LibraryImport("libc", EntryPoint = "mkfifo", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int CreateLocalFileFixturePipe(string path, uint mode);
}
