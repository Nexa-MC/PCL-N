using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Launch-plan adapter over the portable, bounded JVM wire codec.</summary>
public static class JvmHostBootstrap
{
    public static ValueTask WriteAsync(Stream destination, MinecraftLaunchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.MainClassIndex is not { } boundary || boundary < 0 || boundary >= plan.Arguments.Count)
            throw new ArgumentException("An explicit main-class boundary is required.", nameof(plan));
        return JvmHostBootstrapCodec.WriteAsync(destination, new(plan.JavaExecutablePath, plan.WorkingDirectory,
            plan.Arguments[boundary], plan.Arguments.Take(boundary).ToArray(), plan.Arguments.Skip(boundary + 1).ToArray()), cancellationToken);
    }

    public static ValueTask<JvmHostBootstrapRequest> ReadAsync(Stream source, CancellationToken cancellationToken = default)
        => JvmHostBootstrapCodec.ReadAsync(source, cancellationToken);
}
