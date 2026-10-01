
using System.Runtime.InteropServices;



using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Libraries;





namespace Nexa.Services.Minecraft.Launch;

/// <summary>The concrete platform facts required by Mojang rules and native selection.</summary>
public readonly record struct MinecraftLaunchPlatform(
    MinecraftLibraryOperatingSystem OperatingSystem,
    string OperatingSystemVersion,
    bool Is64BitArchitecture,
    bool IsArm64Architecture)
{
    public static MinecraftLaunchPlatform Detect()
    {
        MinecraftLibraryOperatingSystem operatingSystem = System.OperatingSystem.IsWindows()
            ? MinecraftLibraryOperatingSystem.Win32
            : System.OperatingSystem.IsMacOS()
                ? MinecraftLibraryOperatingSystem.MacOs
                : System.OperatingSystem.IsLinux()
                    ? MinecraftLibraryOperatingSystem.Linux
                    : throw new PlatformNotSupportedException(
                        "Minecraft launch is not supported on this operating system.");
        Architecture architecture = RuntimeInformation.OSArchitecture;
        return new MinecraftLaunchPlatform(
            operatingSystem,
            Environment.OSVersion.Version.ToString(),
            architecture is Architecture.X64 or Architecture.Arm64,
            architecture == Architecture.Arm64);
    }
}

/// <summary>Prepared product launch input and the Java contract which selected its runtime.</summary>
public sealed record MinecraftLaunchPreparation(
    MinecraftInstanceDescriptor Instance,
    MinecraftLaunchRequest Request,
    JavaRequirementResolution JavaRequirement);
