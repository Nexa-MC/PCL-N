

namespace Nexa.Services.Minecraft;

/// <summary>Typed view of the game's options.txt. Absent keys keep Minecraft's own defaults.</summary>
public sealed record MinecraftOptionsSnapshot
{
    public static readonly MinecraftOptionsSnapshot Missing = new() { Readable = false };

    public bool Readable { get; init; }
    public int RenderDistance { get; init; } = 12;
    public int SimulationDistance { get; init; } = 12;
    public int MipmapLevels { get; init; } = 4;
    public string GraphicsMode { get; init; } = "fancy";
    public bool Fullscreen { get; init; }
    public string Particles { get; init; } = "all";
    public double EntityDistance { get; init; } = 1.0;
    public bool EntityShadows { get; init; } = true;
    public string ResourcePacks { get; init; } = "";
}
