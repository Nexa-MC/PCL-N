using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Composition;
using Nexa.Services.Logging;
using Nexa.Services.Setup;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ContentStoreDeduplicatesAndPrunesOnlyUnreferencedObjects()
    {
        string directory = WorkspaceTestDirectory();
        try
        {
            string source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "shared"); await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "shared");
            var service = new ContentBackupService(Path.Combine(directory, "store"));
            AssertFalse((await service.ReadRetentionAsync()).Enabled);
            await service.SetRetentionAsync(new(true, 1, 90));
            AssertTrue((await new ContentBackupService(Path.Combine(directory, "store")).ReadRetentionAsync()).Enabled);
            var first = await service.CaptureAsync(source, "first"); var stats = await service.StatisticsAsync();
            AssertEqual(1, stats.Objects); AssertEqual(12L, stats.LogicalBytes); AssertEqual(6L, stats.PhysicalBytes);
            var preview = await service.PreviewPruneAsync(1, 90);
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "changed"); var second = await service.CaptureAsync(source, "second");
            await WorkspaceFailureAsync(() => service.ApplyPruneAsync(1, 90, preview.Revision)); AssertEqual(2, (await service.ListAsync()).Count);
            preview = await service.PreviewPruneAsync(1, 90); await service.ApplyPruneAsync(1, 90, preview.Revision);
            AssertEqual(1, (await service.ListAsync()).Count); AssertEqual(2, (await service.StatisticsAsync()).Objects);
            AssertTrue((await service.VerifyOfflineAsync(second.Identity)).Ready);
            await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "changed"); var third = await service.CaptureAsync(source, "third");
            preview = await service.PreviewPruneAsync(1, 90); AssertEqual(1, preview.Objects);
            await service.ApplyPruneAsync(1, 90, preview.Revision); AssertEqual(1, (await service.StatisticsAsync()).Objects);
            AssertTrue((await service.VerifyOfflineAsync(third.Identity)).Ready);
            int configuredCount = 1;
            var configured = new ContentBackupService(Path.Combine(directory, "store")) { ReadConfiguredKeepCount = () => configuredCount };
            await File.WriteAllTextAsync(Path.Combine(source, "b.txt"), "fourth"); await configured.CaptureAsync(source, "fourth");
            preview = await configured.PreviewPruneAsync(1, 90);
            configuredCount = 2;
            var policy = await configured.ReadRetentionAsync(); AssertTrue(policy.Enabled); AssertEqual(2, policy.KeepCount);
            await WorkspaceFailureAsync(() => configured.ApplyPruneAsync(1, 90, preview.Revision)); AssertEqual(2, (await configured.ListAsync()).Count);
            await WorkspaceFailureAsync(() => configured.SetRetentionAsync(new(true, 1, 90)));
            await configured.SetRetentionAsync(new(true, 2, 30)); AssertEqual(30, (await configured.ReadRetentionAsync()).KeepDays);
            await WorkspaceFailureAsync(async () => { await configured.PreviewPruneAsync(1, 90); });
            AssertEqual(0, (await configured.PreviewPruneAsync(2, 90)).BackupIdentities.Count);
            configuredCount = 0; await WorkspaceFailureAsync(async () => { await configured.ReadRetentionAsync(); });
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask ContentThinBackupAndRestoreVerifyBytesAndAdmitPaths()
    {
        string directory = WorkspaceTestDirectory();
        try
        {
            string source = Path.Combine(directory, "source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "file"), "original");
            var store = new ContentBackupService(Path.Combine(directory, "store")); var manifest = await store.CaptureAsync(source, "copy");
            string thin = Path.Combine(directory, "thin.json"); await store.ExportThinAsync(manifest.Identity, thin);
            var imported = new ContentBackupService(Path.Combine(directory, "other-store")); var report = await imported.ImportThinAsync(thin);
            AssertFalse(report.Ready); AssertEqual(1, report.MissingOrCorruptPaths.Count);
            string restored = Path.Combine(directory, "restored"); await store.RestoreAsync(manifest.Identity, restored);
            AssertEqual("original", await File.ReadAllTextAsync(Path.Combine(restored, "file")));
            await File.WriteAllTextAsync(Path.Combine(restored, "file"), "edited"); AssertTrue((await store.VerifyOfflineAsync(manifest.Identity)).Ready);
            await WorkspaceFailureAsync(() => store.RestoreAsync(manifest.Identity, restored));
            string objectPath = Path.Combine(directory, "store", "objects", manifest.Files[0].Sha256 + ".blob"); await File.WriteAllTextAsync(objectPath, "corrupt!");
            AssertFalse((await store.VerifyOfflineAsync(manifest.Identity)).Ready);
            string rejected = Path.Combine(directory, "rejected"); await WorkspaceFailureAsync(() => store.RestoreAsync(manifest.Identity, rejected));
            AssertFalse(Directory.Exists(rejected)); AssertFalse(Directory.EnumerateDirectories(directory, "rejected.nexa-restore-*").Any());
            var json = JsonNode.Parse(await File.ReadAllTextAsync(thin))!.AsObject(); json["identity"] = Guid.NewGuid().ToString("N"); json["files"]![0]!["path"] = "../escape";
            await File.WriteAllTextAsync(thin, json.ToJsonString()); await WorkspaceFailureAsync(async () => { await imported.ImportThinAsync(thin); });
            AssertEqual(1, (await imported.ListAsync()).Count); AssertFalse(File.Exists(Path.Combine(directory, "escape")));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask ContentStoreRejectsBusyLinksAndCancelledTransactions()
    {
        string directory = WorkspaceTestDirectory();
        try
        {
            string source = Path.Combine(directory, "source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "a"), "bytes");
            bool idle = false; var store = new ContentBackupService(Path.Combine(directory, "store"), () => idle);
            await WorkspaceFailureAsync(async () => { await store.CaptureAsync(source, "busy"); }); AssertEqual(0, (await store.ListAsync()).Count);
            idle = true;
            using var stop = new CancellationTokenSource(); stop.Cancel();
            bool cancelled = false; try { await store.CaptureAsync(source, "cancelled", stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled); AssertEqual(0, (await store.ListAsync()).Count);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(source, "linked"), Path.Combine(source, "a"));
                await WorkspaceFailureAsync(async () => { await store.CaptureAsync(source, "linked"); }); AssertEqual(0, (await store.ListAsync()).Count);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask LegacyMigrationRetainsSettingsAndOfflineIdentityWithoutCredentials()
    {
        string directory = WorkspaceTestDirectory();
        try
        {
            string source = Path.Combine(directory, "legacy"), destination = Path.Combine(directory, "new"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), """{"schemaVersion":1,"booleanOptions":{"UiTrayEnabled":false},"textOptions":{"unknownToken":"secret"}}""");
            await File.WriteAllTextAsync(Path.Combine(source, "launch-profiles.json"), """{"schemaVersion":1,"profiles":[{"username":"Offline","kind":"Offline","uuid":"0123456789abcdef0123456789abcdef","accessToken":"secret"},{"username":"Online","kind":"Microsoft","uuid":"0123456789abcdef0123456789abcdef","refreshToken":"private"}]}""");
            var service = new LegacyMigrationService(); var preview = await service.PreviewAsync(source, destination);
            AssertEqual(1, preview.Settings); AssertEqual(1, preview.OfflineAccounts); AssertEqual(1, preview.ReauthenticationAccounts);
            AssertFalse(Directory.Exists(destination)); await service.ApplyAsync(preview);
            string accounts = await File.ReadAllTextAsync(Path.Combine(destination, "profiles", "profiles.json"));
            AssertFalse(accounts.Contains("secret")); AssertFalse(accounts.Contains("private")); AssertFalse(accounts.Contains("Online")); AssertTrue(accounts.Contains("Offline"));
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(destination, "settings", "settings.json")));
            AssertFalse(settings.RootElement.GetProperty("booleanOptions").GetProperty("UiTrayEnabled").GetBoolean());
            AssertTrue((await File.ReadAllTextAsync(Path.Combine(source, "launch-profiles.json"))).Contains("secret"));
            await WorkspaceFailureAsync(async () => { await service.PreviewAsync(source, destination); });
            string secondDestination = Path.Combine(directory, "new2"); preview = await service.PreviewAsync(source, secondDestination);
            await File.AppendAllTextAsync(Path.Combine(source, "settings.json"), " ");
            await WorkspaceFailureAsync(() => service.ApplyAsync(preview)); AssertFalse(Directory.Exists(secondDestination));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask DurableDiagnosticHistoryAndRawWorkspaceExcludePrivateText()
    {
        string directory = WorkspaceTestDirectory();
        try
        {
            string historyDirectory = Path.Combine(directory, "history"); Directory.CreateDirectory(historyDirectory);
            string foreign = Path.Combine(historyDirectory, "foreign.json"); await File.WriteAllTextAsync(foreign, "unrelated private file");
            var sink = new DurableDiagnosticHistorySink(historyDirectory);
            sink.Write(new(1, DateTimeOffset.UtcNow, LogLevel.Info, "Launch", "password=secret /home/user/private", "token=private")
            { Operation = new("StartMinecraft", "exit", DiagnosticOperationOutcome.Failed) }, "raw private");
            sink.Write(new(2, DateTimeOffset.UtcNow, LogLevel.Info, "Launch", "unstructured", null), "unstructured");
            await sink.DisposeAsync(); var rows = await sink.ReadAsync(); AssertEqual(1, rows.Count); AssertEqual(DiagnosticOperationOutcome.Failed, rows[0].Outcome);
            string owned = Directory.GetFiles(historyDirectory).Single(path => path != foreign);
            string persisted = await File.ReadAllTextAsync(owned);
            AssertFalse(persisted.Contains("secret")); AssertFalse(persisted.Contains("private")); AssertFalse(persisted.Contains("user")); AssertEqual(0L, sink.WriteFailures);
            for (int i = 1; i < DurableDiagnosticHistorySink.MaximumEntries; i++)
                File.Copy(owned, Path.Combine(historyDirectory, i.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json"));
            var retention = new DurableDiagnosticHistorySink(historyDirectory);
            retention.Write(new(3, DateTimeOffset.UtcNow, LogLevel.Info, "Launch", "", null)
            { Operation = new("GameExit", "normal-exit", DiagnosticOperationOutcome.Completed) }, "");
            await retention.DisposeAsync(); AssertEqual(0L, retention.WriteFailures);
            AssertEqual(DurableDiagnosticHistorySink.MaximumEntries, (await retention.ReadAsync()).Count);
            AssertEqual("unrelated private file", await File.ReadAllTextAsync(foreign));
            var preview = DiagnosticRawWorkspace.Preview("password=secret Authorization: Bearer token-value\n/home/user/private.txt\n0123456789abcdef0123456789abcdef");
            AssertFalse(preview.RedactedText.Contains("secret")); AssertFalse(preview.RedactedText.Contains("token-value")); AssertFalse(preview.RedactedText.Contains("user")); AssertFalse(preview.RedactedText.Contains("012345"));
            string output = Path.Combine(directory, "raw.txt"); await WorkspaceFailureAsync(() => DiagnosticRawWorkspace.ExportAsync(output, preview, "stale")); AssertFalse(File.Exists(output));
            await DiagnosticRawWorkspace.ExportAsync(output, preview, preview.Revision); AssertEqual(preview.RedactedText, await File.ReadAllTextAsync(output));
            var bounded = DiagnosticRawWorkspace.Preview(new string('中', DiagnosticRawWorkspace.MaximumBytes)); AssertTrue(bounded.Truncated); AssertTrue(bounded.Utf8Bytes <= DiagnosticRawWorkspace.MaximumBytes);
            await ScopedDiagnosticHistorySurvivesReopenWithoutGuessingIdentity(directory);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask DiagnosticAiHonorsPreviewScopeBudgetAndCancellation()
    {
        using var handler = new WorkspaceAiHandler(); using var http = new HttpClient(handler); var ai = new DiagnosticAiService(http);
        var preview = DiagnosticAiService.PreviewFacts([new(DateTimeOffset.UtcNow, "Launch", "StartMinecraft", "exit", DiagnosticOperationOutcome.Failed)]);
        var request = new DiagnosticAiRequest(new("https://provider.example/v1/chat/completions"), "model", "key", DiagnosticAiDataScope.StructuredFacts, preview.Revision, 4096, 128);
        var result = await ai.SuggestAsync(request, preview, DiagnosticAiDataScope.StructuredFacts); AssertEqual("Inspect Java compatibility.", result.Text); AssertEqual(1, handler.Calls);
        AssertTrue(handler.Body.Contains("StartMinecraft")); AssertFalse(handler.Body.Contains("key"));
        AssertFalse(handler.Body.Contains("reasoning_effort", StringComparison.Ordinal));
        await ai.SuggestAsync(request with { Reasoning = DiagnosticAiReasoning.Medium }, preview, DiagnosticAiDataScope.StructuredFacts);
        using (var body = JsonDocument.Parse(handler.Body)) AssertEqual("medium", body.RootElement.GetProperty("reasoning_effort").GetString());
        AssertEqual(2, handler.Calls);
        await WorkspaceFailureAsync(async () => { await ai.SuggestAsync(request with { Reasoning = (DiagnosticAiReasoning)99 }, preview, DiagnosticAiDataScope.StructuredFacts); }); AssertEqual(2, handler.Calls);
        var disabled = new DiagnosticAiService(http) { IsEnabled = () => false };
        await WorkspaceFailureAsync(async () => { await disabled.SuggestAsync(request, preview, DiagnosticAiDataScope.StructuredFacts); }); AssertEqual(2, handler.Calls);
        int admissionReads = 0; var retired = new DiagnosticAiService(http) { IsEnabled = () => ++admissionReads == 1 };
        await WorkspaceFailureAsync(async () => { await retired.SuggestAsync(request, preview, DiagnosticAiDataScope.StructuredFacts); }); AssertEqual(2, handler.Calls);
        await WorkspaceFailureAsync(async () => { await ai.SuggestAsync(request, preview, DiagnosticAiDataScope.RedactedRawText); }); AssertEqual(2, handler.Calls);
        await WorkspaceFailureAsync(async () => { await ai.SuggestAsync(request with { MaximumInputBytes = 1 }, preview, DiagnosticAiDataScope.StructuredFacts); }); AssertEqual(2, handler.Calls);
        await WorkspaceFailureAsync(async () => { await ai.SuggestAsync(request with { PreviewRevision = "stale" }, preview, DiagnosticAiDataScope.StructuredFacts); }); AssertEqual(2, handler.Calls);
        handler.OutputTokens = 129; await WorkspaceFailureAsync(async () => { await ai.SuggestAsync(request, preview, DiagnosticAiDataScope.StructuredFacts); });
        using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
        try { await ai.SuggestAsync(request, preview, DiagnosticAiDataScope.StructuredFacts, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
        AssertTrue(cancelled);
    }

    private static async Task ScopedDiagnosticHistorySurvivesReopenWithoutGuessingIdentity(string directory)
    {
        string first = Path.Combine(directory, "root-a", "same-name"), second = Path.Combine(directory, "root-b", "same-name"), historyDirectory = Path.Combine(directory, "scoped-history");
        var context = new DiagnosticInstanceContext(DiagnosticInstanceIdentity.ScopeHash(first), Guid.NewGuid())
        { StartedAt = DateTimeOffset.UtcNow.AddSeconds(-4), EndedAt = DateTimeOffset.UtcNow, ExitCode = 7, FailureCode = "game_crash", LaunchDurationMilliseconds = 750 };
        var sink = new DurableDiagnosticHistorySink(historyDirectory);
        using (var log = CreateLogService())
        {
            log.AddSink(sink);
            using (var operation = log.BeginInstanceOperation("Process", "GameExit", context))
            { operation.Stage("game-crash", "password=secret " + first); operation.Reject("game_crash"); }
            using (var operation = log.BeginInstanceOperation("Process", "GameExit", context with { ScopeHash = DiagnosticInstanceIdentity.ScopeHash(second), SessionId = Guid.NewGuid(), ExitCode = 0, FailureCode = null }))
            { operation.Stage("normal-exit"); operation.Complete(); }
            using (var operation = log.BeginOperation("Launch", "StartMinecraft", "password=secret " + first)) operation.Complete();
        }
        sink.Write(new(99, DateTimeOffset.UtcNow, LogLevel.Info, "Process", "", null)
        { Operation = new("GameExit", "normal-exit", DiagnosticOperationOutcome.Completed) { Instance = context with { SessionId = Guid.Empty } } }, "");
        await sink.DisposeAsync(); AssertEqual(0L, sink.WriteFailures);
        string owned = Directory.GetFiles(historyDirectory).First(path => JsonNode.Parse(File.ReadAllText(path))!["instance"] is not null);
        var legacy = JsonNode.Parse(await File.ReadAllTextAsync(owned))!.AsObject(); legacy["schema"] = 1;
        await File.WriteAllTextAsync(Path.Combine(historyDirectory, DateTimeOffset.UtcNow.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json"), legacy.ToJsonString());
        foreach (string path in Directory.GetFiles(historyDirectory))
        {
            string persisted = await File.ReadAllTextAsync(path);
            AssertFalse(persisted.Contains("secret", StringComparison.Ordinal)); AssertFalse(persisted.Contains(directory, StringComparison.Ordinal));
        }
        await using var reopened = new DurableDiagnosticHistorySink(historyDirectory);
        var scoped = await reopened.ReadForInstanceAsync(first); AssertEqual(3, scoped.Count);
        AssertTrue(scoped.All(x => x.Instance?.SessionId == context.SessionId));
        var started = scoped.Single(x => x.Outcome == DiagnosticOperationOutcome.Started); AssertEqual(context, started.Instance);
        var terminal = scoped.Single(x => x.Outcome == DiagnosticOperationOutcome.Rejected); AssertEqual(context, terminal.Instance);
        AssertEqual(3, (await reopened.ReadForInstanceAsync(second)).Count); AssertEqual(9, (await reopened.ReadAsync()).Count);
        var commandBuilder = new XsrCommandRouterBuilder(); var queryBuilder = new XsrQueryRouterBuilder(); var observer = new RecordingDispatchObserver();
        ContentWorkspaceRuntime.Register(commandBuilder, queryBuilder, new ContentBackupService(Path.Combine(directory, "scoped-store")), new LegacyMigrationService(), reopened);
        var queries = queryBuilder.Build(observer); AssertTrue(queries.TryResolve(DiagnosticWorkspaceContract.InstanceHistory, out var route));
        var routed = await queries.QueryAsync<DurableInstanceDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(route, new(first));
        AssertTrue(routed.IsSuccess); AssertEqual(3, routed.Value!.Count); AssertTrue(observer.Completed.Count > 0);
        var invalid = await queries.QueryAsync<DurableInstanceDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(route, new("relative-instance")); AssertFalse(invalid.IsSuccess);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = await queries.QueryAsync<DurableInstanceDiagnosticHistoryQuery, IReadOnlyList<DurableDiagnosticEntry>>(route, new(first), cancellationToken: stop.Token); AssertFalse(cancelled.IsSuccess);
    }

    private static async ValueTask ContentWorkspaceRoutesObserveTypedMutationsAndRejections()
    {
        string directory = WorkspaceTestDirectory();
        try
        {
            string source = Path.Combine(directory, "source"); Directory.CreateDirectory(source); await File.WriteAllTextAsync(Path.Combine(source, "a"), "bytes");
            var backups = new ContentBackupService(Path.Combine(directory, "store")); await using var history = new DurableDiagnosticHistorySink(Path.Combine(directory, "history"));
            var commandBuilder = new XsrCommandRouterBuilder(); var queryBuilder = new XsrQueryRouterBuilder(); var observer = new RecordingDispatchObserver();
            ContentWorkspaceRuntime.Register(commandBuilder, queryBuilder, backups, new LegacyMigrationService(), history);
            var commands = commandBuilder.Build(observer); var queries = queryBuilder.Build(observer);
            AssertTrue(commands.TryResolve(ContentWorkspaceContract.Capture, out var capture));
            AssertTrue((await commands.Dispatch(capture, new ContentBackupCaptureCommand(source, "routed")).Completion).IsSuccess);
            AssertTrue(queries.TryResolve(ContentWorkspaceContract.List, out var list));
            var rows = await queries.QueryAsync<ContentBackupListQuery, IReadOnlyList<ContentBackupManifest>>(list, new());
            AssertTrue(rows.IsSuccess); AssertEqual(1, rows.Value!.Count);
            AssertTrue(commands.TryResolve(ContentWorkspaceContract.Prune, out var prune));
            AssertFalse((await commands.Dispatch(prune, new ContentPruneCommand(1, 90, "stale")).Completion).IsSuccess);
            AssertEqual(1, (await backups.ListAsync()).Count); AssertTrue(observer.Completed.Count >= 3);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            AssertFalse((await commands.Dispatch(capture, new ContentBackupCaptureCommand(source, "cancelled"), cancellationToken: stop.Token).Completion).IsSuccess);
            AssertEqual(1, (await backups.ListAsync()).Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class WorkspaceAiHandler : HttpMessageHandler
    {
        internal int Calls, OutputTokens = 12;
        internal string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            AssertEqual("key", request.Headers.Authorization!.Parameter); Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Inspect Java compatibility.\"}}],\"usage\":{\"completion_tokens\":" + OutputTokens + "}}", Encoding.UTF8, "application/json") };
        }
    }
    private static string WorkspaceTestDirectory() { string path = Path.Combine(Path.GetTempPath(), "nexa-workspace-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static async Task WorkspaceFailureAsync(Func<Task> action)
    {
        bool failed = false; try { await action(); } catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException) { failed = true; }
        AssertTrue(failed);
    }
}
