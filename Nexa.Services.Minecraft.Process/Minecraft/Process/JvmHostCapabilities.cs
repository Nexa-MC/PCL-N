using Nexa.Services.Capabilities;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Per-launch JVM host capabilities. These do not belong in the machine snapshot.</summary>
public static class JvmHostCapabilityCatalog
{
    private const string Provider = "nexa.jvmhost";
    public static readonly CapabilityDefinition<string> JvmArguments = Fact<string>("jvmhost.environment.jvm_args", "JVM 参数");
    public static readonly CapabilityDefinition<string> GameArguments = Fact<string>("jvmhost.environment.game_args", "游戏参数");
    public static readonly CapabilityDefinition<string> Variables = Fact<string>("jvmhost.environment.variables", "环境变量");
    public static readonly CapabilityDefinition<string> WorkingDirectory = Fact<string>("jvmhost.environment.working_directory", "工作目录");
    public static readonly CapabilityDefinition<string> Classpath = Fact<string>("jvmhost.environment.classpath", "Classpath");
    public static readonly CapabilityDefinition<string> NativePath = Fact<string>("jvmhost.environment.native_path", "本机库路径");
    public static readonly CapabilityDefinition<string> Wrapper = Fact<string>("jvmhost.environment.wrapper", "启动包装器");
    public static readonly CapabilityDefinition<bool> Spawn = Action("jvmhost.process.spawn", "启动进程");
    public static readonly CapabilityDefinition<bool> Tree = Action("jvmhost.process.tree", "进程树");
    public static readonly CapabilityDefinition<bool> Wait = Action("jvmhost.process.wait", "等待进程");
    public static readonly CapabilityDefinition<bool> Terminate = Action("jvmhost.process.terminate", "结束进程");
    public static readonly CapabilityDefinition<bool> KillTree = Action("jvmhost.process.kill_tree", "结束进程树");
    public static readonly CapabilityDefinition<bool> Suspend = Action("jvmhost.process.suspend", "暂停进程");
    public static readonly CapabilityDefinition<bool> Resume = Action("jvmhost.process.resume", "恢复进程");
    public static readonly CapabilityDefinition<bool> Priority = Action("jvmhost.process.priority", "进程优先级");
    public static readonly CapabilityDefinition<bool> Affinity = Action("jvmhost.process.affinity", "处理器亲和性");
    public static readonly CapabilityDefinition<bool> CpuSets = Action("jvmhost.process.cpu_sets", "处理器集合");
    public static readonly CapabilityDefinition<bool> Qos = Action("jvmhost.process.qos", "服务质量");
    public static readonly CapabilityDefinition<bool> Stdout = Fact<bool>("jvmhost.io.stdout", "标准输出");
    public static readonly CapabilityDefinition<bool> Stderr = Fact<bool>("jvmhost.io.stderr", "标准错误");
    public static readonly CapabilityDefinition<bool> Stdin = Fact<bool>("jvmhost.io.stdin", "标准输入");
    public static readonly CapabilityDefinition<int> RingBuffer = Fact<int>("jvmhost.io.ring_buffer", "输出环形缓冲", "lines");
    public static readonly CapabilityDefinition<bool> Timestamp = Fact<bool>("jvmhost.io.timestamp", "输出时间戳");
    public static readonly CapabilityDefinition<string> Encoding = Fact<string>("jvmhost.io.encoding", "输出编码");
    public static readonly CapabilityDefinition<bool> CpuMetric = Metric("jvmhost.metric.cpu", "CPU 观测");
    public static readonly CapabilityDefinition<bool> MemoryMetric = Metric("jvmhost.metric.memory", "内存观测");
    public static readonly CapabilityDefinition<bool> CommitMetric = Metric("jvmhost.metric.commit", "提交量观测");
    public static readonly CapabilityDefinition<bool> IoMetric = Metric("jvmhost.metric.io", "I/O 观测");
    public static readonly CapabilityDefinition<bool> ThreadMetric = Metric("jvmhost.metric.threads", "线程观测");
    public static readonly CapabilityDefinition<bool> GpuMetric = Metric("jvmhost.metric.gpu", "GPU 观测");
    public static readonly CapabilityDefinition<bool> ProcessTreeMetric = Metric("jvmhost.metric.process_tree", "进程树观测");
    public static readonly CapabilityDefinition<bool> ExitCode = Fact<bool>("jvmhost.crash.exit_code", "退出码");
    public static readonly CapabilityDefinition<bool> CrashReport = Fact<bool>("jvmhost.crash.crash_report", "崩溃报告");
    public static readonly CapabilityDefinition<bool> HsErr = Fact<bool>("jvmhost.crash.hs_err", "JVM 崩溃文件");
    public static readonly CapabilityDefinition<bool> StdoutTail = Fact<bool>("jvmhost.crash.stdout_tail", "标准输出尾部");
    public static readonly CapabilityDefinition<bool> StderrTail = Fact<bool>("jvmhost.crash.stderr_tail", "错误输出尾部");
    public static readonly CapabilityDefinition<bool> SystemCorrelation = Fact<bool>("jvmhost.crash.system_correlation", "系统事件关联");

