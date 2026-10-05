using System.Diagnostics;

using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Process;

public interface IJvmHost
{
    JvmHostEnvironment Describe(MinecraftLaunchPlan plan);
    ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId,
        CancellationToken cancellationToken = default);
    JvmHostControlResult Suspend(MinecraftProcessSession session);
    JvmHostControlResult ResumeProcess(MinecraftProcessSession session);
    JvmHostControlResult SetPriority(MinecraftProcessSession session, ProcessPriorityClass priority);
    JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask);
}

/// <summary>
/// Formal JVM process boundary. It retains the proven Minecraft process lifecycle and adds a
/// typed environment query plus bounded observations without exposing process mechanics to the
/// launch coordinator.
/// </summary>
public sealed class JvmHostService : IJvmHost
{
    private readonly MinecraftProcessService _processes;
    private readonly XsrStateStore? _store;
    private readonly XsrStateId _observationsId;
    private readonly ResourceObservationHistory? _history;
    private readonly Management.InstanceRecoveryService? _recovery;

    public JvmHostService(MinecraftProcessService processes, ResourceObservationHistory? history = null,
        Management.InstanceRecoveryService? recovery = null, Nexa.Platform.IPlatformProcessControl? control = null)
    {
        _control = control ?? new Nexa.Platform.PlatformProcessControl();
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _store = processes.StateStore;
        _history = history;
        _recovery = recovery;
        if (_store is not null) _observationsId = _store.Resolve(JvmHostStateContract.ObservationsKey);
    }

    public JvmHostEnvironment Describe(MinecraftLaunchPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int mainClass = plan.MainClassIndex ?? FindMainClass(plan.Arguments);
        if (mainClass < 0 || mainClass > plan.Arguments.Count
            || (plan.MainClassIndex.HasValue && mainClass == plan.Arguments.Count))
            throw new ArgumentException("Invalid main-class boundary.", nameof(plan));
        string[] jvm = plan.Arguments.Take(mainClass).ToArray();
        string[] game = mainClass < plan.Arguments.Count ? plan.Arguments.Skip(mainClass + 1).ToArray() : [];
        return new(plan.JavaExecutablePath, plan.WorkingDirectory, jvm, game, plan.ClasspathEntries,
            plan.NativesDirectory, string.IsNullOrWhiteSpace(plan.WrapperCommand) ? null : plan.WrapperCommand)
        { MainClass = mainClass < plan.Arguments.Count ? plan.Arguments[mainClass] : null };
    }

    public static IReadOnlyList<Capabilities.ICapability> DescribeCapabilities(MinecraftLaunchPlan plan,
        DateTimeOffset? timestamp = null) => JvmHostCapabilityCatalog.Describe(plan, timestamp ?? DateTimeOffset.UtcNow);

