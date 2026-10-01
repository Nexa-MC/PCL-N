namespace Nexa.Services.Minecraft.Management;

public enum InstanceDependencyState { Present, Disabled, Missing, Unknown, Ambiguous, Runtime, NestedCandidate }

public sealed record InstanceContentDependency(string Id, string Requirement, InstanceDependencyState State, IReadOnlyList<int> Providers);
public sealed record InstanceContentConsumer(int Node, int Dependency);

/// <summary>A metadata identity, not proof that the Mod was loaded by Minecraft.</summary>
public sealed record InstanceContentNode(int Key, string Id, string Version, bool Enabled, bool NestedCandidate,
    bool DependenciesComplete, bool InCycle, IReadOnlyList<InstanceContentDependency> Dependencies)
{
    public IReadOnlyList<InstanceContentConsumer> Consumers { get; init; } = [];
}

public sealed record InstanceContentGraph(IReadOnlyList<InstanceContentNode> Nodes, int UnknownFiles, bool Complete, string? Notice);