    public static IReadOnlyList<ICapability> Describe(MinecraftLaunchPlan plan, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(plan);
        const string source = "JvmHostService";
        bool tunable = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        bool suspendable = tunable || OperatingSystem.IsMacOS();
        var native = new Nexa.Platform.PlatformJvmRuntime();
        JvmHostEnvironment environment = DescribeEnvironment(plan);
        return Array.AsReadOnly<ICapability>(
        [
            JvmArguments.Observe(string.Join(' ', environment.JvmArguments), timestamp, source),
            GameArguments.Observe(string.Join(' ', environment.GameArguments), timestamp, source),
            Variables.Observe(string.Join(';', plan.EnvironmentVariables.Keys.Order(StringComparer.Ordinal)), timestamp, source),
            WorkingDirectory.Observe(environment.WorkingDirectory, timestamp, source),
            Classpath.Observe(string.Join(Path.PathSeparator, plan.ClasspathEntries), timestamp, source),
            NativePath.Observe(plan.NativesDirectory, timestamp, source),
            environment.Wrapper is { } wrapper
                ? Wrapper.Observe(wrapper, timestamp, source)
                : Wrapper.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "未配置启动包装器"),
            Spawn.Observe(true, timestamp, source), Tree.Observe(true, timestamp, source), Wait.Observe(true, timestamp, source),
            Terminate.Observe(true, timestamp, source), KillTree.Observe(true, timestamp, source),
            Supported(Suspend, suspendable, timestamp, source), Supported(Resume, suspendable, timestamp, source),
            Supported(Priority, tunable, timestamp, source), Supported(Affinity, tunable, timestamp, source),
            Supported(CpuSets, native.CpuSetsSupported, timestamp, source),
            Supported(Qos, native.QualitySupported, timestamp, source),
            Stdout.Observe(true, timestamp, source), Stderr.Observe(true, timestamp, source),
            Stdin.Unavailable(CapabilityAvailability.PlatformUnsupported, timestamp, "标准输入由私有 JVM bootstrap 独占，不提供交互式终端"),
            RingBuffer.Observe(100, timestamp, source), Timestamp.Observe(true, timestamp, source),
            Encoding.Observe("UTF-8/system", timestamp, source), CpuMetric.Observe(true, timestamp, source),
            MemoryMetric.Observe(true, timestamp, source),
            Supported(CommitMetric, native.CommitSupported, timestamp, source),
            Supported(IoMetric, OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), timestamp, source), ThreadMetric.Observe(true, timestamp, source),
            native.GpuSupported ? GpuMetric.Observe(true, timestamp, source)
                : GpuMetric.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "GPU 驱动未提供 DRM 进程驻留计数器"),
            Supported(ProcessTreeMetric, native.TreeSupported, timestamp, source),
            ExitCode.Observe(true, timestamp, source), CrashReport.Observe(true, timestamp, source),
            HsErr.Observe(true, timestamp, source), StdoutTail.Observe(true, timestamp, source), StderrTail.Observe(true, timestamp, source),
            Supported(SystemCorrelation, native.SystemEventsSupported, timestamp, source),
        ]);
    }

    private static JvmHostEnvironment DescribeEnvironment(MinecraftLaunchPlan plan)
    {
        int main = plan.Arguments.Count;
        for (int index = 0; index + 2 < plan.Arguments.Count; index++)
            if (plan.Arguments[index] is "-cp" or "-classpath") { main = index + 2; break; }
        return new(plan.JavaExecutablePath, plan.WorkingDirectory, plan.Arguments.Take(main).ToArray(),
            main < plan.Arguments.Count ? plan.Arguments.Skip(main + 1).ToArray() : [], plan.ClasspathEntries,
            plan.NativesDirectory, string.IsNullOrWhiteSpace(plan.WrapperCommand) ? null : plan.WrapperCommand);
    }

    private static CapabilityDefinition<T> Fact<T>(string id, string label, string unit = "") =>
        new(id, label, "JVM Host", Provider, unit: unit);
    private static CapabilityDefinition<bool> Action(string id, string label) =>
        new(id, label, "JVM Host", Provider, CapabilityKind.Action);
    private static CapabilityDefinition<bool> Metric(string id, string label) =>
        new(id, label, "JVM Host", Provider, CapabilityKind.Metric, CapabilityStability.Dynamic);
    private static ICapability Supported(CapabilityDefinition<bool> definition, bool supported,
        DateTimeOffset timestamp, string source) => supported
            ? definition.Observe(true, timestamp, source)
            : definition.Unavailable(CapabilityAvailability.PlatformUnsupported, timestamp, "当前平台不支持此能力");
}

