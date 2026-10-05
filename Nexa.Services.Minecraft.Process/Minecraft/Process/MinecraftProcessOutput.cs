using Nexa.Services.Logging;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Process;

public sealed partial class MinecraftProcessSession
{
    internal const int MaximumOutputEntries = 512;
    internal const int MaximumOutputLineLength = 2048;
    private readonly Queue<MinecraftProcessOutputEntry> _output = new();
    private long _outputRevision, _droppedOutputEntries;

    private void AddOutput(MinecraftProcessOutputChannel source, string line, bool overflow)
    {
        // Do not expose continuation fragments of a long secret-bearing line. The crash
        // evidence queues keep their existing behavior; the viewer omits an oversized line.
        string text = overflow ? "[日志行超过 2048 字符，已省略]" : LogRedactor.Redact(line);
        lock (_gate)
        {
            if (_output.Count == MaximumOutputEntries) { _output.Dequeue(); _droppedOutputEntries++; }
            _output.Enqueue(new(++_outputRevision, source, text));
        }
    }

    internal async ValueTask<MinecraftProcessOutputSnapshot> ReadOutputAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Snapshot.State is not (MinecraftProcessState.Created or MinecraftProcessState.Running))
        {
            // Descendants can hold the pipes open. Final reads stay bounded and cancellable.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try { await Task.WhenAll(_outputDrain, _errorDrain).WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        lock (_gate)
            return new(_snapshot.SessionId, _snapshot.InstanceId, _snapshot.State, _outputRevision,
                _droppedOutputEntries, Array.AsReadOnly(_output.ToArray()));
    }
}

public sealed partial class MinecraftProcessService
{
    internal async ValueTask<XsrResult<MinecraftProcessOutputSnapshot>> ReadOutputAsync(Guid sessionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PruneSessions();
        if (!_sessions.TryGetValue(sessionId, out var session))
            return XsrResult.Failure<MinecraftProcessOutputSnapshot>(
                MinecraftErrors.InvalidRequest("这次运行的日志已过期，或该进程不由启动器管理。"));
        return XsrResult.Success(await session.ReadOutputAsync(token).ConfigureAwait(false));
    }
}
