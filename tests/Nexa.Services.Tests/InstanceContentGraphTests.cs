using System.IO.Compression;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static LaunchModIdentity GraphMod(string id, bool enabled = true, params string[] dependencies) =>
        new(id, "1", "fabric.mod.json", enabled, dependencies.ToDictionary(name => name, _ => ">=1", StringComparer.Ordinal), true);

    private static void ContentGraphDistinguishesEvidenceAndAliases()
    {
        var baseMod = GraphMod("base") with { ProvidedIds = new Dictionary<string, string> { ["alias"] = "1" } };
        var child = GraphMod("child", true, "alias", "off", "missing", "java", "fabricloader", "nested", "duplicate");
        var graph = InstanceContentGraphBuilder.Build(new([
            baseMod, child, GraphMod("off", false), GraphMod("nested") with { NestedCandidate = true },
            GraphMod("duplicate"), GraphMod("duplicate") with { Version = "2" }
        ], 0, true), [new(InstallLoader.Fabric, "0.16")]);
        var node = graph.Nodes.Single(item => item.Id == "child");
        InstanceContentDependency Edge(string id) => node.Dependencies.Single(edge => edge.Id == id);
        AssertEqual(InstanceDependencyState.Present, Edge("alias").State);
        AssertEqual("base", graph.Nodes[Edge("alias").Providers.Single()].Id);
        var aliasConsumer = graph.Nodes[Edge("alias").Providers.Single()].Consumers.Single();
        AssertEqual("child", graph.Nodes[aliasConsumer.Node].Id);
        AssertEqual("alias", graph.Nodes[aliasConsumer.Node].Dependencies[aliasConsumer.Dependency].Id);
        AssertEqual(InstanceDependencyState.Disabled, Edge("off").State);
        AssertEqual(InstanceDependencyState.Missing, Edge("missing").State);
        AssertEqual(InstanceDependencyState.Runtime, Edge("java").State);
        AssertEqual(InstanceDependencyState.Runtime, Edge("fabricloader").State);
        AssertEqual(InstanceDependencyState.NestedCandidate, Edge("nested").State);
        AssertEqual(InstanceDependencyState.Ambiguous, Edge("duplicate").State);
        AssertEqual(2, Edge("duplicate").Providers.Count);
        AssertEqual(">=1", Edge("alias").Requirement);
        var unknown = InstanceContentGraphBuilder.Build(new([child], 1, false), []);
        AssertFalse(unknown.Complete);
        AssertEqual(InstanceDependencyState.Unknown, unknown.Nodes.Single().Dependencies.Single(edge => edge.Id == "missing").State);
        AssertTrue(unknown.Notice is not null);
        var incomplete = InstanceContentGraphBuilder.Build(new([
            GraphMod("partial") with { DependenciesComplete = false }, GraphMod("owner", true, "undeclared-alias")
        ], 0, true), []);
        AssertFalse(incomplete.Complete);
        AssertFalse(incomplete.Nodes.Single(item => item.Id == "partial").DependenciesComplete);
        AssertEqual(InstanceDependencyState.Unknown, incomplete.Nodes.Single(item => item.Id == "owner").Dependencies.Single().State);
    }

    private static void ContentGraphFindsActualCyclesWithoutMarkingTails()
    {
        var graph = InstanceContentGraphBuilder.Build(new([
            GraphMod("a", true, "b"), GraphMod("b", true, "a"), GraphMod("tail", true, "a"),
            GraphMod("self", true, "self"), GraphMod("off-a", false, "off-b"), GraphMod("off-b", true, "off-a"),
            GraphMod("nested-a", true, "nested-b"), GraphMod("nested-b", true, "nested-a") with { NestedCandidate = true }
        ], 0, true), []);
        AssertTrue(graph.Nodes.Where(node => node.Id is "a" or "b" or "self").All(node => node.InCycle));
        AssertFalse(graph.Nodes.Where(node => node.Id is not ("a" or "b" or "self")).Any(node => node.InCycle));
        // Deep chains must not recurse on the native stack or misclassify all predecessors as a cycle.
        var chain = Enumerable.Range(0, 4096).Select(index => GraphMod("m" + index, true,
            index == 4095 ? [] : ["m" + (index + 1)])).ToArray();
        graph = InstanceContentGraphBuilder.Build(new(chain, 0, true), []);
        AssertEqual(4096, graph.Nodes.Count); AssertFalse(graph.Nodes.Any(node => node.InCycle));
    }

    private static void ContentGraphBudgetsNeverConvertTruncationIntoMissing()
    {
        var graph = InstanceContentGraphBuilder.Build(new(
            Enumerable.Range(0, 4097).Select(index => GraphMod("m" + index, true, "missing")).ToArray(), 0, true), []);
        AssertEqual(4096, graph.Nodes.Count); AssertFalse(graph.Complete);
        AssertFalse(graph.Nodes.SelectMany(node => node.Dependencies).Any(edge => edge.State == InstanceDependencyState.Missing));
        var dependencies = Enumerable.Range(0, InstanceContentGraphBuilder.MaximumEdges + 1)
            .ToDictionary(index => "d" + index, _ => "*", StringComparer.Ordinal);
        graph = InstanceContentGraphBuilder.Build(new([GraphMod("owner") with { Dependencies = dependencies }], 0, true), []);
        AssertEqual(InstanceContentGraphBuilder.MaximumEdges, graph.Nodes[0].Dependencies.Count);
        AssertFalse(graph.Nodes[0].DependenciesComplete); AssertFalse(graph.Complete);
        AssertFalse(graph.Nodes[0].Dependencies.Any(edge => edge.State == InstanceDependencyState.Missing));
        graph = InstanceContentGraphBuilder.Build(new([
            GraphMod("owner", true, "alias"), .. Enumerable.Range(0, 17).Select(index => GraphMod("provider" + index)
                with { ProvidedIds = new Dictionary<string, string> { ["alias"] = "1" } })
        ], 0, true), []);
        var edge = graph.Nodes.Single(node => node.Id == "owner").Dependencies.Single();
        AssertEqual(16, edge.Providers.Count); AssertEqual(InstanceDependencyState.Unknown, edge.State);
        AssertFalse(graph.Complete);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { InstanceContentGraphBuilder.Build(new([], 0, true), [], stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
    }

    private static async ValueTask ContentGraphUsesOnlyTheSelectedInstanceAndRequestedProjection()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = CreateVersionDirectory(root, "graph", new JsonObject
            {
                ["id"] = "graph",
                ["_minecraftVersion"] = "1.20.1",
                ["mainClass"] = "example.Main",
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.0" })
            });
            void WriteJar(string game, string id, string dependency)
            {
                Directory.CreateDirectory(Path.Combine(game, "mods"));
                using var archive = ZipFile.Open(Path.Combine(game, "mods", id + ".jar"), ZipArchiveMode.Create);
                using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open());
                writer.Write(new JsonObject
                {
                    ["id"] = id,
                    ["version"] = "1",
                    ["depends"] = new JsonObject { [dependency] = "*" }
                }.ToJsonString());
            }
            WriteJar(instance, "isolated", "minecraft"); WriteJar(root, "shared", "uninspected-alias");
            var normal = await InstanceManagementService.ReadAsync(new(instance));
            AssertTrue(normal.ContentGraph is null); AssertTrue(normal.Pages.Any(page => page.Id == "contentgraph"));
            var isolated = await InstanceManagementService.ReadAsync(new(instance) { IncludeContentGraph = true });
            AssertEqual("isolated", isolated.ContentGraph!.Nodes.Single().Id);
            await new Nexa.Services.Minecraft.MinecraftInstanceMetadataStore().SaveAsync(instance,
                new Nexa.Services.Minecraft.MinecraftInstanceMetadata { InstanceIsolation = false });
            var shared = await InstanceManagementService.ReadAsync(new(instance) { IncludeContentGraph = true });
            AssertEqual("shared", shared.ContentGraph!.Nodes.Single().Id);
            AssertEqual(InstanceDependencyState.Missing, shared.ContentGraph.Nodes.Single().Dependencies.Single().State);
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "legacy.litemod"), "uninspected");
            shared = await InstanceManagementService.ReadAsync(new(instance) { IncludeContentGraph = true });
            AssertFalse(shared.ContentGraph!.Complete); AssertEqual(1, shared.ContentGraph.UnknownFiles);
            AssertEqual(InstanceDependencyState.Unknown, shared.ContentGraph.Nodes.Single().Dependencies.Single().State);
        }
        finally { Directory.Delete(root, true); }
    }
}