public static class ObservationCapabilityCatalog
{
    private const string Provider = "nexa.jvmhost.observation";
    public static readonly CapabilityDefinition<long> LaunchDuration = Metric("observation.launch.duration", "启动耗时", "ms");
    public static readonly CapabilityDefinition<long> RuntimePeakWorkingSet = Metric("observation.runtime.peak_working_set", "峰值工作集", "bytes");
    public static readonly CapabilityDefinition<long> RuntimePeakPrivate = Metric("observation.runtime.peak_private", "峰值专用内存", "bytes");
    public static readonly CapabilityDefinition<long> RuntimePeakThreads = Metric("observation.runtime.peak_threads", "峰值线程数", "threads");
    public static readonly CapabilityDefinition<long> RuntimeCpu = Metric("observation.runtime.cpu", "CPU 时间", "ms");
    public static readonly CapabilityDefinition<long> LaunchHeapPeak = Metric("observation.launch.heap_peak", "启动前30秒堆峰值", "bytes");
    public static readonly CapabilityDefinition<long> LaunchNativePeak = Metric("observation.launch.native_peak", "启动前30秒本机内存峰值", "bytes");
    public static readonly CapabilityDefinition<long> LaunchPhysicalPeak = Metric("observation.launch.physical_peak", "启动前30秒物理内存峰值", "bytes");
    public static readonly CapabilityDefinition<long> LaunchCommitPeak = Metric("observation.launch.commit_peak", "启动前30秒提交量峰值", "bytes");
    public static readonly CapabilityDefinition<long> LaunchGpuLocalPeak = Metric("observation.launch.gpu_local_peak", "启动前30秒本地显存峰值", "bytes");
    public static readonly CapabilityDefinition<long> LaunchGpuSharedPeak = Metric("observation.launch.gpu_shared_peak", "启动前30秒共享显存峰值", "bytes");
    public static readonly CapabilityDefinition<long> LaunchCpuPeak = Metric("observation.launch.cpu_peak", "启动前30秒 CPU 峰值", "%");
    public static readonly CapabilityDefinition<long> LaunchIoRead = Metric("observation.launch.io_read", "启动前30秒读取量", "bytes");
    public static readonly CapabilityDefinition<long> RuntimeHeapP95 = Metric("observation.runtime.heap_p95", "运行堆 P95", "bytes");
    public static readonly CapabilityDefinition<long> RuntimePhysicalP95 = Metric("observation.runtime.physical_p95", "运行物理内存 P95", "bytes");
    public static readonly CapabilityDefinition<long> RuntimeCommitP95 = Metric("observation.runtime.commit_p95", "运行提交量 P95", "bytes");
    public static readonly CapabilityDefinition<long> RuntimeGpuP95 = Metric("observation.runtime.gpu_p95", "运行 GPU P95", "bytes");
    public static readonly CapabilityDefinition<long> RuntimeCpuP95 = Metric("observation.runtime.cpu_p95", "运行 CPU P95", "%");

