using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

namespace Nexa.Services.Logging;

/// <summary>Persistent structured history. Logging never waits for disk IO or a consumer.</summary>
public sealed class DurableDiagnosticHistorySink : ILogSink, IAsyncDisposable
{
    public const int MaximumEntries = 1024;
    private readonly string _directory;
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    private long _writeFailures;
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public DurableDiagnosticHistorySink(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择绝对诊断目录。", nameof(directory));
        _directory = Path.GetFullPath(directory); _writer = Task.Run(WriteLoopAsync);
    }

    public void Write(LogEntry entry, string formattedLine)
    {
        if (entry.Operation is not { } facts || !Valid(entry.Module) || !Valid(facts.Name) || !Valid(facts.Stage) || !Enum.IsDefined(facts.Outcome)) return;
        if (facts.Instance is { } instance && !DiagnosticInstanceIdentity.Valid(instance)) return;
        // Neither Message nor ExceptionText is retained by the queue.
        _queue.Writer.TryWrite(entry with { Message = "", ExceptionText = null });
    }

    public async Task<IReadOnlyList<DurableDiagnosticEntry>> ReadAsync(CancellationToken token = default)
    {
        CheckLinks(_directory); if (!Directory.Exists(_directory)) return [];
        List<DurableDiagnosticEntry> entries = [];
        int visited = 0;
        foreach (string path in EnumerateOwnedPaths(_directory).OrderDescending(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); if (++visited > MaximumEntries + 128) throw new IOException("诊断历史条目超过预算。");
            CheckLinks(path); await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            if (file.Length > 4096) throw new IOException("诊断历史记录超过预算。");
            byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
            int schema = root.GetProperty("schema").GetInt32();
            if (schema is not (1 or 2)) throw new IOException("诊断历史版本不支持。");
            string module = root.GetProperty("module").GetString()!, operation = root.GetProperty("operation").GetString()!, stage = root.GetProperty("stage").GetString()!;
            if (!Valid(module) || !Valid(operation) || !Valid(stage) || !Enum.TryParse<DiagnosticOperationOutcome>(root.GetProperty("outcome").GetString(), out var outcome) || !Enum.IsDefined(outcome)) throw new IOException("诊断历史内容无效。");
            entries.Add(new(DateTimeOffset.ParseExact(root.GetProperty("timestamp").GetString()!, "O", CultureInfo.InvariantCulture), module, operation, stage, outcome)
            { Instance = schema == 2 ? ReadInstance(root) : null });
        }
        return Array.AsReadOnly(entries.OrderByDescending(x => x.Timestamp).Take(MaximumEntries).ToArray());
    }

    public async Task<IReadOnlyList<DurableDiagnosticEntry>> ReadForInstanceAsync(string instanceDirectory, CancellationToken token = default)
    {
        string scope = DiagnosticInstanceIdentity.ScopeHash(instanceDirectory);
        return Array.AsReadOnly((await ReadAsync(token).ConfigureAwait(false)).Where(x => x.Instance is { } instance
            && instance.ScopeHash.Equals(scope, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    public async ValueTask DisposeAsync()
    { _queue.Writer.TryComplete(); await _writer.ConfigureAwait(false); }

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            string? temporary = null;
            try
            {
                CheckLinks(_directory); Directory.CreateDirectory(_directory); CheckLinks(_directory);
                string basename = entry.Timestamp.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                temporary = Path.Combine(_directory, basename + ".part"); string target = Path.Combine(_directory, basename + ".json");
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer))
                {
                    json.WriteStartObject(); json.WriteNumber("schema", 2); json.WriteString("timestamp", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                    json.WriteString("module", entry.Module); json.WriteString("operation", entry.Operation!.Name); json.WriteString("stage", entry.Operation.Stage);
                    json.WriteString("outcome", entry.Operation.Outcome.ToString());
                    if (entry.Operation.Instance is { } instance) WriteInstance(json, instance);
                    json.WriteEndObject();
                }
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                { await file.WriteAsync(buffer.ToArray()).ConfigureAwait(false); await file.FlushAsync().ConfigureAwait(false); file.Flush(true); }
                CheckLinks(target); File.Move(temporary, target); temporary = null;
                var paths = EnumerateOwnedPaths(_directory);
                foreach (string path in paths.OrderDescending(StringComparer.Ordinal).Skip(MaximumEntries))
                { CheckLinks(path); File.Delete(path); }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { Interlocked.Increment(ref _writeFailures); }
            finally { if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } }
        }
    }

    private static void WriteInstance(Utf8JsonWriter json, DiagnosticInstanceContext instance)
    {
        json.WriteStartObject("instance"); json.WriteString("scopeHash", instance.ScopeHash); json.WriteString("sessionId", instance.SessionId.ToString("N"));
        if (instance.ExitCode is { } code) json.WriteNumber("exitCode", code);
        if (instance.FailureCode is { } failure) json.WriteString("failureCode", failure);
        if (instance.StartedAt is { } started) json.WriteString("startedAt", started.ToString("O", CultureInfo.InvariantCulture));
        if (instance.EndedAt is { } ended) json.WriteString("endedAt", ended.ToString("O", CultureInfo.InvariantCulture));
        if (instance.LaunchDurationMilliseconds is { } duration) json.WriteNumber("launchDurationMilliseconds", duration);
        json.WriteEndObject();
    }

    private static DiagnosticInstanceContext? ReadInstance(JsonElement root)
    {
        if (!root.TryGetProperty("instance", out var value)) return null;
        var instance = new DiagnosticInstanceContext(value.GetProperty("scopeHash").GetString()!, Guid.ParseExact(value.GetProperty("sessionId").GetString()!, "N"))
        {
            ExitCode = value.TryGetProperty("exitCode", out var code) ? code.GetInt32() : null,
            FailureCode = value.TryGetProperty("failureCode", out var failure) ? failure.GetString() : null,
            StartedAt = value.TryGetProperty("startedAt", out var started) ? DateTimeOffset.ParseExact(started.GetString()!, "O", CultureInfo.InvariantCulture) : null,
            EndedAt = value.TryGetProperty("endedAt", out var ended) ? DateTimeOffset.ParseExact(ended.GetString()!, "O", CultureInfo.InvariantCulture) : null,
            LaunchDurationMilliseconds = value.TryGetProperty("launchDurationMilliseconds", out var duration) ? duration.GetInt64() : null
        };
        if (!DiagnosticInstanceIdentity.Valid(instance)) throw new IOException("实例诊断事实无效。");
        return instance;
    }

    private static string[] EnumerateOwnedPaths(string directory)
    {
        var paths = Directory.EnumerateFiles(directory, "*.json").Take(MaximumEntries + 129).ToArray();
        if (paths.Length > MaximumEntries + 128) throw new IOException("诊断目录条目超过预算。");
        return paths.Where(IsOwnedPath).ToArray();
    }

    private static bool IsOwnedPath(string path)
    {
        string basename = Path.GetFileNameWithoutExtension(path);
        if (basename.Length != 52 || basename[19] != '-') return false;
        for (int i = 0; i < 19; i++) if (!char.IsAsciiDigit(basename[i])) return false;
        return Guid.TryParseExact(basename.AsSpan(20), "N", out _);
    }

    private static bool Valid(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or ' ');
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        { try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("诊断目录不能包含链接。"); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
    }
}
