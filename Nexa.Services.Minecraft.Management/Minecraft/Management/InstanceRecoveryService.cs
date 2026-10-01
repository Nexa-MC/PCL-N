using Nexa.Services.Logging;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Services.Scheduling;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Owns best-effort background capture after a confirmed successful game session.</summary>
public sealed partial class InstanceRecoveryService(SettingsPolicyService settings, XsrStateStore store, LogService? log = null)
{
    public IWorkScheduler? WorkScheduler { get; init; }
    internal Task<bool> RecordSuccessfulExitAsync(MinecraftLaunchPlan plan, MinecraftProcessSnapshot session,
        bool hasCrashEvidence, CancellationToken token = default) => Task.Run(async () =>
    {
        if (!InstanceRecoveryEligibility.CanCapture(session, hasCrashEvidence)
            || plan.MinecraftRootDirectory is not { } root
            || !Nexa.Core.PathIdentity.Comparer.Equals(session.InstanceDirectory, plan.InstanceDirectory)
            || !Nexa.Core.PathIdentity.Comparer.Equals(session.GameDirectory, plan.GameDirectory)) return false;
        try
        {
            using var capture = InstanceRecoveryOperationGate.TryCapture(root);
            if (capture is null) return false;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, capture.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(3));
            var stop = cancellation.Token;
            string instance = Path.GetFullPath(plan.InstanceDirectory), game = Path.GetFullPath(plan.GameDirectory);
            string manifest = Path.Combine(instance, Path.GetFileName(instance) + ".json");
            bool KeepHistory()
            {
                var effective = settings.Read(new(instance));
                if (!effective.IsSuccess) throw new InvalidDataException("无法读取快照保留设置。");
                var value = effective.Value!.Values.Single(item => item.Key == "recovery.keep-history");
                if (value.ValidationError is not null) throw new InvalidDataException("快照保留设置无效。");
                return bool.Parse(value.Value.Value!);
            }
            bool keepHistory = KeepHistory();
            string baselineSettings = settings.CaptureRecoverySettings(instance);
            async Task Validate(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (store.TryResolve(MinecraftProcessStateComposition.SessionsKey, out var id)
                    && store.ReadCollection<MinecraftProcessSnapshot>(id, cancellationToken: ct).Items.Any(item =>
                        item.State is MinecraftProcessState.Created or MinecraftProcessState.Running
                        && (Nexa.Core.PathIdentity.Comparer.Equals(item.InstanceDirectory, instance)
                            || Nexa.Core.PathIdentity.Comparer.Equals(item.GameDirectory, game))))
                    throw new IOException("游戏仍在使用恢复范围。");
                var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, ct).ConfigureAwait(false);
                string expectedGame = metadata.InstanceIsolation ? instance : Path.GetFullPath(root);
                if (!Nexa.Core.PathIdentity.Comparer.Equals(expectedGame, game)
                    || settings.CaptureRecoverySettings(instance) != baselineSettings || KeepHistory() != keepHistory)
                    throw new IOException("采集期间版本设置发生变化。");
            }
            IReadOnlyList<RecoverySource> sources;
            using (IDisposable? admission = WorkScheduler is null ? null : await WorkScheduler.AcquireAsync(
                WorkPriority.Background, WorkResource.Cpu | WorkResource.Disk, stop).ConfigureAwait(false))
            {
                await Validate(stop).ConfigureAwait(false);
                // Verify the artifact actually used by this launch, not just a guessed filename.
                sources = await RecoveryCapturePlan.BuildAsync(root, instance, game, manifest, stop).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(plan.ClientJarPath) || !sources.Any(source =>
                    Nexa.Core.PathIdentity.Comparer.Equals(Path.GetFullPath(Path.Combine(
                        source.Area switch { "instance" => instance, "game" => game, _ => root }, source.RelativePath)), Path.GetFullPath(plan.ClientJarPath))))
                    throw new InvalidDataException("恢复范围缺少本次启动使用的核心文件。");
            }
            await new RecoverySnapshotStore(instance, game, WorkScheduler).CaptureAsync(sources, baselineSettings, async ct =>
            {
                var current = await RecoveryCapturePlan.BuildAsync(root, instance, game, manifest, ct).ConfigureAwait(false);
                if (!sources.SequenceEqual(current)) throw new IOException("采集期间恢复范围发生变化。");
                await Validate(ct).ConfigureAwait(false);
            }, keepHistory, stop).ConfigureAwait(false);
            log?.Info("Recovery", "已保存本次正常退出的恢复基线。");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            log?.Warn("Recovery", $"恢复快照未完成（{error.GetType().Name}），已保留旧基线。");
            return false;
        }
    }, token);
}
