using System.Diagnostics;
using Nexa.Platform;
using Nexa.Services.Composition;
using Nexa.Services.Minecraft.Java;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JavaDiagnosticsReuseInventoryTypedFactsModulesAndRedaction()
    {
        string executable = Path.GetFullPath("java-diagnostics-fixture/bin/java");
        using var locator = new RegistrationLocator(new(new(Path.GetDirectoryName(executable)!, executable, null,
            new Version(21, 0, 1), JavaBrand.EclipseTemurin, JavaArchitecture.X64, true, false)));
        var port = new DiagnosticsPort { Output = new(PlatformJavaProbeStatus.Available, 0, "", "java.version = 21.0.1\njava.vendor = Eclipse Adoptium\nos.arch = amd64\njava.vm.name = OpenJDK 64-Bit Server VM\njava.vm.version = 21.0.1\nuser.home = /home/private-person\nuser.name=private-person\ntoken=secret-value") };
        var service = new JavaRuntimeDiagnosticsService(new(locator, null), port);
        var queries = new XsrQueryRouterBuilder(); JavaDiagnosticsRuntime.Register(queries, service); var router = queries.Build(new RecordingDispatchObserver());
        AssertTrue(router.TryResolve(JavaRuntimeDiagnosticsContract.Properties, out var properties));
        var result = await router.QueryAsync<JavaRuntimePropertiesQuery, JavaRuntimeDiagnosticsSnapshot>(properties, new(executable, 0));
        AssertTrue(result.IsSuccess); AssertEqual(JavaRuntimeDiagnosticStatus.Available, result.Value!.Status);
        AssertEqual("21.0.1", result.Value.Facts!.Version); AssertEqual("Eclipse Adoptium", result.Value.Facts.Vendor);
        AssertEqual("amd64", result.Value.Facts.Architecture); AssertTrue(result.Value.Facts.MatchesInventory);
        AssertFalse(result.Value.RedactedRaw.Contains("private-person", StringComparison.Ordinal)); AssertFalse(result.Value.RedactedRaw.Contains("secret-value", StringComparison.Ordinal));
        AssertEqual(PlatformJavaProbeKind.Properties, port.Kinds.Single());
        port.Output = new(PlatformJavaProbeStatus.Available, 0, "java.base@21.0.1\njava.desktop@21.0.1\n", "");
        AssertTrue(router.TryResolve(JavaRuntimeDiagnosticsContract.Modules, out var modules));
        result = await router.QueryAsync<JavaRuntimeModulesQuery, JavaRuntimeDiagnosticsSnapshot>(modules, new(executable, 0));
        AssertEqual(JavaRuntimeDiagnosticStatus.Available, result.Value!.Status); AssertEqual(2, result.Value.Modules.Count);
        AssertEqual(new JavaRuntimeModule("java.base", "21.0.1"), result.Value.Modules[0]); AssertEqual(PlatformJavaProbeKind.Modules, port.Kinds[^1]);
    }

    private static async ValueTask JavaDiagnosticsRejectUnknownStaleEmptyAndLegacyModules()
    {
        string executable = Path.GetFullPath("java-diagnostics-rejections/bin/java");
        using var locator = new RegistrationLocator(new(new(Path.GetDirectoryName(executable)!, executable, null,
            new Version(1, 8, 0, 402), JavaBrand.Unknown, JavaArchitecture.X64, true, true)));
        var port = new DiagnosticsPort(); var service = new JavaRuntimeDiagnosticsService(new(locator, null), port);
        AssertEqual(JavaRuntimeDiagnosticStatus.PlatformUnsupported, (await service.ReadModulesAsync(new(executable, 0))).Value!.Status);
        AssertEqual(0, port.Kinds.Count);
        AssertEqual(JavaRuntimeDiagnosticStatus.Rejected, (await service.ReadPropertiesAsync(new("relative-java", 0))).Value!.Status);
        AssertEqual(JavaRuntimeDiagnosticStatus.Rejected, (await service.ReadPropertiesAsync(new(Path.GetFullPath("unknown-java"), 0))).Value!.Status);
        AssertEqual(JavaRuntimeDiagnosticStatus.Stale, (await service.ReadPropertiesAsync(new(executable, 42))).Value!.Status);
        AssertEqual(0, port.Kinds.Count);
        var malformed = await service.ReadPropertiesAsync(new(executable, 0));
        AssertEqual(JavaRuntimeDiagnosticStatus.Malformed, malformed.Value!.Status); AssertTrue(malformed.Value.Facts is null);
        port.Output = new(PlatformJavaProbeStatus.IdentityChanged, null, "", "");
        AssertEqual(JavaRuntimeDiagnosticStatus.IdentityChanged, (await service.ReadPropertiesAsync(new(executable, 0))).Value!.Status);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool canceled = false;
        try { await service.ReadPropertiesAsync(new(executable, 0), cancelled.Token); }
        catch (OperationCanceledException) { canceled = true; }
        AssertTrue(canceled);
    }

    private static async ValueTask JavaScannerRejectsCanceledCacheAndLateInspection()
    {
        string executable = Path.GetFullPath("java-scanner-cancellation/bin/java");
        var candidate = new JavaRuntimeCandidate(new(Path.GetDirectoryName(executable)!, executable, null,
            new Version(21, 0, 1), JavaBrand.EclipseTemurin, JavaArchitecture.X64, true, false));
        using var port = new RegistrationLocator(candidate);
        var locator = new LocalJavaRuntimeLocator(inspectionPort: port);
        _ = await locator.FindAllAsync();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        bool canceled = false;
        try { _ = await locator.FindAllAsync(stop.Token); } catch (OperationCanceledException) { canceled = true; }
        AssertTrue(canceled);
        using var lateStop = new CancellationTokenSource();
        port.Probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = locator.InspectAsync(executable, lateStop.Token).AsTask();
        AssertTrue(port.Entered.Wait(TimeSpan.FromSeconds(3))); lateStop.Cancel(); port.Probe.SetResult(candidate);
        canceled = false;
        try { _ = await late; } catch (OperationCanceledException) { canceled = true; }
        AssertTrue(canceled);
    }

    private static async ValueTask PlatformJavaDiagnosticsBoundArgumentsFingerprintBytesAndReapCancellation()
    {
        if (OperatingSystem.IsWindows()) return; // Managed port contracts remain cross-platform; executable fixture uses POSIX.
        string directory = Path.Combine(Path.GetTempPath(), "nexacl-java-diagnostics-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "java"), pidFile = Path.Combine(directory, "pid");
        var platform = new PlatformJavaDiagnostics();
        try
        {
            await Fixture("#!/bin/sh\nprintf '%s\\n' \"$1\" \"$2\"\n");
            var identity = await platform.CaptureIdentityAsync(executable);
            var output = await platform.ProbeAsync(identity, PlatformJavaProbeKind.Properties);
            AssertEqual(PlatformJavaProbeStatus.Available, output.Status); AssertEqual("-XshowSettings:properties\n-version\n", output.StandardOutput);
            output = await platform.ProbeAsync(identity, PlatformJavaProbeKind.Modules);
            AssertEqual(PlatformJavaProbeStatus.Available, output.Status); AssertEqual("--list-modules\n\n", output.StandardOutput);
            await File.AppendAllTextAsync(executable, "# changed\n");
            AssertEqual(PlatformJavaProbeStatus.IdentityChanged, (await platform.ProbeAsync(identity, PlatformJavaProbeKind.Properties)).Status);
            await Fixture("#!/bin/sh\nprintf 'java.version = 21.0.1\\njava.vendor = Eclipse Adoptium\\nos.arch = amd64\\n'\n");
            var scanner = new LocalJavaRuntimeLocator();
            AssertEqual(new Version(21, 0, 1), (await scanner.InspectAsync(executable))!.Installation.Version);
            await Fixture("#!/bin/sh\n/usr/bin/head -c 70000 /dev/zero\n"); identity = await platform.CaptureIdentityAsync(executable);
            AssertEqual(PlatformJavaProbeStatus.OutputLimit, (await platform.ProbeAsync(identity, PlatformJavaProbeKind.Properties)).Status);
            AssertTrue(await scanner.InspectAsync(executable) is null);
            foreach (bool throughInventory in new[] { false, true })
            {
                File.Delete(pidFile);
                await Fixture("#!/bin/sh\necho $$ > '" + pidFile.Replace("'", "'\\''", StringComparison.Ordinal) + "'\nexec /bin/sleep 30\n");
                identity = await platform.CaptureIdentityAsync(executable);
                using var stop = new CancellationTokenSource();
                Task running = throughInventory ? scanner.InspectAsync(executable, stop.Token).AsTask()
                    : platform.ProbeAsync(identity, PlatformJavaProbeKind.Properties, stop.Token).AsTask();
                AssertTrue(SpinWait.SpinUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, TimeSpan.FromSeconds(3)));
                int pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
                stop.Cancel(); bool cancelled = false;
                try { await running.WaitAsync(TimeSpan.FromSeconds(4)); } catch (OperationCanceledException) { cancelled = true; }
                AssertTrue(cancelled);
                bool alive;
                try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
                AssertFalse(alive);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        async Task Fixture(string script)
        {
            await File.WriteAllTextAsync(executable, script);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class DiagnosticsPort : IPlatformJavaDiagnostics
    {
        internal PlatformJavaProbeOutput Output = new(PlatformJavaProbeStatus.Available, 0, "", "");
        internal List<PlatformJavaProbeKind> Kinds { get; } = [];
        public ValueTask<PlatformJavaExecutableIdentity> CaptureIdentityAsync(string executable, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new PlatformJavaExecutableIdentity(executable, 1, 1, "verified-fixture")); }
        public ValueTask<PlatformJavaProbeOutput> ProbeAsync(PlatformJavaExecutableIdentity identity, PlatformJavaProbeKind kind, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Kinds.Add(kind); return ValueTask.FromResult(Output); }
    }
}
