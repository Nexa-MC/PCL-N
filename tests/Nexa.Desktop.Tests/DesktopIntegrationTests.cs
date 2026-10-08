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
        AssertEqual("NexaCL Firefly v2.0.0.alpha.6", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0.alpha.6"));
        AssertEqual("NexaCL Firefly v2.0.0.beta.2", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0.beta.2"));
        AssertEqual("NexaCL Firefly v2.0.0.ci.abcdef", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0.ci.abcdef"));
        AssertEqual("NexaCL Firefly v2.0.0", Nexa.Desktop.Program.ProductDisplayTitle("2.0.0"));
        XsrStateStoreBuilder builder = new(); LaunchPageState.DeclareState(builder);
        var shell = PxmlShellComposer.Compose(builder.Build(), new XsrUiShellOptions
        { Title = "NexaCL", Version = "2.0.0.alpha.6" });
        var scene = shell.Render(new(1024, 600));
        AssertEqual("NexaCL", shell.Title);
        AssertTrue(scene.Nodes.Any(node => node.Text == "NexaCL"));
        AssertTrue(!scene.Nodes.Any(node => node.Text == "v2.0.0.alpha.6"));
        string root = Path.Combine(Path.GetTempPath(), "nexa-desktop-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            AssertTrue(Nexa.Desktop.Program.SingleInstanceEnabled(root));
            AssertFalse(Nexa.Desktop.Program.HardwareAccelerationDisabled(root));
            Directory.CreateDirectory(Path.Combine(root, FolderNames.Settings));
            string path = Path.Combine(root, FolderNames.Settings, "settings.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"booleanOptions\":{\"SystemSingleInstance\":false}}");
            AssertTrue(!Nexa.Desktop.Program.SingleInstanceEnabled(root));
            File.WriteAllText(path, "{\"schemaVersion\":1,\"booleanOptions\":{\"SystemDisableHardwareAcceleration\":true}}");
            AssertTrue(Nexa.Desktop.Program.HardwareAccelerationDisabled(root));
            foreach (string value in new[] { "false", "\"true\"", "null" })
            {
                File.WriteAllText(path, "{\"schemaVersion\":1,\"booleanOptions\":{\"SystemDisableHardwareAcceleration\":" + value + "}}");
                AssertFalse(Nexa.Desktop.Program.HardwareAccelerationDisabled(root));
            }
            foreach (string invalid in new[] { "[]", "{\"schemaVersion\":\"1\"}", "{\"schemaVersion\":2,\"booleanOptions\":{\"SystemSingleInstance\":false}}", "broken" })
            {
                File.WriteAllText(path, invalid);
                AssertTrue(Nexa.Desktop.Program.SingleInstanceEnabled(root));
                AssertFalse(Nexa.Desktop.Program.HardwareAccelerationDisabled(root));
                AssertEqual(invalid, File.ReadAllText(path));
            }
            string oversized = "{\"schemaVersion\":1,\"booleanOptions\":{\"SystemSingleInstance\":false,\"SystemDisableHardwareAcceleration\":true}}"
                + new string(' ', 4 * 1024 * 1024);
            File.WriteAllText(path, oversized);
            AssertTrue(Nexa.Desktop.Program.SingleInstanceEnabled(root));
            AssertFalse(Nexa.Desktop.Program.HardwareAccelerationDisabled(root));
            AssertEqual(oversized, File.ReadAllText(path));
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
        string pack = Path.Combine(Path.GetTempPath(), "整合包 with spaces.mrpack");
        AssertTrue(DesktopActivation.TryFile(pack, out string admitted)); AssertEqual(Path.GetFullPath(pack), admitted);
        AssertTrue(DesktopActivation.TryFile(Path.ChangeExtension(pack, ".NEXAPACK"), out _));
        foreach (string invalid in new[] { "relative.mrpack", "https://example.test/x.mrpack", "file:///tmp/a.mrpack", pack + "\n", pack + "\0", Path.ChangeExtension(pack, ".jar"), Path.Combine(Path.GetTempPath(), "bad\ud800.mrpack") })
            AssertTrue(!DesktopActivation.TryFile(invalid, out _));
        string prefix = Path.GetTempPath();
        string maximum = prefix + new string('a', DesktopActivation.MaximumFileBytes - System.Text.Encoding.UTF8.GetByteCount(prefix) - 7) + ".mrpack";
        AssertTrue(DesktopActivation.TryFile(maximum, out _));
        AssertTrue(!DesktopActivation.TryFile(maximum.Insert(prefix.Length, "a"), out _));
        DesktopFilesWaitForRendererFrame();
        DesktopJumpListPreservesBoundedLauncherArguments();
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
                string file = Path.Combine(directory, "中文 Pack.nexapack");
                var fileStart = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in command.Skip(1)) fileStart.ArgumentList.Add(argument);
                fileStart.ArgumentList.Add("--desktop-instance-file-peer"); fileStart.ArgumentList.Add(directory); fileStart.ArgumentList.Add(file);
                using (Process filePeer = Process.Start(fileStart)!)
                { AssertTrue(filePeer.WaitForExit(10000)); AssertEqual(0, filePeer.ExitCode); }
                AssertTrue(primary.TryTakeActivation(out var forwardedFile));
                AssertEqual(DesktopDestination.Install, forwardedFile.Destination); AssertEqual(file, forwardedFile.File!);
                AssertEqual(2, Volatile.Read(ref wakes));
                SendMalformedDesktopActivation(directory, [2, (byte)DesktopDestination.Activate, 2, 0, 0xff, 0xff]);
                SendMalformedDesktopActivation(directory, [2, (byte)DesktopDestination.Activate, 1, 32]);
                SendMalformedDesktopActivation(directory, [7, (byte)DesktopDestination.Activate]);
                AssertTrue(!primary.TryTakeActivation(out _));
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

    private static void SendMalformedDesktopActivation(string directory, byte[] packet)
    {
        string path = Path.Combine(Path.GetFullPath(directory), "instance.lock");
        string pipe = "nexacl-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..24];
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
        client.WriteAsync(packet, timeout.Token).AsTask().GetAwaiter().GetResult();
        byte[] reply = new byte[1]; client.ReadExactlyAsync(reply, timeout.Token).AsTask().GetAwaiter().GetResult();
        AssertEqual((byte)0, reply[0]);
        client.WriteAsync(new byte[] { 0xA6 }, timeout.Token).AsTask().GetAwaiter().GetResult();
    }

    private static void DesktopFilesWaitForRendererFrame()
    {
        var store = new XsrStateStoreBuilder().Build();
        var shell = XsrUiShellComposer.Compose(store);
        var intents = new DesktopUiIntentSink();
        var platform = new Nexa.UI.Next.Backend.Avalonia.AvaloniaUiPlatformActions();
        var mediaCommand = Nexa.Xsr.XsrSemanticId.Parse("ui.media.play-pause");
        shell.Tree.SetComponent(shell.Root, new XsrUiContextMenu([new("音乐播放 / 暂停", mediaCommand)]));
        List<string> opened = [];
        string file = Path.Combine(Path.GetTempPath(), "confirmation-required.mrpack");
        var integration = new DesktopIntegrationSession(shell, intents, store, platform, null, [file],
            static _ => { }, opened.Add, registerProtocol: false);
        try
        {
            AssertEqual(1, shell.Tree.GetComponent<XsrUiContextMenu>(shell.Root)!.Items.Count(item => item.Command == mediaCommand));
            AssertEqual(0, opened.Count);
            shell.Render(new(800, 600));
            AssertEqual(1, opened.Count); AssertEqual(file, opened[0]);
            shell.Render(new(800, 600)); AssertEqual(1, opened.Count);
        }
        finally { integration.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        AssertEqual(mediaCommand, shell.Tree.GetComponent<XsrUiContextMenu>(shell.Root)!.Items.Single().Command);
    }
}
