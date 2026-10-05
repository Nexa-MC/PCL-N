using System.Diagnostics;
using Nexa.Desktop.Ui;
using Nexa.Services.Composition;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.UI.Next;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static MinecraftRuntime ProcessLogRuntime(XsrQueryHandler<MinecraftProcessOutputQuery, MinecraftProcessOutputSnapshot> handler)
    {
        XsrQueryRouterBuilder queries = new();
        queries.Register(MinecraftProcessOutputContract.Query, handler);
        NoopDispatchObserver observer = new();
        return new(new MinecraftVersionDiscovery(), new MinecraftInstanceDiscovery(), new MinecraftProcessService(),
            new XsrCommandRouterBuilder().Build(observer), queries.Build(observer));
    }

    private static void PublishLogSessions(LaunchPageFixture fixture, params MinecraftProcessSnapshot[] sessions)
    {
        var id = fixture.Store.Resolve(MinecraftProcessStateComposition.SessionsKey);
        var revision = fixture.Store.ReadCollection<MinecraftProcessSnapshot>(id).Revision;
        fixture.Store.PublishDelta(id, new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(revision, sessions, []));
    }

    private static void ProcessLogViewerReadsSelectedSessionAndCopiesRedactedPages()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Guid requested = default;
        const string secret = "fixture-output-credential-never-display";
        var entries = Enumerable.Range(1, 63).Select(index => new MinecraftProcessOutputEntry(index,
            index % 2 == 0 ? MinecraftProcessOutputChannel.Stderr : MinecraftProcessOutputChannel.Stdout,
            index == 1 ? LogRedactor.Redact("access_token=" + secret) : index == 37 ? "needle {state fake.key}"
                : "line " + index)).ToArray();
        using var runtime = ProcessLogRuntime((query, _) =>
        {
            requested = query.SessionId;
            return ValueTask.FromResult(XsrResult.Success<MinecraftProcessOutputSnapshot>(
                new(query.SessionId, query.SessionId == first ? "first" : "second", MinecraftProcessState.Running,
                    63, 10, query.SessionId == first ? entries : [new(1, MinecraftProcessOutputChannel.Stderr, "other session")])));
        });
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("first")]), runtime, ownsMinecraftRuntime: false);
        fixture.Shell.Renderer.ReducedMotion = true;
        string? copied = null;
        fixture.Controller.CopyProcessLogText = text => { copied = text; return Task.CompletedTask; };
        var started = DateTimeOffset.UtcNow;
        PublishLogSessions(fixture,
            new(first, "first", 11, MinecraftProcessState.Running, null, started.AddMinutes(-2), null),
            new(second, "second", 12, MinecraftProcessState.Running, null, started.AddMinutes(-1), null));
        var scene = fixture.Shell.Render(new(1000, 700));
        AssertTrue(fixture.Shell.Renderer.Activate(FindByKey(fixture.Shell, scene, "process-logs-" + first).Entity));
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual(first, requested);
        AssertEqual("游戏日志", FindByKey(fixture.Shell, scene, "TitleSubpage").Text);
        var rows = FindByKey(fixture.Shell, scene, "ProcessLogRows").Entity;
        AssertEqual(ProcessLogPageController.PageSize, fixture.Shell.Tree.Children(rows).Count);
        AssertEqual("1 / 3", FindByKey(fixture.Shell, scene, "ProcessLogPageNumber").Text);
        AssertFalse(FindByKey(fixture.Shell, scene, "ProcessLogEntry:1").Text!.Contains(secret, StringComparison.Ordinal));
        AssertTrue(FindByKey(fixture.Shell, scene, "ProcessLogStatus").Text!.Contains("已省略 10", StringComparison.Ordinal));
        Emit(fixture.Intents, "ui.process.log.copy-page");
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        AssertTrue(copied!.Contains("[stdout] access_token=<redacted>", StringComparison.Ordinal));
        AssertTrue(copied.Contains("[stderr] line 24", StringComparison.Ordinal));
        AssertFalse(copied.Contains(secret, StringComparison.Ordinal));

        var selected = FindByKey(fixture.Shell, scene, "ProcessLogEntry:1").Entity;
        Emit(fixture.Intents, "ui.process.log.select", selected);
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual(selected, FindByKey(fixture.Shell, scene, "ProcessLogEntry:1").Entity);
        Emit(fixture.Intents, "ui.process.log.next");
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual("2 / 3", FindByKey(fixture.Shell, scene, "ProcessLogPageNumber").Text);
        Emit(fixture.Intents, "ui.process.log.copy-selected");
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        AssertEqual("[stdout] access_token=<redacted>", copied);
        Emit(fixture.Intents, "ui.process.log.next");
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual("3 / 3", FindByKey(fixture.Shell, scene, "ProcessLogPageNumber").Text);
        AssertEqual(15, fixture.Shell.Tree.Children(rows).Count);

        var search = FindByKey(fixture.Shell, scene, "ProcessLogSearch").Entity;
        fixture.Shell.Renderer.SetTextInputValue(search, "NEEDLE");
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual(1, fixture.Shell.Tree.Children(rows).Count);
        AssertEqual("[stdout] needle {state fake.key}", FindByKey(fixture.Shell, scene, "ProcessLogEntry:37").Text);
        Emit(fixture.Intents, "ui.process.log.stderr");
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual(0, fixture.Shell.Tree.Children(rows).Count);
        AssertEqual("没有符合搜索条件的日志。", FindByKey(fixture.Shell, scene, "ProcessLogEmpty").Text);
        Emit(fixture.Intents, "ui.process.log.next-session");
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual(second, requested);
        AssertEqual("[stderr] other session", FindByKey(fixture.Shell, scene, "ProcessLogEntry:1").Text);
        AssertFalse(FindByKey(fixture.Shell, scene, "ProcessLogCopySelected").IsClickable);
        PublishLogSessions(fixture,
            new(first, "first", 11, MinecraftProcessState.Exited, 0, started.AddMinutes(-2), started),
            new(second, "second", 12, MinecraftProcessState.Cancelled, null, started.AddMinutes(-1), started));
        scene = fixture.Shell.Render(new(1000, 700));
        AssertTrue(FindByKey(fixture.Shell, scene, "ProcessLogSession").Text!.Contains("已结束（取消）", StringComparison.Ordinal));
        AssertFalse(HasKey(fixture.Shell, scene, "process-stop-" + second));
        Emit(fixture.Intents, "ui.process.log.copy-page");
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        AssertEqual("[stderr] other session", copied);
        Emit(fixture.Intents, "ui.page.back");
        scene = fixture.Shell.Render(new(1000, 700));
        AssertTrue(FindByKey(fixture.Shell, scene, "process-logs-" + second).IsClickable);
    }

    private static void ProcessLogViewerCancelsRetiredReadsAndRejectsWrongSessions()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var requests = new List<(Guid Session, CancellationToken Token, TaskCompletionSource<XsrResult<MinecraftProcessOutputSnapshot>> Completion)>();
        object gate = new();
        using var runtime = ProcessLogRuntime((query, token) =>
        {
            TaskCompletionSource<XsrResult<MinecraftProcessOutputSnapshot>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) requests.Add((query.SessionId, token, completion));
            return new(completion.Task);
        });
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([Instance("first")]), runtime, ownsMinecraftRuntime: false);
        fixture.Shell.Renderer.ReducedMotion = true;
        var started = DateTimeOffset.UtcNow;
        PublishLogSessions(fixture,
            new(first, "first", 11, MinecraftProcessState.Running, null, started.AddMinutes(-1), null),
            new(second, "second", 12, MinecraftProcessState.Running, null, started, null));
        fixture.Shell.Render(new(1000, 700));
        Emit(fixture.Intents, "ui.process.logs." + first.ToString("N"));
        Task retired = fixture.Controller.WaitForProcessLogsAsync();
        AssertTrue(SpinWait.SpinUntil(() => { lock (gate) return requests.Count == 1; }, TimeSpan.FromSeconds(2)));
        Emit(fixture.Intents, "ui.page.back");
        lock (gate) AssertTrue(requests[0].Token.IsCancellationRequested);
        Emit(fixture.Intents, "ui.process.logs." + second.ToString("N"));
        AssertTrue(SpinWait.SpinUntil(() => { lock (gate) return requests.Count == 2; }, TimeSpan.FromSeconds(2)));
        lock (gate)
        {
            requests[0].Completion.SetResult(XsrResult.Success<MinecraftProcessOutputSnapshot>(new(first, "first",
                MinecraftProcessState.Running, 1, 0, [new(1, MinecraftProcessOutputChannel.Stdout, "stale first output")])));
            requests[1].Completion.SetResult(XsrResult.Success<MinecraftProcessOutputSnapshot>(new(second, "second",
                MinecraftProcessState.Running, 1, 0, [new(1, MinecraftProcessOutputChannel.Stderr, "current second output")])));
        }
        retired.GetAwaiter().GetResult();
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        var scene = fixture.Shell.Render(new(1000, 700));
        AssertEqual("[stderr] current second output", FindByKey(fixture.Shell, scene, "ProcessLogEntry:1").Text);
        Emit(fixture.Intents, "ui.process.log.refresh");
        AssertTrue(SpinWait.SpinUntil(() => { lock (gate) return requests.Count == 3; }, TimeSpan.FromSeconds(2)));
        lock (gate) requests[2].Completion.SetResult(XsrResult.Success<MinecraftProcessOutputSnapshot>(new(first, "first",
            MinecraftProcessState.Running, 1, 0, [new(1, MinecraftProcessOutputChannel.Stdout, "wrong session")])));
        fixture.Controller.WaitForProcessLogsAsync().GetAwaiter().GetResult();
        scene = fixture.Shell.Render(new(1000, 700));
        AssertFalse(HasKey(fixture.Shell, scene, "ProcessLogEntry:1"));
        AssertEqual("日志与所选运行不匹配。", FindByKey(fixture.Shell, scene, "ProcessLogStatus").Text);

        Emit(fixture.Intents, "ui.process.log.refresh");
        AssertTrue(SpinWait.SpinUntil(() => { lock (gate) return requests.Count == 4; }, TimeSpan.FromSeconds(2)));
        Task beforeDispose = fixture.Controller.WaitForProcessLogsAsync();
        fixture.Controller.Dispose();
        lock (gate)
        {
            AssertTrue(requests[3].Token.IsCancellationRequested);
            requests[3].Completion.SetResult(XsrResult.Success<MinecraftProcessOutputSnapshot>(new(second, "second",
                MinecraftProcessState.Exited, 1, 0, [])));
        }
        beforeDispose.GetAwaiter().GetResult();
    }

    private static void ProcessOutputQueryCapturesBoundedRedactedStreamsAndEndedSessions()
    {
        const string secret = "fixture-pipe-token-must-be-redacted";
        string script = OperatingSystem.IsWindows()
            ? "echo access_token=" + secret + " & echo standard-error 1>&2 & for /L %i in (1,1,600) do @echo output-%i"
            : "printf 'access_token=" + secret + "\\n'; printf 'standard-error\\n' >&2; i=0; while [ $i -lt 600 ]; do printf 'output-%s\\n' $i; i=$((i+1)); done";
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows() ? ["/d", "/c", script] : ["-c", script];
        MinecraftLaunchPlan plan = new(executable, Path.GetTempPath(), arguments, [], [],
            new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "fixture.Main", []));
        var service = new MinecraftProcessService(new ProcessLogTestPort());
        try
        {
            using var runtime = MinecraftRuntimeComposer.Compose(processes: service);
            var session = service.StartAsync(plan, "pipe-output").AsTask().GetAwaiter().GetResult();
            session.WaitForExitAsync().AsTask().GetAwaiter().GetResult();
            AssertTrue(runtime.Queries.TryResolve(MinecraftProcessOutputContract.Query, out var route));
            var result = runtime.Queries.QueryAsync<MinecraftProcessOutputQuery, MinecraftProcessOutputSnapshot>(route,
                new(session.Snapshot.SessionId)).AsTask().GetAwaiter().GetResult();
            AssertTrue(result.IsSuccess);
            AssertEqual(session.Snapshot.SessionId, result.Value!.SessionId);
            AssertEqual(MinecraftProcessState.Exited, result.Value.State);
            AssertEqual(MinecraftProcessSession.MaximumOutputEntries, result.Value.Entries.Count);
            AssertTrue(result.Value.DroppedEntries >= 89);
            AssertFalse(result.Value.Entries.Any(entry => entry.Text.Contains(secret, StringComparison.Ordinal)));
            AssertTrue(result.Value.Entries.Any(entry => entry.Stream == MinecraftProcessOutputChannel.Stdout && entry.Text == "output-599")
                || OperatingSystem.IsWindows() && result.Value.Entries.Any(entry => entry.Text == "output-600"));
            var evidence = session.ReadSeparatedEvidenceAsync().GetAwaiter().GetResult();
            AssertEqual(100, evidence.Stdout.Length);
            AssertTrue(evidence.Stderr.Any(line => line.TrimEnd() == "standard-error"));

            // A short second session keeps both streams and redaction observable instead of
            // depending on which stream happened to survive the saturated first tail.
            string shortScript = OperatingSystem.IsWindows()
                ? "echo access_token=" + secret + " & echo standard-error 1>&2"
                : "printf 'access_token=" + secret + "\\n'; printf 'standard-error\\n' >&2; printf 'token='; i=0; while [ $i -lt 2100 ]; do printf x; i=$((i+1)); done; printf '\\n'";
            var second = service.StartAsync(plan with { Arguments = OperatingSystem.IsWindows() ? ["/d", "/c", shortScript] : ["-c", shortScript] },
                "short-output").AsTask().GetAwaiter().GetResult();
            second.WaitForExitAsync().AsTask().GetAwaiter().GetResult();
            var shortResult = runtime.Queries.QueryAsync<MinecraftProcessOutputQuery, MinecraftProcessOutputSnapshot>(route,
                new(second.Snapshot.SessionId)).AsTask().GetAwaiter().GetResult();
            AssertTrue(shortResult.IsSuccess);
            AssertTrue(shortResult.Value!.Entries.Any(entry => entry.Stream == MinecraftProcessOutputChannel.Stdout
                && entry.Text.StartsWith("access_token=<redacted>", StringComparison.Ordinal)));
            AssertTrue(shortResult.Value.Entries.Any(entry => entry.Stream == MinecraftProcessOutputChannel.Stderr && entry.Text.TrimEnd() == "standard-error"));
            AssertFalse(shortResult.Value.Entries.Any(entry => entry.Text.Contains(secret, StringComparison.Ordinal)));
            if (!OperatingSystem.IsWindows())
                AssertTrue(shortResult.Value.Entries.Any(entry => entry.Text == "[日志行超过 2048 字符，已省略]"));
            AssertTrue(shortResult.Value.Entries.All(entry => entry.Text.Length <= 2060));
            AssertFalse(runtime.Queries.QueryAsync<MinecraftProcessOutputQuery, MinecraftProcessOutputSnapshot>(route,
                new(Guid.NewGuid())).AsTask().GetAwaiter().GetResult().IsSuccess);
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private sealed class ProcessLogTestPort : IMinecraftProcessPort
    {
        public bool UsesPrivateArgumentTransport => true;
        public ValueTask<System.Diagnostics.Process> StartAsync(ProcessStartInfo startInfo, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            startInfo.RedirectStandardOutput = true; startInfo.RedirectStandardError = true;
            return ValueTask.FromResult(System.Diagnostics.Process.Start(startInfo) ?? throw new IOException("Unable to start output fixture."));
        }
    }
}
