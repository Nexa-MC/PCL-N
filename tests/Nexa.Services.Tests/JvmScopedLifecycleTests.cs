using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JvmLifecycleRecordsScopedProcessStartAndTerminalExit()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Nexa.Services.Tests.exe" : "Nexa.Services.Tests");
        string directory = CreateTempDirectory();
        try
        {
            using var log = CreateLogService();
            await using var history = new DurableDiagnosticHistorySink(Path.Combine(directory, "history"));
            log.AddSink(history);
            await using var processes = new MinecraftProcessService(jvmHostExecutable: executable);
            var host = new JvmHostService(processes) { Log = log };
            var plan = new MinecraftLaunchPlan("java", directory, ["-cp", "fixture", "fixture.Main", "", "private-token"],
                ["fixture"], [], new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "fixture.Main", []))
            { MainClassIndex = 2 };
            var session = await host.StartAsync(plan, "fixture");
            var started = log.GetSnapshot().Single(entry => entry.Operation is { Name: "GameLaunch", Outcome: DiagnosticOperationOutcome.Completed });
            AssertTrue(started.Operation!.Instance is not null);
            var context = started.Operation.Instance!;
            AssertEqual(session.Snapshot.SessionId, context.SessionId);
            AssertEqual(DiagnosticInstanceIdentity.ScopeHash(directory), context.ScopeHash);
            AssertEqual(session.Snapshot.StartedAt, context.StartedAt!.Value);
            AssertTrue(context.LaunchDurationMilliseconds is >= 0);
            AssertTrue(context.EndedAt is null && context.ExitCode is null && context.FailureCode is null);
            AssertFalse(started.ToDisplayText().Contains(directory, StringComparison.Ordinal));
            AssertEqual(0, await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
            AssertTrue(SpinWait.SpinUntil(() => log.GetSnapshot().Any(entry => entry.Operation is
            { Name: "GameExit", Outcome: DiagnosticOperationOutcome.Completed }), TimeSpan.FromSeconds(5)));
            var ended = log.GetSnapshot().Single(entry => entry.Operation is { Name: "GameExit", Outcome: DiagnosticOperationOutcome.Completed });
            AssertEqual(context.ScopeHash, ended.Operation!.Instance!.ScopeHash);
            AssertEqual(context.SessionId, ended.Operation.Instance.SessionId);
            AssertEqual(0, ended.Operation.Instance.ExitCode!.Value);
            AssertTrue(ended.Operation.Instance.EndedAt >= context.StartedAt);
            AssertTrue(ended.Operation.Instance.FailureCode is null);
            await history.DisposeAsync();
            var durable = await history.ReadForInstanceAsync(directory);
            AssertTrue(durable.Any(entry => entry.Operation == "GameLaunch" && entry.Outcome == DiagnosticOperationOutcome.Completed
                && entry.Instance?.SessionId == context.SessionId && entry.Instance.EndedAt is null));
            AssertTrue(durable.Any(entry => entry.Operation == "GameExit" && entry.Outcome == DiagnosticOperationOutcome.Completed
                && entry.Instance?.SessionId == context.SessionId && entry.Instance.ExitCode == 0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
