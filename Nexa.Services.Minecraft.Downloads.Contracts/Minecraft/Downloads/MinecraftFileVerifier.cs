


namespace Nexa.Services.Minecraft.Downloads;

/// <summary>What a file on disk must satisfy to count as present. Null facts are unchecked.</summary>
public sealed record MinecraftExpectedFile(string Path, long? Size, string? Sha1);
