using System.Text;
using Nexa.UI.Next.Backend.Avalonia;

namespace Nexa.UI.Next.Backend.Avalonia.Tests;

internal static partial class Program
{
    private static void DocumentTransfersBoundActualBytesAndReplaceAtomically()
        => VerifyDocumentTransfersAsync().GetAwaiter().GetResult();

    private static async Task VerifyDocumentTransfersAsync()
    {
        await using var oversized = new MemoryStream(new byte[33]);
        bool rejected = false;
        try { await NativeDocumentTransfer.ReadAsync(oversized, 16, CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected); AssertEqual(17L, oversized.Position);
        await using var invalid = new MemoryStream(new byte[] { 0xFF });
        rejected = false;
        try { await NativeDocumentTransfer.ReadAsync(invalid, 16, CancellationToken.None); }
        catch (DecoderFallbackException) { rejected = true; }
        AssertTrue(rejected);
        await using var valid = new MemoryStream(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"语言\":1}")).ToArray());
        AssertEqual("{\"语言\":1}", await NativeDocumentTransfer.ReadAsync(valid, 32, CancellationToken.None));

        string root = Path.Combine(Path.GetTempPath(), "nexa-document-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "settings.json");
        try
        {
            await File.WriteAllTextAsync(target, "original");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            rejected = false;
            try { await NativeDocumentTransfer.WriteAsync(target, "new", 16, canceled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            AssertTrue(rejected); AssertEqual("original", await File.ReadAllTextAsync(target));
            rejected = false;
            try { await NativeDocumentTransfer.WriteAsync(target, "❄❄❄", 8, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected); AssertEqual("original", await File.ReadAllTextAsync(target));
            string existingDirectory = Path.Combine(root, "directory.json"); Directory.CreateDirectory(existingDirectory);
            rejected = false;
            try { await NativeDocumentTransfer.WriteAsync(existingDirectory, "new", 16, CancellationToken.None); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            AssertTrue(rejected); AssertTrue(Directory.Exists(existingDirectory));
            AssertEqual(0, Directory.GetFiles(root, ".nexa-document-*.tmp").Length);
            await NativeDocumentTransfer.WriteAsync(target, "{\"语言\":1}", 32, CancellationToken.None);
            AssertEqual("{\"语言\":1}", await File.ReadAllTextAsync(target));
            AssertEqual(0, Directory.GetFiles(root, ".nexa-document-*.tmp").Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
