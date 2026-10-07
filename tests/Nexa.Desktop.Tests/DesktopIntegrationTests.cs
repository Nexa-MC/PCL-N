using System.Diagnostics;
using Nexa.Desktop;
using Nexa.Desktop.Ui;
using Nexa.Services.Files;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void DesktopBrandAndBootstrapDefaults()
    {
        AssertEqual("NexaCL Firefly Alpha 6", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0.alpha.6"));
        AssertEqual("NexaCL Firefly Beta 2", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0.beta.2"));
        AssertEqual("NexaCL Firefly CI", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0.ci.abcdef"));
        AssertEqual("NexaCL Firefly", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0"));
        XsrStateStoreBuilder builder = new(); LaunchPageState.DeclareState(builder);
        var shell = PxmlShellComposer.Compose(builder.Build(), new XsrUiShellOptions
        { Title = "NexaCL Firefly Alpha 6", Version = "2.0.0.alpha.6" });
        var scene = shell.Render(new(1024, 600));
        var title = scene.Nodes.Single(node => node.Text == "NexaCL Firefly Alpha 6");
        var version = scene.Nodes.Single(node => node.Text == "v2.0.0.alpha.6");
        AssertTrue(version.Rect.Y >= title.Rect.Y + title.Rect.Height);
        AssertTrue(version.VisualStyle.FontSize < title.VisualStyle.FontSize);
        string root = Path.Combine(Path.GetTempPath(), "nexa-desktop-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            AssertTrue(Nexa.Desktop.Program.SingleInstanceEnabled(root));
            Directory.CreateDirectory(Path.Combine(root, FolderNames.Settings));
            string path = Path.Combine(root, FolderNames.Settings, "settings.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"booleanOptions\":{\"SystemSingleInstance\":false}}");
            AssertTrue(!Nexa.Desktop.Program.SingleInstanceEnabled(root));
            foreach (string invalid in new[] { "[]", "{\"schemaVersion\":\"1\"}", "{\"schemaVersion\":2,\"booleanOptions\":{\"SystemSingleInstance\":false}}", "broken" })
            {
                File.WriteAllText(path, invalid);
                AssertTrue(Nexa.Desktop.Program.SingleInstanceEnabled(root));
                AssertEqual(invalid, File.ReadAllText(path));
            }
            AssertTrue(LauncherDefaults.BooleanDefaults["SystemSingleInstance"]);
            foreach (string key in new[] { "general.single-instance", "general.tray", "general.close-to-tray", "general.minimize-to-tray" })
                AssertEqual(SettingsCapabilityAvailability.Available, SettingsCatalog.Entries.Single(entry => entry.SettingKey == key).Availability);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void DesktopSingleInstanceWaitsForClosingOwner()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-instance-closing-" + Guid.NewGuid().ToString("N"));
        try
        {
            var primary = DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Activate).GetAwaiter().GetResult()!;
            primary.BeginShutdown();
            var replacement = DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Settings);
            AssertTrue(!replacement.IsCompleted);
            primary.DisposeAsync().AsTask().GetAwaiter().GetResult();
            var resumed = replacement.GetAwaiter().GetResult();
            AssertTrue(resumed is not null);
            AssertTrue(resumed!.TryTake(out var destination)); AssertEqual(DesktopDestination.Settings, destination);
            resumed.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void DesktopProtocolRoutesAndRegistrationCommands()
    {
        AssertTrue(DesktopActivation.TryParse("nexacl://", out var activation)); AssertEqual(DesktopDestination.Activate, activation);
        foreach (var pair in new[] { ("launch", DesktopDestination.Launch), ("install", DesktopDestination.Install), ("resources", DesktopDestination.Resources), ("settings", DesktopDestination.Settings) })
            foreach (string uri in new[] { "nexacl://" + pair.Item1, "nexacl://open/" + pair.Item1 })
            { AssertTrue(DesktopActivation.TryParse(uri, out var result)); AssertEqual(pair.Item2, result); }
        foreach (string invalid in new[] { "https://settings", "nexacl://settings/extra", "nexacl://settings?run=cmd", "nexacl://user@settings", "nexacl://settings:123", "nexacl://settings#x", "nexacl://execute", "nexacl://" + new string('a', 2049) })
            AssertTrue(!DesktopActivation.TryParse(invalid, out _));
        AssertEqual("\"C:\\App Space\\Nexa.Desktop.exe\" \"%1\"", DesktopProtocolRegistration.WindowsCommand([@"C:\App Space\Nexa.Desktop.exe"]));
        string desktop = DesktopProtocolRegistration.LinuxDesktopEntry(["/opt/App Space/100%/dotnet", "/opt/app/Nexa.Desktop.dll"]);
        AssertTrue(desktop.Contains("Exec=\"/opt/App Space/100%%/dotnet\" \"/opt/app/Nexa.Desktop.dll\" %u", StringComparison.Ordinal));
        AssertTrue(desktop.Contains("MimeType=x-scheme-handler/nexacl;", StringComparison.Ordinal));
        AssertTrue(!desktop.Contains("sh -c", StringComparison.Ordinal));
    }

    private static void DesktopSingleInstanceForwardsAcrossProcessesAndReleases()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-instance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var primary = DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Activate).GetAwaiter().GetResult();
            AssertTrue(primary is not null);
            try
            {
                AssertTrue(primary!.TryTake(out var initial)); AssertEqual(DesktopDestination.Activate, initial);
                int wakes = 0; primary.Wake = () => Interlocked.Increment(ref wakes);
                IReadOnlyList<string> command = DesktopProtocolRegistration.LauncherCommand();
                var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in command.Skip(1)) start.ArgumentList.Add(argument);
                start.ArgumentList.Add("--desktop-instance-peer"); start.ArgumentList.Add(directory);
                using Process peer = Process.Start(start)!;
                AssertTrue(peer.WaitForExit(10000)); AssertEqual(0, peer.ExitCode);
                AssertTrue(primary.TryTake(out var forwarded)); AssertEqual(DesktopDestination.Settings, forwarded);
                AssertEqual(1, Volatile.Read(ref wakes));
                // Fill the bounded queue, then reject without replacing the primary lease.
                for (int index = 0; index < 16; index++)
                    AssertTrue(DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Activate).GetAwaiter().GetResult() is null);
                bool rejected = false;
                try { _ = DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Activate).GetAwaiter().GetResult(); }
                catch (IOException) { rejected = true; }
                AssertTrue(rejected);
                for (int index = 0; index < 16; index++) AssertTrue(primary.TryTake(out _));
            }
            finally { primary!.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            var restarted = DesktopSingleInstance.AcquireAsync(directory, DesktopDestination.Install).GetAwaiter().GetResult();
            AssertTrue(restarted is not null);
            restarted!.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
