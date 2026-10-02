using System.Diagnostics;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

// XSR-751: signed staging never grants caller-controlled executables helper authority.
internal static partial class Program
{
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Launched { get; } = [];
        public void Launch(ProcessStartInfo startInfo) => Launched.Add(startInfo);
    }

    private static PreparedLauncherUpdate SampleUpdate(string directory, bool withPlan)
    {
        string staged = Path.Combine(directory, "staged", "NexaCL.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllBytes(staged, [0x01]);
        string? planPath = null;
        if (withPlan)
        {
            planPath = Path.Combine(directory, "staged", "install-plan.json");
            File.WriteAllText(planPath, "{}");
        }
        var package = new UpdatePackage("2.0.0.alpha.2", "v2.0.0.alpha.2",
            "https://dist.example/pkg", "pkg.zip", "NexaCL.exe", null, null, [],
            "win-x64", "SelfContained", "Release");
        return new PreparedLauncherUpdate(package, Path.Combine(directory, "current", "NexaCL.exe"),
            staged, Path.Combine(directory, "work"), UsedPatch: false)
        { InstallPlanPath = planPath };
    }

    internal static void UpdateSchedulingRefusesCallerControlledExecutable()
    {
        string directory = CreateTempDirectory();
        try
        {
            var launcher = new RecordingLauncher();
            var scheduler = new UpdateRestartScheduler(launcher);
            foreach (bool withPlan in new[] { false, true })
            {
                PreparedLauncherUpdate update = SampleUpdate(directory, withPlan);
                byte[] original = File.ReadAllBytes(update.StagedExecutablePath);
                AssertThrows<NotSupportedException>(() => scheduler.ScheduleInstallAndRestart(update, 4242));
                AssertThrows<NotSupportedException>(() => scheduler.ScheduleInstallOnExit(update, 4242));
                foreach (bool restart in new[] { false, true })
                    AssertThrows<NotSupportedException>(() => UpdateRestartScheduler.CreateReplacementProcess(update, 4242, restart));
                AssertFalse(Directory.Exists(update.WorkDirectory));
                AssertTrue(original.SequenceEqual(File.ReadAllBytes(update.StagedExecutablePath)));
            }
            AssertEqual(0, launcher.Launched.Count);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static void UpdateSchedulingRefusesBeforeInspectingMissingArtifacts()
    {
        string directory = CreateTempDirectory();
        try
        {
            var launcher = new RecordingLauncher();
            var scheduler = new UpdateRestartScheduler(launcher);
            PreparedLauncherUpdate sample = SampleUpdate(directory, withPlan: true);
            foreach (PreparedLauncherUpdate update in new[]
            {
                sample with { StagedExecutablePath = Path.Combine(directory, "missing.exe") },
                sample with { InstallPlanPath = Path.Combine(directory, "gone.json") },
                // Authority must be refused before even malformed paths are inspected.
                sample with { StagedExecutablePath = "\0", WorkDirectory = "\0" },
            })
            {
                AssertThrows<NotSupportedException>(() => scheduler.ScheduleInstallAndRestart(update, 1234));
                AssertThrows<NotSupportedException>(() => scheduler.ScheduleInstallOnExit(update, 1234));
                AssertThrows<NotSupportedException>(() => UpdateRestartScheduler.CreateReplacementProcess(update, 1234, true));
            }
            AssertEqual(0, launcher.Launched.Count);
            AssertFalse(Directory.Exists(sample.WorkDirectory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static void StagedPathHelpersSanitizeVersions()
    {
        string directory = CreateTempDirectory();
        try
        {
            string current = Path.Combine(directory, "NexaCL.exe");
            string staged = UpdateStaging.BuildStagedPath(current, "2.0.0.alpha.1");
            AssertTrue(staged.StartsWith(directory, StringComparison.Ordinal));
            AssertTrue(staged.EndsWith(".NexaCL.exe.2.0.0.alpha.1.update", StringComparison.Ordinal));
            string sanitized = UpdateStaging.SanitizeFileName("v1:bad/name?x");
            AssertFalse(sanitized.Contains('/', StringComparison.Ordinal));
            AssertTrue(sanitized.Contains("v1", StringComparison.Ordinal));
            AssertTrue(sanitized.Contains("name", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
