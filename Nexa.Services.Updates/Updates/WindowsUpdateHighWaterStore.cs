using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Nexa.Platform.Updates;

namespace Nexa.Services.Updates;

/// <summary>Monotonic helper-side state bound to an admitted Windows directory and exclusive file handle.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateHighWaterStore
{
    internal const string StateName = "highest-accepted-version.journal";
    private readonly WindowsUpdateDirectory _directory;

    public WindowsUpdateHighWaterStore(WindowsUpdateDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public string? Read()
    {
        using FileStream state = Acquire();
        return UpdateHighWaterJournal.Read(state).Version;
    }

    public void Advance(string candidateVersion)
    {
        UpdateHighWaterJournal.ParseVersion(candidateVersion);
        using FileStream state = Acquire();
        UpdateHighWaterJournal.Append(state, candidateVersion);
        state.Flush(flushToDisk: true);
    }

    private FileStream Acquire()
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return _directory.OpenExclusiveStateFile(StateName); }
            catch (IOException failure) when (failure.InnerException is Win32Exception { NativeErrorCode: 32 }
                && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(10);
            }
        }
    }
}
