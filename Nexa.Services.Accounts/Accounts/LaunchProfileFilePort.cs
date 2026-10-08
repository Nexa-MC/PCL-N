using System.Collections.Concurrent;
using System.Text.Json;
using Nexa.Services.Files;

namespace Nexa.Services.Accounts;

/// <summary>
/// JSON file port for the legacy launch profile store: a `schemaVersion` plus the profile
/// list, camelCase and indented. Unreadable or unsupported-schema files are quarantined next
/// to themselves (`profiles.invalid`) and then surface as <see cref="IOException"/>; writes
/// are atomic — a write-through temporary file replaced with bounded retries. Only the
/// current schema can be saved.
/// </summary>
public sealed class LaunchProfileFilePort : ILaunchProfilePort
{
    public const int SupportedSchemaVersion = LaunchProfileSet.CurrentSchemaVersion;

    private const int ReplaceAttemptCount = 6;

    private const string QuarantineSuffix = ".invalid";

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _path;

    public LaunchProfileFilePort(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public string QuarantinePath => _path + QuarantineSuffix;

    public LaunchProfileSet Load()
    {
        lock (PathLocks.GetOrAdd(_path, static _ => new object()))
        {
            if (!File.Exists(_path))
            {
                return new LaunchProfileSet();
            }

            try
            {
                LaunchProfileSet? profiles;
                using (FileStream stream = new(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan))
                {
                    profiles = JsonSerializer.Deserialize(stream, LaunchProfileJsonContext.Default.LaunchProfileSet);
                }

                if (profiles is null)
                {
                    throw new InvalidDataException("The launch profile file is empty.");
                }

                if (profiles.SchemaVersion is <= 0 or > SupportedSchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported launch profile schema: {profiles.SchemaVersion}.");
                }

                return profiles;
            }
            catch (Exception failure) when (failure is JsonException or InvalidDataException)
            {
                Quarantine();
                throw new IOException($"The launch profile file '{_path}' is unreadable: {failure.Message}", failure);
            }
        }
    }

    public void Save(LaunchProfileSet profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.SchemaVersion != SupportedSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profiles),
                profiles.SchemaVersion,
                "Only the current launch profile schema can be saved.");
        }

        lock (PathLocks.GetOrAdd(_path, static _ => new object()))
        {
            AtomicFileWriter.Write(
                _path,
                "launch profile",
                stream => JsonSerializer.Serialize(stream, profiles, LaunchProfileJsonContext.Default.LaunchProfileSet),
                ReplaceAttemptCount,
                static attempt => TimeSpan.FromMilliseconds(25 * attempt),
                retryUnauthorizedAccess: true);
        }
    }

    private void Quarantine()
    {
        try
        {
            File.Copy(_path, QuarantinePath, overwrite: true);
        }
        catch (IOException)
        {
            // Loading valid profiles matters more than persisting the quarantine copy.
        }
    }
}
