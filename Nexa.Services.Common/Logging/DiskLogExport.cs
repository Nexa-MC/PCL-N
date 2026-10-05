using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Nexa.Services.Logging;

/// <summary>Projects owned disk-log headers. Arbitrary text is never an export field.</summary>
internal static class DiskLogExport
{
    private const int MaximumRecords = 2000;
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly record struct Fact(string Time, LogLevel Level, string Module, DateTime ModifiedUtc, bool Current);

    internal static Task WriteAsync(string destination, DiskLogSnapshot snapshot, CancellationToken token) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Choose an absolute export destination.", nameof(destination));
        string target = Path.GetFullPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".nexa-log-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var facts = new Queue<Fact>();
            long records = 0;
            await using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true))
            {
                created = true;
                foreach (var file in snapshot.Files)
                {
                    token.ThrowIfCancellationRequested();
                    // The worker captured this handle before resuming rotation/pruning; names are never reopened.
                    FileStream source = file.Stream;
                    long remaining = file.Length;
                    byte[] buffer = new byte[65536], prefix = new byte[256];
                    int prefixLength = 0;
                    while (remaining > 0)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                        if (read == 0) break;
                        remaining -= read;
                        for (int index = 0; index < read; index++)
                        {
                            if (buffer[index] == '\n') { RecordPrefix(); prefixLength = 0; }
                            else if (prefixLength < prefix.Length) prefix[prefixLength++] = buffer[index];
                        }
                        token.ThrowIfCancellationRequested();
                    }
                    // A final incomplete line cannot be a coherent published record.

                    void RecordPrefix()
                    {
                        if (!TryReadFact(Encoding.ASCII.GetString(prefix, 0, prefixLength), file.ModifiedUtc, file.Current, out var fact)) return;
                        records++; facts.Enqueue(fact);
                        if (facts.Count > MaximumRecords) facts.Dequeue();
                    }
                }
                using MemoryStream content = new();
                using (Utf8JsonWriter json = new(content))
                {
                    json.WriteStartObject(); json.WriteNumber("schema", 1);
                    json.WriteString("scope", "current-and-retained-disk");
                    json.WriteString("log_policy", "timestamp-level-built-in-module-only; messages/exceptions/context excluded");
                    json.WriteString("timestamp_policy", "local-clock-only; disk format has no UTC date");
                    json.WriteNumber("source_files", snapshot.Files.Count);
                    json.WriteBoolean("records_truncated", records > MaximumRecords || snapshot.Truncated);
                    json.WriteStartArray("entries");
                    foreach (var fact in facts)
                    {
                        token.ThrowIfCancellationRequested();
                        json.WriteStartObject(); json.WriteString("local_time", fact.Time);
                        json.WriteString("source_last_write_utc", fact.ModifiedUtc);
                        json.WriteString("source", fact.Current ? "current" : "archive");
                        json.WriteString("level", fact.Level.ToString()); json.WriteString("module", fact.Module);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray(); json.WriteEndObject();
                }
                if (content.Length > MaximumBytes) throw new InvalidDataException("Disk log export content exceeds budget.");
                content.Position = 0;
                using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using Stream entry = archive.CreateEntry("logs.json", CompressionLevel.Fastest).Open();
                    await content.CopyToAsync(entry, token).ConfigureAwait(false);
                }
                if (output.Length > MaximumBytes) throw new InvalidDataException("Disk log export archive exceeds budget.");
                await output.FlushAsync(token).ConfigureAwait(false); output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false); created = false;
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }, token);

    private static bool TryReadFact(string prefix, DateTime modified, bool current, out Fact fact)
    {
        fact = default;
        if (prefix.Length < 20 || prefix[0] != '[' || prefix[13] != ']'
            || !TimeOnly.TryParseExact(prefix.AsSpan(1, 12), "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        int levelStart = 15;
        if (prefix[levelStart] != '[') return false;
        int levelEnd = prefix.IndexOf(']', levelStart + 1);
        if (levelEnd < 0 || !Enum.TryParse<LogLevel>(prefix.AsSpan(levelStart + 1, levelEnd - levelStart - 1), out var level) || !Enum.IsDefined(level)) return false;
        int moduleStart = levelEnd + 2;
        if (moduleStart >= prefix.Length || prefix[moduleStart] != '[') return false;
        int moduleEnd = prefix.IndexOf(']', moduleStart + 1);
        if (moduleEnd < 0) return false;
        string module = prefix[(moduleStart + 1)..moduleEnd];
        // Module text can be supplied by external call sites too. Only known built-in literals survive.
        module = module switch
        {
            "Account" or "AccountLogin" or "AccountOnboarding" or "AccountSkin" or "Cloudflare" or "Download" or "Downloads"
                or "GameOutput" or "General" or "HTTP" or "Install" or "Java" or "Launch" or "Launcher" or "LittleSkinAuth"
                or "Logging" or "MicrosoftAuth" or "Process" or "Recovery" or "Settings" or "Sidecar" or "Telemetry"
                or "UI" or "VersionScan" or "YggdrasilAuth" => module,
            _ => "unknown",
        };
        fact = new(prefix.Substring(1, 12), level, module, modified, current);
        return true;
    }
}
