using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nexa.Services.Logging;

/// <summary>Explicit raw selection; credentials, user paths and IDs are removed before preview.</summary>
public static partial class DiagnosticRawWorkspace
{
    public const int MaximumBytes = 256 * 1024;
    public static DiagnosticRawPreview Preview(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        bool truncated = input.Length > MaximumBytes;
        string text = input[..Math.Min(input.Length, MaximumBytes)];
        text = LogRedactor.Redact(text);
        text = PathPattern().Replace(text, "<path>"); text = IdentifierPattern().Replace(text, "<identity>");
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaximumBytes)
        {
            text = Encoding.UTF8.GetString(bytes.AsSpan(0, MaximumBytes));
            if (text.EndsWith('\uFFFD')) text = text[..^1];
            bytes = Encoding.UTF8.GetBytes(text); truncated = true;
        }
        string revision = Convert.ToHexString(SHA256.HashData(bytes));
        return new(revision, text, bytes.Length, truncated);
    }

    public static async Task ExportAsync(string destination, DiagnosticRawPreview preview, string expectedRevision, CancellationToken token = default)
    {
        var admitted = Preview(preview.RedactedText);
        if (admitted.Revision != preview.Revision || admitted.Revision != expectedRevision) throw new IOException("诊断预览已变化。");
        if (!Path.IsPathFullyQualified(destination)) throw new IOException("请选择绝对导出路径。");
        string path = Path.GetFullPath(destination), stage = path + "." + Guid.NewGuid().ToString("N") + ".part";
        CheckLinks(path);
        try
        {
            await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            { await file.WriteAsync(Encoding.UTF8.GetBytes(admitted.RedactedText), token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true); }
            token.ThrowIfCancellationRequested(); CheckLinks(path); File.Move(stage, path, false);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    [GeneratedRegex("(?:[A-Za-z]:[\\\\/][^\\r\\n\\t\"<>|]+|/(?:home|Users|workspace|tmp|var|private|mnt)/[^\\r\\n\\t\"<>|]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PathPattern();
    [GeneratedRegex("\\b(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12})\\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IdentifierPattern();
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        { try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("诊断路径不能包含链接。"); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
    }
}
