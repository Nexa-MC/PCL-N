using System.Text.Json.Nodes;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void LaunchDiagnosticsCaptureRedactsAndBoundsWithoutMutatingPlan()
    {
        string root = CreateTempDirectory();
        try
        {
            var diagnostics = new MinecraftLaunchPlanDiagnostics();
            string[] arguments = ["-DapiKey=private-api", "-Dauthlibinjector.yggdrasil.prefetched=private-prefetch", "fixture.Main",
                "--accessToken", "private-access", "--username", "private-name", "--uuid=private-uuid", "--width", "1280"];
            var plan = HookPlan(root) with
            {
                Arguments = arguments,
                MainClassIndex = 2,
                EnvironmentVariables = new Dictionary<string, string> { ["SAFE_NAME"] = "private-environment" },
                PostExitCommand = "private-hook"
            };
            AssertTrue(diagnostics.Read(new(root)).CapturedAt is null);
            diagnostics.Capture(plan); arguments[0] = "changed-source";
            var captured = diagnostics.Read(new(root));
            string all = string.Join('\n', captured.Arguments);
            foreach (string secret in new[] { "private-api", "private-prefetch", "private-access", "private-name", "private-uuid", "private-environment", "private-hook", "changed-source" })
                AssertFalse(all.Contains(secret, StringComparison.Ordinal));
            AssertTrue(all.Contains("1280", StringComparison.Ordinal)); AssertEqual("fixture.Main", captured.MainClass);
            AssertEqual("private-access", plan.Arguments[4]); AssertEqual("private-environment", plan.EnvironmentVariables["SAFE_NAME"]);
            AssertTrue(captured.EnvironmentNames.SequenceEqual(["SAFE_NAME"]));
            diagnostics.Capture(plan with { ClasspathEntries = Enumerable.Range(0, 3000).Select(index => new string('x', 4096) + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray() });
            var bounded = diagnostics.Read(new(root)); AssertTrue(bounded.Truncated);
            AssertTrue(bounded.ClasspathEntries.Count <= 2048);
            AssertTrue(bounded.Arguments.Sum(value => value.Length) + bounded.ClasspathEntries.Sum(value => value.Length) <= 128 * 1024);
            for (int index = 0; index < 17; index++) diagnostics.Capture(plan with { InstanceDirectory = Path.Combine(root, "instance-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
            AssertTrue(diagnostics.Read(new(root)).CapturedAt is null);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask LocalLaunchDocumentsReadBoundedInheritanceRedactAndRetainDamage()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = CreateVersionDirectory(root, "diagnostic", new JsonObject { ["id"] = "diagnostic", ["inheritsFrom"] = "base", ["token"] = "manifest-secret" });
            string parent = CreateVersionDirectory(root, "base", new JsonObject { ["id"] = "base", ["mainClass"] = "fixture.Main" });
            string metadata = new MinecraftInstanceMetadataStore().GetMetadataPath(instance); Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
            File.WriteAllText(metadata, """{"schemaVersion":1,"accessToken":"metadata-secret","displayName":"Local"}""");
            string before = File.ReadAllText(metadata);
            var documents = await InstanceLocalDocumentsService.ReadAsync(new(instance));
            AssertTrue(documents.Complete); AssertEqual(2, documents.Documents.Count(item => item.Kind == "manifest"));
            string display = string.Join('\n', documents.Documents.SelectMany(item => item.Lines));
            AssertFalse(display.Contains("manifest-secret", StringComparison.Ordinal)); AssertFalse(display.Contains("metadata-secret", StringComparison.Ordinal));
            AssertTrue(display.Contains("fixture.Main", StringComparison.Ordinal)); AssertEqual(before, File.ReadAllText(metadata));
            File.WriteAllText(metadata, "{damaged");
            var damaged = await InstanceLocalDocumentsService.ReadAsync(new(instance));
            AssertFalse(damaged.Complete); AssertEqual("{damaged", File.ReadAllText(metadata));
            File.WriteAllText(Path.Combine(parent, "base.json"), """{"id":"base","inheritsFrom":"diagnostic"}""");
            var cycle = await InstanceLocalDocumentsService.ReadAsync(new(instance));
            AssertFalse(cycle.Complete); AssertTrue(cycle.Documents.Any(item => item.Status.Contains("循环", StringComparison.Ordinal)));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); bool canceled = false;
            try { await InstanceLocalDocumentsService.ReadAsync(new(instance), cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            AssertTrue(canceled);
        }
        finally { Directory.Delete(root, true); }
    }
}
