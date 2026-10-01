using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Linear bounded metadata graph. It makes no version-range or loaded-set claims.</summary>
internal static class InstanceContentGraphBuilder
{
    internal const int MaximumNodes = 4096, MaximumEdges = 16384, MaximumProviders = 16;

    internal static InstanceContentGraph Build(LaunchModInventory inventory, IReadOnlyList<InstallBuildSelection> components,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var mods = inventory.Mods.Take(MaximumNodes).OrderBy(mod => mod.Id, StringComparer.Ordinal)
            .ThenBy(mod => mod.Version, StringComparer.Ordinal).ThenBy(mod => mod.ContentSha256, StringComparer.Ordinal).ToArray();
        bool bounded = inventory.Mods.Count <= MaximumNodes;
        var providers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var saturated = new HashSet<string>(StringComparer.Ordinal);
        int aliases = 0;
        for (int index = 0; index < mods.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            Add(mods[index].Id, index);
            foreach (string id in mods[index].ProvidedIds.Keys)
            {
                if (++aliases > MaximumEdges) { bounded = false; break; }
                Add(id, index);
            }
        }
        void Add(string id, int index)
        {
            if (!providers.TryGetValue(id, out var list)) providers[id] = list = [];
            if (list.Count > 0 && list[^1] == index) return;
            if (list.Count == MaximumProviders) { saturated.Add(id); bounded = false; return; }
            list.Add(index);
        }
        var runtime = new HashSet<string>(StringComparer.Ordinal) { "minecraft", "java" };
        foreach (var component in components)
        {
            switch (component.Loader)
            {
                case InstallLoader.Fabric or InstallLoader.LegacyFabric: runtime.Add("fabricloader"); break;
                case InstallLoader.Quilt: runtime.Add("quilt_loader"); break;
                case InstallLoader.Forge: runtime.Add("forge"); runtime.Add("fml"); break;
                case InstallLoader.NeoForge: runtime.Add("neoforge"); break;
                case InstallLoader.Cleanroom: runtime.Add("cleanroom"); break;
                case InstallLoader.LiteLoader: runtime.Add("liteloader"); break;
            }
        }
        bool identityComplete = inventory.Complete && bounded && inventory.UnknownFiles == 0
            && mods.All(mod => mod.DependenciesComplete);
        var nodes = new InstanceContentNode[mods.Length];
        var links = Enumerable.Range(0, mods.Length).Select(_ => new List<int>()).ToArray();
        int edges = 0;
        for (int index = 0; index < mods.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var mod = mods[index];
            List<InstanceContentDependency> dependencies = [];
            int available = MaximumEdges - edges;
            if (mod.Dependencies.Count > available) bounded = false;
            foreach (var declaration in mod.Dependencies.Take(available).OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                edges++;
                int[] matches = providers.TryGetValue(declaration.Key, out var list) ? list.ToArray() : [];
                InstanceDependencyState state;
                if (saturated.Contains(declaration.Key)) state = InstanceDependencyState.Unknown;
                else if (matches.Length > 1) state = InstanceDependencyState.Ambiguous;
                else if (matches.Length == 1)
                {
                    var provider = mods[matches[0]];
                    state = !provider.Enabled ? InstanceDependencyState.Disabled : provider.NestedCandidate
                        ? InstanceDependencyState.NestedCandidate : InstanceDependencyState.Present;
                }
                else state = runtime.Contains(declaration.Key) ? InstanceDependencyState.Runtime
                    : identityComplete ? InstanceDependencyState.Missing : InstanceDependencyState.Unknown;
                dependencies.Add(new(declaration.Key, declaration.Value, state, Array.AsReadOnly(matches)));
                if (state == InstanceDependencyState.Present && mod.Enabled && !mod.NestedCandidate)
                    links[index].Add(matches[0]);
            }
            nodes[index] = new(index, mod.Id, mod.Version, mod.Enabled, mod.NestedCandidate,
                mod.DependenciesComplete && dependencies.Count == mod.Dependencies.Count, false, dependencies.AsReadOnly());
        }
        // Truncating any identity/alias/edge evidence must never produce a false missing verdict.
        if (!bounded)
            for (int index = 0; index < nodes.Length; index++)
                nodes[index] = nodes[index] with
                {
                    Dependencies = Array.AsReadOnly(nodes[index].Dependencies.Select(edge =>
                        edge.State == InstanceDependencyState.Missing ? edge with { State = InstanceDependencyState.Unknown } : edge).ToArray())
                };
        var cycles = FindCycles(links, token);
        var consumers = nodes.Select(_ => new List<InstanceContentConsumer>()).ToArray();
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            for (int dependency = 0; dependency < node.Dependencies.Count; dependency++)
                foreach (int provider in node.Dependencies[dependency].Providers)
                    consumers[provider].Add(new(node.Key, dependency));
        }
        for (int index = 0; index < nodes.Length; index++)
            nodes[index] = nodes[index] with { InCycle = cycles[index], Consumers = consumers[index].AsReadOnly() };
        bool complete = inventory.Complete && bounded && inventory.UnknownFiles == 0 && nodes.All(node => node.DependenciesComplete);
        return new(Array.AsReadOnly(nodes), inventory.UnknownFiles, complete,
            !bounded ? "依赖图超过读取上限，当前仅显示部分关系。"
            : !complete ? "部分模组或依赖尚未识别，未知关系不能当作缺失。"
            : null);
    }

    private static bool[] FindCycles(List<int>[] links, CancellationToken token)
    {
        var reverse = Enumerable.Range(0, links.Length).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < links.Length; index++)
            foreach (int target in links[index]) reverse[target].Add(index);
        var seen = new bool[links.Length];
        List<int> order = [];
        Stack<(int Node, int Next)> pending = new();
        for (int root = 0; root < links.Length; root++)
        {
            if (seen[root]) continue;
            seen[root] = true; pending.Push((root, 0));
            while (pending.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                if (frame.Next == links[frame.Node].Count) { order.Add(frame.Node); continue; }
                pending.Push((frame.Node, frame.Next + 1));
                int target = links[frame.Node][frame.Next];
                if (!seen[target]) { seen[target] = true; pending.Push((target, 0)); }
            }
        }
        Array.Clear(seen);
        var cycles = new bool[links.Length];
        Stack<int> stack = new();
        for (int index = order.Count - 1; index >= 0; index--)
        {
            int root = order[index];
            if (seen[root]) continue;
            List<int> component = [];
            seen[root] = true; stack.Push(root);
            while (stack.TryPop(out int node))
            {
                token.ThrowIfCancellationRequested();
                component.Add(node);
                foreach (int target in reverse[node])
                    if (!seen[target]) { seen[target] = true; stack.Push(target); }
            }
            if (component.Count > 1 || links[root].Contains(root))
                foreach (int node in component) cycles[node] = true;
        }
        return cycles;
    }
}
