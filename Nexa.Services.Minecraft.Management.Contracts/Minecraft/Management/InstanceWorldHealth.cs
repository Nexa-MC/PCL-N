using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceWorldHealthQuery(string InstanceDirectory, string WorldName);
public sealed record InstanceWorldHealth(bool Complete, int Regions, int Chunks, int DetectedIssues, IReadOnlyList<string> Issues)
{
    public bool Healthy => Complete && DetectedIssues == 0;
}
public static class InstanceWorldHealthContract
{
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.instance.world.health.read");
}
