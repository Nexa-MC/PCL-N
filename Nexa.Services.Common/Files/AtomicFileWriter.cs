namespace Nexa.Services.Files;

/// <summary>Shared write-through temporary-file publication; formats and retry policy stay with the owner.</summary>
internal static class AtomicFileWriter
{
    internal static void Write(
        string path,
        string fileDescription,
        Action<FileStream> write,
        int replaceAttempts,
        Func<int, TimeSpan> retryDelay,
        bool retryUnauthorizedAccess = false)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replaceAttempts);
        ArgumentNullException.ThrowIfNull(retryDelay);

        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"The {fileDescription} path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        bool replaced = false;
        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                write(stream);
            }

            Exception? lastFailure = null;
            for (int attempt = 1; attempt <= replaceAttempts; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, overwrite: true);
                    replaced = true;
                    return;
                }
                catch (Exception failure) when (failure is IOException
                    || retryUnauthorizedAccess && failure is UnauthorizedAccessException)
                {
                    lastFailure = failure;
                    if (attempt < replaceAttempts)
                    {
                        Thread.Sleep(retryDelay(attempt));
                    }
                }
            }

            throw new IOException(
                $"Unable to replace {fileDescription} file '{path}' after {replaceAttempts} attempts.",
                lastFailure);
        }
        finally
        {
            if (!replaced)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception failure) when (failure is IOException
                    || retryUnauthorizedAccess && failure is UnauthorizedAccessException)
                {
                    // Preserve the original save exception with the owner's cleanup policy.
                }
            }
        }
    }
}