    public async ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId,
        CancellationToken cancellationToken = default)
    {
        long started = Environment.TickCount64;
        _ = Describe(plan); // validate and freeze the environment view before spawning.
        MinecraftProcessSession session;
        using (var recoveryOperation = plan.MinecraftRootDirectory is { } root
            ? await Management.InstanceRecoveryOperationGate.EnterOperationAsync(Path.GetFullPath(root), cancellationToken).ConfigureAwait(false) : null)
            session = await _processes.StartAsync(plan, instanceId, cancellationToken).ConfigureAwait(false);
        long launchDuration = Math.Max(0, Environment.TickCount64 - started);
        Task<JvmRunContext?> context = CollectContextAsync(session, plan);
        _ = ObserveAsync(session, plan, launchDuration, context);
        return session;
    }

    private readonly Nexa.Platform.IPlatformProcessControl _control;
    private static JvmHostControlResult Contract(Nexa.Platform.PlatformProcessControlResult result)
        => new(result.Succeeded, result.Code, result.Message);
    public JvmHostControlResult Suspend(MinecraftProcessSession session) => Contract(_control.Suspend(session.Process, true));
    public JvmHostControlResult ResumeProcess(MinecraftProcessSession session) => Contract(_control.Suspend(session.Process, false));
    public JvmHostControlResult SetPriority(MinecraftProcessSession session, ProcessPriorityClass priority)
        => Contract(_control.SetPriority(session.Process, priority));
    public JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask)
        => Contract(_control.SetAffinity(session.Process, affinityMask));

    private async Task ObserveAsync(MinecraftProcessSession session, MinecraftLaunchPlan plan, long launchDuration,
        Task<JvmRunContext?> contextTask)
    {
        long peakWorking = 0, peakPrivate = 0, peakThreads = 0, cpuMs = 0, ioRead = 0, ioWrite = 0;
        RunResourceHistogram workingSamples = new();
        long[] cpuSamples = new long[101];
        JvmRunWindow window = new();
        long sequence = 0, runStart = Environment.TickCount64, windowStart = runStart;
        int epoch = 0;
        long successfulSamples = 0;
        bool samplingComplete = true;
        string? settingsFingerprint = GameOptionsFingerprint.Read(plan.GameDirectory);
        bool settingsFileStable = settingsFingerprint is not null;
        JvmRunSettings settings = JvmRunSettings.Read(plan.GameDirectory);
        void Emit(bool ended)
        {
            long now = Environment.TickCount64;
            PublishSample(window.Finish(session.Snapshot.SessionId, sequence++, now - runStart, now - windowStart,
                epoch, settings, plan.JavaMajorVersion, plan.ModLoader.Kind.ToString(), plan.ClasspathEntries.Count,
                plan.HeapLimitMiB, ended, ended ? session.Snapshot.ExitCode : null));
            windowStart = now;
        }
        TimeSpan previousCpu = TimeSpan.Zero;
        long previousSampleTick = Environment.TickCount64;
        bool hasCpuBaseline = false;
        try
        {
            while (session.Snapshot.State is MinecraftProcessState.Created or MinecraftProcessState.Running)
            {
                try
                {
                    Nexa.Platform.PlatformProcessSample sample = _control.ReadSample(session.Process);
                    peakWorking = Math.Max(peakWorking, sample.WorkingSetBytes);
                    peakPrivate = Math.Max(peakPrivate, sample.PrivateBytes);
                    peakThreads = Math.Max(peakThreads, sample.ThreadCount);
                    TimeSpan currentCpu = sample.CpuTime;
                    cpuMs = Math.Max(cpuMs, (long)currentCpu.TotalMilliseconds);
                    long currentSampleTick = Environment.TickCount64;
                    workingSamples.Add(sample.WorkingSetBytes);
                    double? sampleCpu = null;
                    if (hasCpuBaseline)
                    {
                        long elapsed = Math.Max(1, currentSampleTick - previousSampleTick);
                        long cpuPercent = (long)Math.Clamp(
                            (currentCpu - previousCpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100,
                            0, 100);
                        cpuSamples[cpuPercent]++;
                        sampleCpu = cpuPercent;
                    }
                    previousCpu = currentCpu;
                    previousSampleTick = currentSampleTick;
                    hasCpuBaseline = true;
                    window.Add(sample.WorkingSetBytes, sample.PrivateBytes, sampleCpu, sample.ThreadCount);
                    successfulSamples++;
                    ioRead = Math.Max(ioRead, sample.IoReadBytes);
                    ioWrite = Math.Max(ioWrite, sample.IoWriteBytes);
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    hasCpuBaseline = false;
                    samplingComplete = false;
                }
                if (Environment.TickCount64 - windowStart >= 30000)
                {
                    Emit(false);
                    JvmRunSettings currentSettings = JvmRunSettings.Read(plan.GameDirectory);
                    settingsFileStable &= settingsFingerprint == GameOptionsFingerprint.Read(plan.GameDirectory);
                    if (currentSettings != settings) { settings = currentSettings; epoch++; }
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            await session.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            // Observation is best effort and cannot change launch truth.
            samplingComplete = false;
        }

        MinecraftProcessSnapshot snapshot = session.Snapshot;
        long observedMilliseconds = Environment.TickCount64 - runStart;
        Emit(snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running));
        (string[] stdout, string[] stderr) = await session.ReadSeparatedEvidenceAsync().ConfigureAwait(false);
        DateTimeOffset evidenceFloor = snapshot.StartedAt - TimeSpan.FromSeconds(2);
        string? hsErr = FindNewestFile(plan.WorkingDirectory,
            $"hs_err_pid{session.Process.Id}.log", evidenceFloor);
        string? crashReport = FindNewestFile(Path.Combine(plan.WorkingDirectory, "crash-reports"),
            "*.txt", evidenceFloor);
        JvmHostObservation observation = new(snapshot.SessionId, snapshot.InstanceId, snapshot.StartedAt,
            snapshot.EndedAt, launchDuration, peakWorking, peakPrivate, peakThreads, cpuMs,
            0, 0, 0, 0, 0, ioRead, ioWrite, crashReport, hsErr, snapshot.ExitCode,
            stdout.TakeLast(40).ToArray(), stderr.TakeLast(40).ToArray())
        {
            CpuPeakPercent = Array.FindLastIndex(cpuSamples, static count => count > 0) is int cpuPeak && cpuPeak >= 0 ? cpuPeak : 0,
            RuntimePhysicalP95Bytes = workingSamples.P95(),
            RuntimeCommitP95Bytes = 0,
            RuntimeCpuP95Percent = CpuPercentile(cpuSamples),
        };
        Publish(observation);
        ResourceObservationSample? historySample = CreateHistorySample(plan, snapshot, observation,
            observedMilliseconds, successfulSamples, samplingComplete,
            epoch == 0 && JvmRunSettings.Read(plan.GameDirectory) == settings && settingsFileStable
                && settingsFingerprint == GameOptionsFingerprint.Read(plan.GameDirectory));
        if (_recovery is not null)
            await _recovery.RecordSuccessfulExitAsync(plan, snapshot, crashReport is not null || hsErr is not null).ConfigureAwait(false);
        if (historySample is not null && await contextTask.ConfigureAwait(false) is { } context
            && ModInventoryFingerprint.Create(context.Inventory) is { } fingerprint)
        {
            try
            {
                using var verify = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var finalInventory = await LaunchModInventoryReader.ReadAsync(plan.GameDirectory, verify.Token).ConfigureAwait(false);
                if (fingerprint == ModInventoryFingerprint.Create(finalInventory))
                    _history?.Record(historySample with { ModFingerprint = fingerprint, SettingsFingerprint = settingsFingerprint });
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
            { /* Incomplete post-run identity must not calibrate future launches. */ }
        }
    }

    internal static ResourceObservationSample? CreateHistorySample(MinecraftLaunchPlan plan,
        MinecraftProcessSnapshot snapshot, JvmHostObservation observation, long elapsedMilliseconds,
        long successfulSamples, bool samplingComplete, bool settingsStable)
    {
        if (snapshot.State != MinecraftProcessState.Exited || snapshot.ExitCode != 0
            || observation.ExitCode != 0 || snapshot.EndedAt is null || elapsedMilliseconds < 60000
            || successfulSamples < 30 || !samplingComplete || !settingsStable
            || observation.CrashReportPath is not null || observation.HsErrPath is not null
            || observation.PeakWorkingSetBytes <= 0) return null;
        return new(plan.InstanceDirectory, plan.ModLoader.Kind.ToString(), plan.JavaMajorVersion,
            -1, -1, 0, 0, BytesToMiB(observation.PeakWorkingSetBytes), 0, 0,
            observation.LaunchDurationMilliseconds, snapshot.EndedAt.Value);
    }

    private void PublishSample(JvmRunSample sample)
    {
        if (_store is null) return;
        XsrStateId id = _store.Resolve(JvmHostStateContract.SamplesKey);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var current = _store.ReadCollection<JvmRunSample>(id);
            string[] removals = current.Items.Take(Math.Max(0, current.Items.Count - 63)).Select(static item => item.Key).ToArray();
            if (_store.PublishDelta(id, new XsrCollectionDelta<JvmRunSample, string>(current.Revision, [sample], removals)).IsApplied) return;
        }
    }

    private async Task<JvmRunContext?> CollectContextAsync(MinecraftProcessSession session, MinecraftLaunchPlan plan)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var inventory = await Task.Run(() => LaunchModInventoryReader.ReadAsync(plan.GameDirectory, timeout.Token), timeout.Token).ConfigureAwait(false);
            string game = "unknown"; var components = new Dictionary<string, string>(StringComparer.Ordinal);
            if (plan.MinecraftRootDirectory is { } root)
            {
                try
                {
                    var edit = await Install.MinecraftInstallEditService.ReadAsync(new(root, session.Snapshot.InstanceId), timeout.Token).ConfigureAwait(false);
                    game = LaunchModInventoryReader.SafeGameVersion(edit.GameVersion) ? edit.GameVersion : "unknown";
                    foreach (var component in edit.Selection)
                        if (LaunchModInventoryReader.SafeVersion(component.Version)) components[component.Loader.ToString()] = component.Version;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
            var context = new JvmRunContext(session.Snapshot.SessionId, plan.ModLoader.Kind.ToString(),
                components.GetValueOrDefault(plan.ModLoader.Kind.ToString()) ?? "unknown", inventory)
            { GameVersion = game, Components = components };
            if (_store is null) return context;
            var id = _store.Resolve(JvmHostStateContract.ContextsKey);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var current = _store.ReadCollection<JvmRunContext>(id);
                var removals = current.Items.Take(Math.Max(0, current.Items.Count - 7)).Select(static item => item.SessionId).ToArray();
                if (_store.PublishDelta(id, new XsrCollectionDelta<JvmRunContext, Guid>(current.Revision, [context], removals)).IsApplied) return context;
            }
            return context;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException) { }
        return null;
    }

    private void Publish(JvmHostObservation observation)
    {
        if (_store is null) return;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            XsrCollectionSnapshot<JvmHostObservation> current = _store.ReadCollection<JvmHostObservation>(_observationsId);
            Guid[] removals = current.Items.OrderBy(static item => item.StartedAt)
                .Take(Math.Max(0, current.Items.Count - 31)).Select(static item => item.SessionId).ToArray();
            if (_store.PublishDelta(_observationsId,
                new XsrCollectionDelta<JvmHostObservation, Guid>(current.Revision, [observation], removals)).IsApplied) return;
        }
    }

    private static int FindMainClass(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index + 2 < arguments.Count; index++)
            if (arguments[index] is "-cp" or "-classpath") return index + 2;
        return arguments.Count;
    }

    private static long BytesToMiB(long bytes) => Math.Max(0, bytes / (1024 * 1024));
    private static long CpuPercentile(long[] bins)
    {
        long target = (long)Math.Ceiling(bins.Sum() * .95), count = 0;
        for (int i = 0; i < bins.Length; i++) { count += bins[i]; if (count >= target) return i; }
        return 0;
    }
    private static string? FindNewestFile(string directory, string pattern, DateTimeOffset notBefore)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern)
                .Where(path => File.GetLastWriteTimeUtc(path) >= notBefore.UtcDateTime)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

}
