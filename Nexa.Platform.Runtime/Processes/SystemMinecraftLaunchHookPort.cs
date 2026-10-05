using System.Buffers.Binary;
using System.Diagnostics;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Owns a hook process group/Job independently of the shell's exit.</summary>
public sealed class SystemMinecraftLaunchHookPort : IMinecraftLaunchHookPort
{
    public async ValueTask<IMinecraftLaunchHookSession> StartAsync(string command, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] request = MinecraftLaunchHookWorker.CreateRequest(command, Path.GetFullPath(workingDirectory));
        System.Diagnostics.Process process = new() { StartInfo = MinecraftLaunchHookWorker.CreateStartInfo() };
        MinecraftLaunchHookWorker.WindowsJob? job = null;
        Session? session = null;
        bool started = false;
        try
        {
            if (OperatingSystem.IsWindows()) job = new();
            if (!process.Start()) throw new IOException("The pre-launch command worker could not be started.");
            started = true;
            if (job is not null)
            {
                job.Assign(process);
                BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(4, 8), job.DuplicateTo(process));
            }
            session = new(process, job);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await session.Ready.WaitAsync(startup.Token).ConfigureAwait(false);
                await MinecraftLaunchHookWorker.WriteRequestAsync(process.StandardInput.BaseStream, request, startup.Token).ConfigureAwait(false);
                await session.Started.WaitAsync(startup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException("The pre-launch worker startup timed out."); }
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            else
            {
                try
                {
                    if (started)
                    {
                        job?.Terminate();
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally { job?.Dispose(); process.Dispose(); }
            }
            throw;
        }
        finally { request.AsSpan().Clear(); }
    }

    internal static int GetWorkerProcessId(IMinecraftLaunchHookSession session)
        => session is Session owned ? owned.WorkerProcessId : throw new ArgumentException("Unknown hook session.", nameof(session));

    private sealed class Session : IMinecraftLaunchHookSession
    {
        private readonly System.Diagnostics.Process _process;
        private readonly MinecraftLaunchHookWorker.WindowsJob? _job;
        private readonly Stream _owner;
        private readonly CancellationTokenSource _drainLifetime = new();
        private readonly Task _errorDrain;
        private readonly Task _physicalExit;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<int> _shellExit;
        private int _ownership;

        public Session(System.Diagnostics.Process process, MinecraftLaunchHookWorker.WindowsJob? job)
        {
            _process = process; _job = job; WorkerProcessId = process.Id;
            _owner = process.StandardInput.BaseStream;
            _errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, _drainLifetime.Token);
            _physicalExit = process.WaitForExitAsync(CancellationToken.None);
            _shellExit = ReadStatusAsync(process.StandardOutput.BaseStream);
        }

        public int WorkerProcessId { get; }
        public Task Ready => _ready.Task;
        public Task Started => _started.Task;

        private async Task<int> ReadStatusAsync(Stream stream)
        {
            try
            {
                if (await MinecraftLaunchHookWorker.ReadInt32Async(stream).ConfigureAwait(false) != MinecraftLaunchHookWorker.ReadyMagic)
                    throw new IOException("The pre-launch worker did not establish process ownership.");
                _ready.TrySetResult();
                if (await MinecraftLaunchHookWorker.ReadInt32Async(stream).ConfigureAwait(false) != MinecraftLaunchHookWorker.StartedMagic)
                    throw new IOException("The pre-launch command could not be started.");
                _started.TrySetResult();
                if (await MinecraftLaunchHookWorker.ReadInt32Async(stream).ConfigureAwait(false) != MinecraftLaunchHookWorker.ExitMagic)
                    throw new IOException("The pre-launch worker exit message was invalid.");
                return await MinecraftLaunchHookWorker.ReadInt32Async(stream).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                _ready.TrySetException(error); _started.TrySetException(error); throw;
            }
        }

        public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
            => await _shellExit.WaitAsync(cancellationToken).ConfigureAwait(false);

        public void Detach(Action<int>? exited = null)
        {
            if (Interlocked.CompareExchange(ref _ownership, 1, 0) != 0)
                throw new InvalidOperationException("Pre-launch command ownership was already transferred.");
            try
            {
                _job?.SetKillOnClose(false);
                _owner.WriteByte(MinecraftLaunchHookWorker.DetachOperation);
                _owner.Flush();
                _owner.Dispose();
                _ = ReapAsync(exited);
            }
            catch
            {
                // A failed handoff stays owned. Disposal still terminates the whole Job/group.
                try { _job?.SetKillOnClose(true); }
                finally { Interlocked.Exchange(ref _ownership, 0); }
                throw;
            }
        }

        private async Task ReapAsync(Action<int>? exited)
        {
            try
            {
                int code = await _shellExit.ConfigureAwait(false);
                try { exited?.Invoke(code); }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
            finally
            {
                try { await _physicalExit.ConfigureAwait(false); }
                finally { await ReleaseAsync().ConfigureAwait(false); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _ownership, 2, 0) != 0) return;
            try
            {
                // The worker remains alive after shell exit, so the group identity is retained.
                try
                {
                    if (_job is not null) _job.Terminate();
                    else MinecraftLaunchHookWorker.KillGroup(WorkerProcessId);
                }
                finally
                {
                    _owner.Dispose();
                    await _physicalExit.ConfigureAwait(false);
                }
            }
            finally { await ReleaseAsync().ConfigureAwait(false); }
        }

        private async ValueTask ReleaseAsync()
        {
            try
            {
                await _drainLifetime.CancelAsync().ConfigureAwait(false);
                try { await _errorDrain.ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
                try { await _shellExit.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                _ = _ready.Task.Exception; _ = _started.Task.Exception;
            }
            finally { _owner.Dispose(); _drainLifetime.Dispose(); _job?.Dispose(); _process.Dispose(); }
        }
    }
}
