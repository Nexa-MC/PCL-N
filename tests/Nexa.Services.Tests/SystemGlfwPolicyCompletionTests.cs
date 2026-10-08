using System.IO.Compression;
using System.Text.Json.Nodes;
using Nexa.Services.Accounts;
using Nexa.Services.Composition;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask SystemGlfwPoliciesReachExecutorNativeFilteringAndSafeLaunch()
    {
        string root = CreateTempDirectory();
        try
        {
            var cases = new[]
            {
                (Name: "builtin", Metadata: true, Global: (string?)null, Instance: (string?)null, Safe: false, Filtered: true),
                (Name: "global-enable", Metadata: false, Global: "true", Instance: (string?)null, Safe: false, Filtered: true),
                (Name: "global-disable", Metadata: true, Global: "false", Instance: (string?)null, Safe: false, Filtered: false),
                (Name: "instance-disable", Metadata: false, Global: "true", Instance: "false", Safe: false, Filtered: false),
                (Name: "safe-launch", Metadata: true, Global: "true", Instance: (string?)null, Safe: true, Filtered: false),
            };
            foreach (var item in cases)
            {
                string game = Path.Combine(root, item.Name); Directory.CreateDirectory(game);
                const string nativeRelative = "org/lwjgl/lwjgl-glfw/3.3.3/lwjgl-glfw-3.3.3-natives-linux.jar";
                string native = Path.Combine(game, "libraries", nativeRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(native)!);
                using (var archive = ZipFile.Open(native, ZipArchiveMode.Create))
                { using var output = archive.CreateEntry("libglfw-policy-fixture.so").Open(); output.Write("fixture-native"u8); }
                string instance = CreateVersionDirectory(game, "1.20.1", new JsonObject
                {
                    ["id"] = "1.20.1",
                    ["type"] = "release",
                    ["mainClass"] = "fixture.Glfw",
                    ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
                    ["libraries"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "org.lwjgl:lwjgl-glfw:3.3.3",
                        ["natives"] = new JsonObject { ["linux"] = "natives-linux" },
                        ["downloads"] = new JsonObject
                        {
                            ["classifiers"] = new JsonObject
                            { ["natives-linux"] = new JsonObject { ["path"] = nativeRelative } }
                        },
                    }),
                });
                File.WriteAllBytes(Path.Combine(instance, "1.20.1.jar"), [0xca, 0xfe]);
                var metadata = new MinecraftInstanceMetadataStore();
                await metadata.SaveAsync(instance, new MinecraftInstanceMetadata { UseSystemGlfw = item.Metadata });
                string javaHome = Path.Combine(game, "java"), executable = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllBytes(executable, [0]);
                using var host = FoundationComposer.Compose(new InMemorySettingsPort(), LauncherDefaults.CreateSchema(),
                    new LaunchProfileFilePort(Path.Combine(game, "profiles.json")));
                AssertTrue(host.Accounts.AddProfile(new LaunchProfile { Username = "Fixture", Kind = LaunchProfileKind.Offline }).IsSuccess);
                if (item.Global is { } global)
                    AssertTrue(host.SettingsPolicy.Set(new("game.system-glfw", SettingsLayer.Global, new(SettingsOverrideMode.Custom, global))).IsSuccess);
                if (item.Instance is { } local)
                    AssertTrue(host.SettingsPolicy.Set(new("game.system-glfw", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, local), instance)).IsSuccess);
                if (item.Safe)
                    AssertTrue(host.SettingsPolicy.Set(new("game.safe-launch", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "true"), instance)).IsSuccess);
                var candidate = new JavaRuntimeCandidate(new JavaInstallation(javaHome, executable, null, new Version(17, 0, 10),
                    JavaBrand.EclipseTemurin, JavaArchitecture.X64, is64Bit: true, isJre: false));
                var installer = new NeverJavaInstaller(); var port = new LongLivedProcessPort();
                await using var processes = new MinecraftProcessService(port, host.StateStore);
                var diagnostics = new MinecraftLaunchPlanDiagnostics();
                var executor = new MinecraftLaunchExecutor(new HookFixtureHost(processes, [])) { Diagnostics = diagnostics };
                var coordinator = new MinecraftLaunchCoordinator(game, Path.Combine(game, "runtime"),
                    new MinecraftInstanceDiscovery(metadataStore: metadata), host.Accounts, host.Settings,
                    new JavaSelectionService(new InMemoryJavaLocator([candidate])), installer, executor,
                    new MinecraftLaunchPlatform(MinecraftLibraryOperatingSystem.Linux, "6.12", true, false),
                    windowProbe: new ImmediateWindowProbe(), settingsPolicy: host.SettingsPolicy);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var launched = await coordinator.StartAsync("1.20.1", 0, cancellationToken: stop.Token);
                AssertTrue(launched.IsSuccess, item.Name + ": " + launched.Error?.Message);
                var captured = diagnostics.Read(new(instance));
                AssertTrue(captured.CapturedAt is not null); AssertEqual(item.Safe, captured.SafeLaunch);
                AssertEqual(item.Filtered ? 0 : 1, captured.NativeArchives.Count);
                if (!item.Filtered) AssertEqual(native, captured.NativeArchives[0]);
                AssertEqual(!item.Filtered, File.Exists(Path.Combine(instance, "natives", "libglfw-policy-fixture.so")));
                AssertEqual(0, installer.Calls);
                AssertTrue(processes.TryCancel(processes.ListSessions().Single().SessionId));
                await port.LastProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await executor.WaitForFinalizationAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
