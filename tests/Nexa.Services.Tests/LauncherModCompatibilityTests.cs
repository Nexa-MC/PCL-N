using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Jvm.Host;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LauncherModsUseGameDirectoryAndKeepPrivateBootstrap()
    {
        string root = CreateTempDirectory();
        const string credential = "launcher-mod-private-credential-fixture";
        try
        {
            string instance = Path.Combine(root, "versions", "loader"), vanilla = Path.Combine(root, "versions", "base");
            Directory.CreateDirectory(instance); Directory.CreateDirectory(vanilla);
            string jar = Path.Combine(vanilla, "base.jar"); File.WriteAllBytes(jar, [0]);
            var request = new MinecraftLaunchRequest
            {
                VersionId = "loader",
                VersionJson = JsonNode.Parse("""
                    {"id":"loader","inheritsFrom":"base","mainClass":"fixture.Loader"}
                    """)!.AsObject(),
                InheritedVersionJsons = [JsonNode.Parse("""
                    {"id":"base","mainClass":"fixture.Base","arguments":{
                      "jvm":["-Dminecraft.launcher.brand=${launcher_name}","-Dminecraft.launcher.version=${launcher_version}"],
                      "game":["--gameDir","${game_directory}","--accessToken","${auth_access_token}"]}}
                    """)!.AsObject()],
                InstanceDirectory = instance,
                MinecraftRootDirectory = root,
                PlayerName = "fixture",
                PlayerUuid = "fixture",
                AccessToken = credential,
                LauncherName = "NexaCL",
                LauncherVersion = "2.0.0.alpha.7",
                EnvironmentVariables = new Dictionary<string, string> { ["DisableEntirelyCrashAssistantModOnSystem"] = "true" },
            };
            var shared = MinecraftLaunchPlanner.CreatePlan(request);
            AssertEqual(root, shared.WorkingDirectory); AssertEqual(root, shared.GameDirectory);
            AssertEqual(instance, shared.InstanceDirectory); AssertEqual(jar, shared.ClientJarPath);
            AssertTrue(shared.ClasspathEntries.All(Path.IsPathFullyQualified));
            AssertTrue(shared.Arguments.Contains("-Dminecraft.launcher.brand=NexaCL"));
            AssertTrue(shared.Arguments.Contains("-Dminecraft.launcher.version=2.0.0.alpha.7"));
            AssertEqual(root, shared.Arguments[shared.Arguments.ToList().IndexOf("--gameDir") + 1]);
            AssertEqual("true", shared.ToStartInfo().Environment["DisableEntirelyCrashAssistantModOnSystem"]!);
            var isolated = MinecraftLaunchPlanner.CreatePlan(request with { IsolatedGameDirectory = true });
            AssertEqual(instance, isolated.WorkingDirectory); AssertEqual(instance, isolated.GameDirectory);
            AssertEqual(jar, isolated.ClientJarPath);

            using MemoryStream bootstrap = new();
            await JvmHostBootstrap.WriteAsync(bootstrap, shared);
            bootstrap.Position = 0;
            var received = await JvmHostBootstrap.ReadAsync(bootstrap);
            AssertEqual(root, received.WorkingDirectory);
            AssertEqual("fixture.Loader", received.MainClass);
            AssertTrue(received.GameArguments.Contains(credential));
            AssertFalse(received.JvmArguments.Any(argument => argument.Contains(credential, StringComparison.Ordinal)));

            var recording = new LauncherModRecordingPort();
            await using var service = new MinecraftProcessService(recording,
                jvmHostExecutable: Path.Combine(root, "Nexa.Jvm.Host"));
            try { await service.StartAsync(shared, "launcher-mod-fixture"); throw new InvalidOperationException("Recording port did not stop."); }
            catch (IOException error) when (error.Message == "launcher-mod-recording-stop") { }
            AssertEqual("--jvm-host", recording.StartInfo!.ArgumentList.Single());
            AssertEqual(root, recording.StartInfo.WorkingDirectory);
            AssertTrue(recording.StartInfo.RedirectStandardInput);
            AssertFalse(recording.StartInfo.Environment.Values.Any(value => value?.Contains(credential, StringComparison.Ordinal) == true));
            AssertEqual(0, service.ListSessions().Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class LauncherModRecordingPort : IMinecraftProcessPort
    {
        internal ProcessStartInfo? StartInfo { get; private set; }
        public ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo info, CancellationToken cancellationToken = default)
        {
            StartInfo = info;
            throw new IOException("launcher-mod-recording-stop");
        }
    }

    private static void LauncherJavaChildBridgeIsBoundedAndPreservesExactArgv()
    {
        string root = CreateTempDirectory();
        try
        {
            string javaDirectory = Path.Combine(root, "runtime 中文 with space", "bin"); Directory.CreateDirectory(javaDirectory);
            string java = Path.Combine(javaDirectory, OperatingSystem.IsWindows() ? "java.exe" : "java");
            File.WriteAllBytes(java, [0]);
            string[] arguments = ["-XX:+UseSerialGC", "-Xmx512m", "-Dfixture=value with spaces", "-cp", "app with space.jar" + Path.PathSeparator + "mod.jar",
                "dev.kostromdan.mods.crash_assistant.app.class_loading.Boot", "--args-file", "local/crash_assistant/123_456_args.info",
                "", "中文😀", "literal \"quote\"", "$literal;no-shell"];
            var command = JvmChildLaunch.CreateStartInfo(java, arguments);
            AssertTrue(command is not null);
            AssertEqual(java, command!.FileName);
            AssertFalse(command.UseShellExecute);
            AssertTrue(command.ArgumentList.SequenceEqual(arguments));
            AssertFalse(command.Environment.ContainsKey(JvmChildLaunch.JavaEnvironmentVariable));
            AssertTrue(JvmChildLaunch.CreateStartInfo(java, ["--class-path=app.jar", "fixture.Child", "--jvm-host"]) is not null);
            AssertTrue(JvmChildLaunch.CreateStartInfo(java, ["--add-opens", "java.base/java.lang=ALL-UNNAMED", "-cp", "app.jar", "fixture.Child"]) is not null);
            AssertTrue(JvmChildLaunch.CreateStartInfo(java, ["-jar", "app with space.jar", "argument"]) is not null);
            AssertTrue(JvmChildLaunch.CreateStartInfo(null, arguments) is null);
            AssertTrue(JvmChildLaunch.CreateStartInfo("java", arguments) is null);
            AssertTrue(JvmChildLaunch.CreateStartInfo(Path.Combine(root, "missing", "java"), arguments) is null);
            string host = Path.Combine(javaDirectory, "Nexa.Jvm.Host"); File.WriteAllBytes(host, [0]);
            AssertTrue(JvmChildLaunch.CreateStartInfo(host, arguments) is null);
            foreach (string[] invalid in new[]
            {
                Array.Empty<string>(), new[] { "--jvm-host" }, new[] { "--jvm-host", "-cp", "x", "fixture.Child" },
                new[] { "-cp" }, new[] { "-cp", "" , "fixture.Child" }, new[] { "-cp", "app.jar" },
                new[] { "--class-path=", "fixture.Child" }, new[] { "@parent-sensitive.args" },
                new[] { "-cp", "@parent-sensitive.args", "fixture.Child" }, new[] { "-jar", "@parent-sensitive.args" },
                new[] { "fixture.Child" }, new[] { "-cp", "app.jar", "invalid main class" },
                new[] { "-cp", "app.jar", "-m", "module" }, new[] { "-cp", "app.jar", "--module=module" },
                new[] { "-cp", "app.jar", "fixture.Child", "nul\0value" },
                Enumerable.Repeat("-Dvalue=x", 16385).ToArray(),
                new[] { "-cp", "app.jar", "fixture.Child", new string('x', 1024 * 1024 + 1) },
                new[] { "-cp", "app.jar", "fixture.Child", new string('x', 1024 * 1024), new string('x', 1024 * 1024),
                    new string('x', 1024 * 1024), new string('x', 1024 * 1024) },
            }) AssertTrue(JvmChildLaunch.CreateStartInfo(java, invalid) is null);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async ValueTask LauncherAuxiliaryJavaLeaseOutlivesManagedGameAndGuardsRemoval()
    {
        string external = CreateTempDirectory();
        try
        {
            string java = Path.Combine(external, "component", "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            Directory.CreateDirectory(Path.GetDirectoryName(java)!); File.WriteAllBytes(java, [0]);
            Directory.CreateDirectory(Path.Combine(external, JvmRuntimeUseRecord.UsesDirectory, "component"));
            AssertTrue(JvmChildRuntimeLease.AcquireIfEnrolled(java) is null);
            AssertEqual(0, Directory.GetFiles(external, "*.lease", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(external, recursive: true); }

        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var deletion = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        AssertTrue(JvmChildRuntimeLease.AcquireIfEnrolled(fixture.Executable) is null);
        var parent = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
        AssertTrue(parent is not null);
        using var child = await new LongLivedProcessPort().StartAsync(new());
        try
        {
            var auxiliary = JvmChildRuntimeLease.AcquireIfEnrolled(fixture.Executable);
            AssertTrue(auxiliary is not null);
            using (auxiliary) auxiliary!.Bind(child);
            parent!.Dispose(); parent = null; // Original managed game/preparation no longer owns the runtime.
            string uses = Path.Combine(fixture.RuntimeRoot, JvmRuntimeUseRecord.UsesDirectory, fixture.Plan.ComponentName);
            string path = Directory.GetFiles(uses).Single();
            byte[] record = await File.ReadAllBytesAsync(path);
            AssertTrue(JvmRuntimeUseRecord.TryRead(record, out int processId, out long ticks));
            AssertEqual(child.Id, processId); AssertEqual(child.StartTime.ToUniversalTime().Ticks, ticks);
            using (var input = File.OpenRead(path))
            {
                var existingReader = JavaRuntimeLeaseIdentity.Read(input);
                AssertEqual(child.Id, existingReader.ProcessId);
                AssertFalse(existingReader.HasDefinitelyExited());
            }
            AssertFalse((await fixture.Management.ManageAsync(deletion)).IsSuccess);
            AssertTrue(File.Exists(path)); AssertTrue(File.Exists(fixture.Executable));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertTrue((await fixture.Management.ManageAsync(deletion)).IsSuccess);
            AssertFalse(File.Exists(path)); AssertFalse(File.Exists(fixture.Executable));
        }
        finally
        {
            parent?.Dispose();
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async ValueTask LauncherAuxiliaryRetiredParentEnrollmentCannotLaunchWithoutLease()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        string uses = Path.Combine(fixture.RuntimeRoot, JvmRuntimeUseRecord.UsesDirectory, fixture.Plan.ComponentName);
        using (var parent = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable))
        {
            AssertTrue(parent is not null);
            AssertEqual(1, Directory.GetFiles(uses, "*.lease").Length);
        }
        // The parent exits before its spawned bridge first runs. Verified enrollment
        // directories remain, but the bridge must neither fall through unleased nor
        // infer new authority from that empty component directory.
        AssertTrue(Directory.Exists(Path.Combine(fixture.RuntimeRoot, JvmRuntimeUseRecord.JobsDirectory)));
        AssertTrue(File.Exists(Path.Combine(fixture.RuntimeRoot, JvmRuntimeUseRecord.RootLockName)));
        AssertTrue(Directory.Exists(uses)); AssertEqual(0, Directory.GetFiles(uses).Length);
        bool refused = false;
        try { using var auxiliary = JvmChildRuntimeLease.AcquireIfEnrolled(fixture.Executable); }
        catch (IOException) { refused = true; }
        AssertTrue(refused, "A retired parent enrollment must refuse an unprotected auxiliary Java launch.");
        AssertEqual(0, Directory.GetFiles(uses).Length);
        AssertTrue(File.Exists(fixture.Executable));
    }

    // Explicit native-fixture probe; it calls the same guard used by managed deletion/replacement.
    private static int RunLauncherJavaUseProbe(string runtimeRoot, string component)
    {
        if (!Path.IsPathFullyQualified(runtimeRoot) || component != "java-runtime-compat") return 2;
        string root = Path.GetFullPath(runtimeRoot);
        string relative = Path.GetRelativePath(Path.GetFullPath(Path.GetTempPath()), root);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.GetFileName(root) != "managed-runtime"
            || Path.GetFileName(Path.GetDirectoryName(root)!).StartsWith("nexa-mod-compat-", StringComparison.Ordinal) != true)
            return 2;
        try { JavaRuntimeManagedStore.CheckUnused(root, component); return 0; }
        catch (IOException) { return 23; }
    }

    private static async ValueTask LauncherAuxiliaryLeaseClockCalibrationCannotAuthorizeLiveRuntimeRemoval()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var deletion = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        var parent = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
        using var child = await new LongLivedProcessPort().StartAsync(new());
        try
        {
            using (var auxiliary = JvmChildRuntimeLease.AcquireIfEnrolled(fixture.Executable))
            {
                AssertTrue(auxiliary is not null); auxiliary!.Bind(child);
            }
            parent!.Dispose(); parent = null;
            string uses = Path.Combine(fixture.RuntimeRoot, JvmRuntimeUseRecord.UsesDirectory, fixture.Plan.ComponentName);
            string path = Directory.GetFiles(uses).Single();
            byte[] original = await File.ReadAllBytesAsync(path);
            long nativeBirth = BinaryPrimitives.ReadInt64LittleEndian(original.AsSpan(12));
            // Different CLR processes can calibrate the same Linux birth by a few ms.
            // This previously authorized deletion while the exact child PID remained alive.
            foreach (long delta in new[] { 10 * TimeSpan.TicksPerMillisecond, TimeSpan.TicksPerSecond })
            {
                byte[] calibrated = (byte[])original.Clone();
                BinaryPrimitives.WriteInt64LittleEndian(calibrated.AsSpan(12), nativeBirth - delta);
                await File.WriteAllBytesAsync(path, calibrated);
                AssertFalse((await fixture.Management.ManageAsync(deletion)).IsSuccess);
                AssertFalse(child.HasExited); AssertTrue(File.Exists(fixture.Executable));
                byte[] retained = await File.ReadAllBytesAsync(path);
                AssertTrue(calibrated.SequenceEqual(retained));
            }
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertTrue((await fixture.Management.ManageAsync(deletion)).IsSuccess);
        }
        finally
        {
            parent?.Dispose();
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void LauncherAuxiliaryLinuxExitStateUsesKernelDelimiterAndRejectsMalformedEvidence()
    {
        AssertTrue(Nexa.Platform.PlatformProcessIdentity.TryReadLinuxKernelState("42 (java) Z 1 0 0"u8, 42, out char zombie));
        AssertEqual('Z', zombie);
        AssertTrue(Nexa.Platform.PlatformProcessIdentity.TryReadLinuxKernelState("42 (contains ) Z 1\ntrick) R 1 0 0"u8, 42, out char running));
        AssertEqual('R', running);
        foreach (byte[] malformed in new[]
        {
            Array.Empty<byte>(), "43 (java) Z 1 0"u8.ToArray(), "42 (java) ? 1 0"u8.ToArray(),
            "42 (java) Z -1 0"u8.ToArray(), "42 (java) Z parent 0"u8.ToArray(), "42 java) Z 1 0"u8.ToArray(),
            "42 (java) Z 1"u8.ToArray(), "42 (java)Z 1 0"u8.ToArray(),
        }) AssertFalse(Nexa.Platform.PlatformProcessIdentity.TryReadLinuxKernelState(malformed, 42, out _));
    }
}
