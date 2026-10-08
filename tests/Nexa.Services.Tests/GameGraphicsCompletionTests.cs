using System.Text.Json.Nodes;
using Nexa.Platform;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask GameGraphicsUsesObservedDependenciesAndReachesOwnedBootstrapEnvironment()
    {
        var native = new PlatformGameGraphicsRuntime();
        AssertTrue(native.Prepare("auto", "auto").Succeeded);
        AssertEqual(0, native.Prepare("auto", "auto").Environment.Count);
        AssertFalse(native.Prepare("arbitrary", "auto").Succeeded);
        if (!OperatingSystem.IsLinux())
        {
            AssertEqual("platform_unsupported", native.Prepare("secondary", "auto").Code);
            AssertFalse(native.Describe().SecondaryAvailable);
            return;
        }
        string root = CreateTempDirectory();
        try
        {
            string drm = Path.Combine(root, "drm"); Directory.CreateDirectory(drm);
            string dri = Path.Combine(root, "swrast_dri.so"), glx = Path.Combine(root, "libGLX_mesa.so.0");
            byte[] elf = new byte[64]; elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
            elf[4] = Environment.Is64BitProcess ? (byte)2 : (byte)1; elf[5] = 1; elf[6] = 1;
            ushort machine = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => 62,
                System.Runtime.InteropServices.Architecture.X86 => 3,
                System.Runtime.InteropServices.Architecture.Arm64 => 183,
                System.Runtime.InteropServices.Architecture.Arm => 40,
                _ => 0
            };
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), machine);
            File.WriteAllBytes(dri, elf); File.WriteAllBytes(glx, elf);
            void Device(string card, string pci, string primary, string driver)
            {
                string physical = Path.Combine(root, "devices", pci); Directory.CreateDirectory(physical);
                File.WriteAllText(Path.Combine(physical, "boot_vga"), primary);
                string module = Path.Combine(root, "modules", driver); Directory.CreateDirectory(module);
                Directory.CreateDirectory(Path.Combine(physical, "driver"));
                Directory.CreateSymbolicLink(Path.Combine(physical, "driver", "module"), module);
                Directory.CreateDirectory(Path.Combine(drm, card));
                Directory.CreateSymbolicLink(Path.Combine(drm, card, "device"), physical);
            }
            Device("card0", "0000:00:02.0", "1", "i915");
            Device("card1", "0000:01:00.0", "0", "amdgpu");
            Device("card2", "0000:02:ff.9", "0", "amdgpu"); // An invalid PCI slot/function cannot become a selector.
            var provider = new PlatformGameGraphicsRuntime(drm, [dri], [glx]);
            var snapshot = provider.Describe();
            AssertTrue(snapshot.SecondaryAvailable && snapshot.MesaSoftwareAvailable);
            AssertEqual("0000:01:00.0", snapshot.SecondaryPciDevice);
            var prepared = provider.Prepare("secondary", "mesa-software");
            AssertTrue(prepared.Succeeded);
            AssertEqual("pci-0000_01_00_0", prepared.Environment["DRI_PRIME"]);
            AssertEqual("1", prepared.Environment["LIBGL_ALWAYS_SOFTWARE"]);
            var policy = new MinecraftLaunchGraphicsPolicy(provider);
            var plan = new MinecraftLaunchPlan("java", root, ["fixture.Graphics"], [], [],
                new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "fixture.Graphics", []))
            {
                MainClassIndex = 0,
                GpuPreference = "secondary",
                RendererPreference = "mesa-software",
                EnvironmentVariables = new Dictionary<string, string> { ["NEXACL_GRAPHICS_FIXTURE"] = "preserved" },
            };
            var applied = policy.Apply(plan, plan.GpuPreference, plan.RendererPreference);
            AssertTrue(applied.IsSuccess);
            AssertEqual(1, plan.EnvironmentVariables.Count);
            AssertEqual("preserved", applied.Value!.EnvironmentVariables["NEXACL_GRAPHICS_FIXTURE"]);
            AssertEqual("pci-0000_01_00_0", applied.Value.ToStartInfo().Environment["DRI_PRIME"]);
            AssertFalse(policy.Apply(plan with { EnvironmentVariables = new Dictionary<string, string> { ["DRI_PRIME"] = "conflicting" } },
                "secondary", "auto").IsSuccess);
            var safe = MinecraftSafeLaunchPolicy.Apply(new MinecraftLaunchRequest
            {
                VersionJson = new JsonObject(),
                VersionId = "fixture",
                InstanceDirectory = root,
                MinecraftRootDirectory = root,
                PlayerName = "fixture",
                PlayerUuid = "fixture",
                Overlay = new(SafeLaunch: true),
                GpuPreference = "secondary",
                RendererPreference = "mesa-software",
                EnvironmentVariables = prepared.Environment,
            });
            AssertEqual("auto", safe.GpuPreference); AssertEqual("auto", safe.RendererPreference);
            AssertEqual(0, safe.EnvironmentVariables.Count);

            string executable = Path.Combine(AppContext.BaseDirectory, "Nexa.Services.Tests");
            await using var processes = new MinecraftProcessService(jvmHostExecutable: executable);
            var executor = new MinecraftLaunchExecutor(new JvmHostService(processes)) { Graphics = policy };
            var session = await executor.ExecuteAsync(plan, "graphics-fixture");
            AssertEqual(0, await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));

            File.Delete(glx);
            AssertFalse(provider.Prepare("auto", "mesa-software").Succeeded);
            File.WriteAllBytes(glx, [1]);
            AssertFalse(provider.Prepare("auto", "mesa-software").Succeeded);
            File.WriteAllText(Path.Combine(root, "devices", "0000:01:00.0", "boot_vga"), "1");
            AssertFalse(provider.Prepare("secondary", "auto").Succeeded); // Two primaries are ambiguous.
            AssertTrue(provider.Prepare("auto", "auto").Succeeded);
        }
        finally { await WaitForLaunchGameDirectoryReleasedAsync(root); Directory.Delete(root, recursive: true); }
    }
}
