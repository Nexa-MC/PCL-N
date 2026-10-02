using System.Text;

namespace Nexa.Services.Updates;

/// <summary>
/// Durable monotonic release state for use inside a separately protected update helper.
/// The caller remains responsible for proving that <paramref name="protectedDirectory"/> is protected.
/// </summary>
public sealed class UpdateHighWaterStore
{
    private const int MaximumStateBytes = 256;
    private readonly string _statePath;
    private readonly string _lockPath;

    public UpdateHighWaterStore(string protectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedDirectory);
        if (!Path.IsPathFullyQualified(protectedDirectory))
            throw new ArgumentException("The protected update directory must be absolute.", nameof(protectedDirectory));
        _statePath = Path.Combine(protectedDirectory, "highest-accepted-version");
        _lockPath = Path.Combine(protectedDirectory, ".highest-accepted-version.lock");
    }

    public string? Read()
    {
        using FileStream gate = AcquireLock();
        return ReadLocked();
    }

    public void Advance(string candidateVersion)
    {
        UpdateVersion candidate = ParseCanonical(candidateVersion);
        using FileStream gate = AcquireLock();
        string? currentText = ReadLocked();
        if (currentText is not null && candidate <= ParseCanonical(currentText))
            throw new InvalidOperationException("The update high-water version can only advance.");

        string temporary = _statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] payload = Encoding.ASCII.GetBytes(candidateVersion + "\n");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private FileStream AcquireLock()
    {
        string? directory = Path.GetDirectoryName(_statePath);
        if (directory is null || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("The protected update directory must be installed before use.");
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
    }

    private string? ReadLocked()
    {
        if (!File.Exists(_statePath)) return null;
        var info = new FileInfo(_statePath);
        if (info.Length is <= 0 or > MaximumStateBytes)
            throw new InvalidDataException("The update high-water state is invalid.");
        string value = File.ReadAllText(_statePath, Encoding.ASCII);
        if (!value.EndsWith('\n') || value.AsSpan(0, value.Length - 1).ContainsAny('\r', '\n'))
            throw new InvalidDataException("The update high-water state is invalid.");
        string version = value[..^1];
        ParseCanonical(version);
        return version;
    }

    private static UpdateVersion ParseCanonical(string value)
    {
        if (value is not { Length: > 0 and <= 128 } || !UpdateVersion.TryParse(value, out UpdateVersion parsed)
            || parsed.ToString() != value || parsed.Stage == UpdateVersionStage.Ci
            || ((parsed.Stage is UpdateVersionStage.Alpha or UpdateVersionStage.Beta) && parsed.Sequence <= 0))
            throw new InvalidDataException("The update high-water version is not canonical.");
        return parsed;
    }
}
