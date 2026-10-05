using System.Diagnostics;
using Nexa.Services.Processes;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static ProcessStartInfo OwnedTestStart(string argument)
    {
        var start = OwnedInstallerProcess.WorkerStartInfo();
        start.ArgumentList[^1] = argument;
        start.WorkingDirectory = Environment.CurrentDirectory;
        return start;
    }

    private static async Task<int> RunOwnedInstallerChild()
    {
        Console.WriteLine(Environment.ProcessId); Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> RunOwnedInstallerOwner()
    {
        using var worker = Process.Start(OwnedInstallerProcess.WorkerStartInfo())!;
        await OwnedInstallerProcess.WriteRequestAsync(worker.StandardInput.BaseStream, OwnedTestStart("--owned-installer-child"));
        string child = (await worker.StandardOutput.ReadLineAsync())!;
        Console.WriteLine(worker.Id + ":" + child); Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async ValueTask OwnedInstallerDiesAfterOwnerIsKilled()
    {
        using var owner = Process.Start(OwnedTestStart("--owned-installer-owner"))!;
        Process? worker = null, child = null;
        OwnedInstallerLinuxSnapshot? workerIdentity = null, childIdentity = null;
        try
        {
            string[] ids = (await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)))!.Split(':');
            worker = Process.GetProcessById(int.Parse(ids[0], System.Globalization.CultureInfo.InvariantCulture));
            child = Process.GetProcessById(int.Parse(ids[1], System.Globalization.CultureInfo.InvariantCulture));
            AssertFalse(child.HasExited);
            if (OperatingSystem.IsLinux())
            {
                workerIdentity = ReadOwnedInstallerLinuxSnapshot(worker.Id) ?? throw new InvalidOperationException("Installer worker exited before owner-loss verification.");
                childIdentity = ReadOwnedInstallerLinuxSnapshot(child.Id) ?? throw new InvalidOperationException("Installer child exited before owner-loss verification.");
                AssertFalse(OwnedInstallerLinuxIdentityTerminated(workerIdentity.Value));
                AssertFalse(OwnedInstallerLinuxIdentityTerminated(childIdentity.Value));
            }
            owner.Kill(); // Deliberately do NOT kill the process tree: only pipe EOF can stop the child.
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (workerIdentity is { } workerBefore && childIdentity is { } childBefore)
            {
                // These are not children of this harness. Linux nonparent Process waits may
                // remain stale after /proc disappearance; PID 1 may also retain dead zombies.
                // Verify the original birth identity cannot execute, rather than its reaping.
                await WaitOwnedInstallerLinuxIdentityTerminatedAsync(workerBefore);
                await WaitOwnedInstallerLinuxIdentityTerminatedAsync(childBefore);
                AssertTrue(OwnedInstallerLinuxIdentityTerminated(childBefore));
            }
            else
            {
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                AssertTrue(child.HasExited);
            }
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            KillOwnedInstallerFixtureProcess(worker, workerIdentity);
            KillOwnedInstallerFixtureProcess(child, childIdentity);
            worker?.Dispose(); child?.Dispose();
        }
    }

    private readonly record struct OwnedInstallerLinuxSnapshot(int ProcessId, ulong StartTime, char State);

    private static OwnedInstallerLinuxSnapshot? ReadOwnedInstallerLinuxSnapshot(int processId)
    {
        string stat;
        try { stat = File.ReadAllText($"/proc/{processId}/stat"); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        int firstSpace = stat.IndexOf(' '), commandEnd = stat.LastIndexOf(')');
        if (firstSpace < 1 || commandEnd <= firstSpace
            || !int.TryParse(stat.AsSpan(0, firstSpace), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int observedId) || observedId != processId)
            throw new InvalidDataException("Malformed installer fixture process identity.");
        // The comm field is parenthesized and may contain spaces; field 22 is starttime.
        string[] fields = stat[(commandEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || fields[0].Length != 1
            || !ulong.TryParse(fields[19], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong startTime))
            throw new InvalidDataException("Malformed installer fixture process state.");
        return new(processId, startTime, fields[0][0]);
    }

    private static bool OwnedInstallerLinuxIdentityTerminated(OwnedInstallerLinuxSnapshot original)
    {
        var current = ReadOwnedInstallerLinuxSnapshot(original.ProcessId);
        return current is null || current.Value.StartTime > original.StartTime
            || current.Value.StartTime == original.StartTime && current.Value.State is ('Z' or 'X');
    }

    private static async ValueTask WaitOwnedInstallerLinuxIdentityTerminatedAsync(OwnedInstallerLinuxSnapshot original)
    {
        long started = Stopwatch.GetTimestamp();
        while (!OwnedInstallerLinuxIdentityTerminated(original))
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                throw new TimeoutException($"Installer fixture process {original.ProcessId} remains alive with its original birth identity.");
            await Task.Delay(10);
        }
    }

    private static void KillOwnedInstallerFixtureProcess(Process? process, OwnedInstallerLinuxSnapshot? identity)
    {
        if (process is null) return;
        if (OperatingSystem.IsLinux())
        {
            // A reused PID belongs to another process and must never be killed by cleanup.
            if (identity is not { } original || ReadOwnedInstallerLinuxSnapshot(original.ProcessId) is not { } current
                || current.StartTime != original.StartTime || current.State is 'Z' or 'X') return;
            process.Kill(entireProcessTree: true);
        }
        else if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
}
