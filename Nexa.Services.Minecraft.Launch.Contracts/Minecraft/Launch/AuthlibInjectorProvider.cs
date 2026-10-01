




namespace Nexa.Services.Minecraft.Launch;

public interface IAuthlibInjectorProvider
{
    Task<string> EnsureAsync(string minecraftRoot, CancellationToken cancellationToken = default);
}