    public static IReadOnlyList<ICapability> Project(JvmHostObservation observation, DateTimeOffset timestamp)
    {
        const string source = "JvmHostObservation";
        return Array.AsReadOnly<ICapability>(
        [
            LaunchDuration.Observe(observation.LaunchDurationMilliseconds, timestamp, source),
            MetricOrUnavailable(RuntimePeakWorkingSet, Observed(observation.PeakWorkingSetBytes, observation.CoreMetricsObserved), timestamp, source),
            MetricOrUnavailable(RuntimePeakPrivate, Observed(observation.PeakPrivateBytes, observation.CoreMetricsObserved), timestamp, source),
            MetricOrUnavailable(RuntimePeakThreads, Observed(observation.PeakThreadCount, observation.CoreMetricsObserved), timestamp, source),
            MetricOrUnavailable(RuntimeCpu, Observed(observation.TotalProcessorMilliseconds, observation.CoreMetricsObserved), timestamp, source),
            MetricOrUnavailable(LaunchHeapPeak, LaunchValue(observation, observation.MeasuredLaunchHeapPeakBytes, observation.MeasuredHeapPeakBytes ?? Observed(observation.HeapPeakBytes)), timestamp, source),
            MetricOrUnavailable(LaunchNativePeak, LaunchValue(observation, observation.MeasuredLaunchNativePeakBytes, observation.MeasuredNativePeakBytes ?? Observed(observation.NativePeakBytes)), timestamp, source),
            MetricOrUnavailable(LaunchPhysicalPeak, LaunchValue(observation, observation.MeasuredLaunchPhysicalPeakBytes, Observed(observation.PeakWorkingSetBytes, observation.CoreMetricsObserved)), timestamp, source),
            MetricOrUnavailable(LaunchCommitPeak, LaunchValue(observation, observation.MeasuredLaunchCommitPeakBytes, observation.MeasuredCommitPeakBytes ?? Observed(observation.CommitPeakBytes)), timestamp, source),
            MetricOrUnavailable(LaunchGpuLocalPeak, LaunchValue(observation, observation.MeasuredLaunchGpuLocalPeakBytes, observation.MeasuredGpuLocalPeakBytes ?? Observed(observation.GpuLocalPeakBytes)), timestamp, source),
            MetricOrUnavailable(LaunchGpuSharedPeak, LaunchValue(observation, observation.MeasuredLaunchGpuSharedPeakBytes, observation.MeasuredGpuSharedPeakBytes ?? Observed(observation.GpuSharedPeakBytes)), timestamp, source),
            MetricOrUnavailable(LaunchCpuPeak, LaunchValue(observation, observation.MeasuredLaunchCpuPeakPercent, Observed(observation.CpuPeakPercent, observation.CpuPercentObserved)), timestamp, source),
            MetricOrUnavailable(LaunchIoRead, LaunchValue(observation, observation.MeasuredLaunchIoReadBytes, Observed(observation.IoReadBytes, observation.IoObserved)), timestamp, source),
            MetricOrUnavailable(RuntimeHeapP95, observation.MeasuredRuntimeHeapP95Bytes, timestamp, source),
            MetricOrUnavailable(RuntimePhysicalP95, Observed(observation.RuntimePhysicalP95Bytes, observation.CoreMetricsObserved), timestamp, source),
            MetricOrUnavailable(RuntimeCommitP95, observation.MeasuredRuntimeCommitP95Bytes ?? Observed(observation.RuntimeCommitP95Bytes), timestamp, source),
            MetricOrUnavailable(RuntimeGpuP95, observation.MeasuredRuntimeGpuP95Bytes, timestamp, source),
            MetricOrUnavailable(RuntimeCpuP95, Observed(observation.RuntimeCpuP95Percent, observation.CpuPercentObserved), timestamp, source),
        ]);
    }

    private static CapabilityDefinition<long> Metric(string id, string label, string unit) =>
        new(id, label, "运行观测", Provider, CapabilityKind.Metric, CapabilityStability.Dynamic, unit: unit);
    private static long? Observed(long value, bool measured = false) => measured || value > 0 ? value : null;
    private static long? LaunchValue(JvmHostObservation observation, long? window, long? legacy)
        => observation.LaunchWindowMilliseconds > 0 ? window : legacy;
    private static ICapability MetricOrUnavailable(CapabilityDefinition<long> definition, long? value,
        DateTimeOffset timestamp, string source) => value is { } measured && measured >= 0
            ? definition.Observe(measured, timestamp, source)
            : definition.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "当前 host 未采集此指标");
}
