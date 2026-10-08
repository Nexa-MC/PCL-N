using Nexa.Services.Files;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    internal static ValueTask AtomicFilePublicationPreservesOriginalsAndRetiresScratchOnFailure()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "state.json");
            byte[] original = [1, 2, 3, 4, 5];
            File.WriteAllBytes(path, original);
            var serializationFailure = new InvalidDataException("Serialization failed after writing partial content.");
            InvalidDataException? observedSerializationFailure = null;
            try
            {
                AtomicFileWriter.Write(path, "test state", stream =>
                {
                    stream.Write([9, 8]);
                    throw serializationFailure;
                }, replaceAttempts: 1, static _ => TimeSpan.Zero);
            }
            catch (InvalidDataException failure)
            {
                observedSerializationFailure = failure;
            }

            AssertTrue(ReferenceEquals(serializationFailure, observedSerializationFailure));
            AssertTrue(File.ReadAllBytes(path).AsSpan().SequenceEqual(original));
            AssertEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count());

            // A directory at the destination deterministically refuses replacement on every platform.
            string blockedPath = Path.Combine(directory, "blocked.json");
            Directory.CreateDirectory(blockedPath);
            string retainedPath = Path.Combine(blockedPath, "retained.txt");
            File.WriteAllText(retainedPath, "retained");
            int writes = 0, retries = 0;
            IOException? observedReplacementFailure = null;
            try
            {
                AtomicFileWriter.Write(blockedPath, "test state", stream =>
                {
                    writes++;
                    stream.Write([9]);
                }, replaceAttempts: 2, attempt =>
                {
                    AssertEqual(1, attempt);
                    retries++;
                    return TimeSpan.Zero;
                }, retryUnauthorizedAccess: true);
            }
            catch (IOException failure)
            {
                observedReplacementFailure = failure;
            }

            AssertTrue(observedReplacementFailure is not null);
            AssertTrue(observedReplacementFailure!.InnerException is IOException or UnauthorizedAccessException);
            AssertTrue(observedReplacementFailure.Message.Contains("after 2 attempts", StringComparison.Ordinal));
            AssertEqual(1, writes);
            AssertEqual(1, retries);
            AssertEqual("retained", File.ReadAllText(retainedPath));
            AssertEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count());

            AtomicFileWriter.Write(path, "test state", static stream => stream.Write([9]),
                replaceAttempts: 1, static _ => TimeSpan.Zero);
            AssertTrue(File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 9 }));
            AssertEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
