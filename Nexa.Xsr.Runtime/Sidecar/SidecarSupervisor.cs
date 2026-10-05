using System.Diagnostics;
using System.Security.Cryptography;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime;

/// <summary>Verifies and independently supervises admitted top-level native Sidecar packages.</summary>
public sealed class SidecarSupervisor : IDisposable, IAsyncDisposable
{
    private const int MaximumPackages = 64;
    private const int MaximumRecoveryAttempts = 3;
    private readonly Func<Stream, Stream, CancellationToken, Task> _verify;
    private readonly Action<string, string>? _diagnostic;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _controls = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Package> _packages = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<Task> _workers = [];
    private Task _startup = Task.CompletedTask;
    private Task? _shutdown;
    private string? _directory;
    private bool _started, _disposed;
    public XsrFunctionPatchAdmission? FunctionPatchAdmission { get; init; }
    public XsrSignalAdmission? SignalAdmission { get; init; }
    public XsrUiPatchAdmission? UiPatchAdmission { get; init; }
    public XsrUiModuleAdmission? UiModuleAdmission { get; init; }

    public SidecarSupervisor(Func<Stream, Stream, CancellationToken, Task> verify,
        Action<string, string>? diagnostic = null)
    {
        _verify = verify ?? throw new ArgumentNullException(nameof(verify));
        _diagnostic = diagnostic;
    }

    public IReadOnlyList<SidecarHostSession> Sessions
    {
        get
        {
            lock (_gate) return Array.AsReadOnly(_packages.Values
                .Select(package => package.Child?.Session)
                .Where(session => session?.State == SidecarSessionState.Active)
                .Cast<SidecarHostSession>().ToArray());
        }
    }

    public IReadOnlyList<SidecarPackageSnapshot> PackageSnapshots
    {
        get
        {
            lock (_gate) return Array.AsReadOnly(_packages.Values.OrderBy(package => package.Name, StringComparer.Ordinal)
                .Select(package => new SidecarPackageSnapshot(package.Name, package.Status,
                    package.Child?.Session.SessionId, package.Child?.ProcessId,
                    package.RecoveryAttempts, package.FailureCode)).ToArray());
        }
    }

    /// <summary>Resolves an active session by admitted package identity, never by an arbitrary path.</summary>
    public bool TryGetSession(string packageName, out SidecarHostSession? session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        lock (_gate)
        {
            session = !_disposed && _packages.TryGetValue(packageName, out Package? package)
                && package.Child?.Session.State == SidecarSessionState.Active ? package.Child.Session : null;
            return session is not null;
        }
    }

    public Task StartAsync(string executableDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Sidecar discovery has already started.");
            _started = true;
            _directory = Path.GetFullPath(executableDirectory);
            _startup = DiscoverAsync(cancellationToken);
            return _startup;
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _controls.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await RescanCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { _controls.Release(); }
    }

    /// <summary>Stops one known package and disables its automatic recovery until explicitly restarted.</summary>
    public async ValueTask<bool> StopAsync(string packageName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _controls.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Package? package = FindPackage(packageName);
            if (package is null) return false;
            await StopPackageAsync(package, SidecarPackageStatus.Stopped).ConfigureAwait(false);
            return true;
        }
        finally { _controls.Release(); }
    }

    /// <summary>Re-verifies the admitted image and publishes only a completely initialized new session.</summary>
    public async ValueTask<bool> RestartAsync(string packageName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _controls.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Package? package = FindPackage(packageName);
            if (package is null || package.Status == SidecarPackageStatus.Removed) return false;
            await StopPackageAsync(package, SidecarPackageStatus.Stopped).ConfigureAwait(false);
            lock (_gate) { package.RecoveryEnabled = true; package.RecoveryAttempts = 0; }
            return await StartChildAsync(package, linked.Token).ConfigureAwait(false);
        }
        finally { _controls.Release(); }
    }

    /// <summary>Rescans the original directory, retires removed packages, and reloads all current packages.</summary>
    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _controls.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_started) throw new InvalidOperationException("Sidecar discovery has not started.");
            }
            await RescanCoreAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _controls.Release(); }
    }

    private Package? FindPackage(string name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _packages.TryGetValue(name, out Package? package) ? package : null;
        }
    }

    private async Task RescanCoreAsync(CancellationToken cancellationToken)
    {
        string[] candidates = Directory.EnumerateFiles(_directory!, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".nsc", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Take(MaximumPackages + 1).ToArray();
        if (candidates.Length > MaximumPackages) throw new InvalidDataException("Sidecar discovery package budget exceeded.");
        var identities = new HashSet<string>(_packages.Comparer);
        List<string> admittedPaths = [];
        foreach (var group in candidates.GroupBy(path => Path.GetFileNameWithoutExtension(path), _packages.Comparer))
        {
            string name = group.Key;
            if (string.IsNullOrWhiteSpace(name) || group.Count() != 1)
            {
                Report(Path.GetFileName(group.First()), "failed: sidecar.package_identity");
                continue;
            }
            identities.Add(name);
            admittedPaths.Add(group.Single());
        }
        candidates = admittedPaths.ToArray();
        Package[] prior;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            prior = _packages.Values.ToArray();
        }
        foreach (Package package in prior)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopPackageAsync(package, identities.Contains(package.Name)
                ? SidecarPackageStatus.Stopped : SidecarPackageStatus.Removed).ConfigureAwait(false);
        }
        // Removing departed packages bounds the catalog independently of reload count.
        lock (_gate)
            foreach (Package package in prior.Where(package => package.Status == SidecarPackageStatus.Removed))
                _packages.Remove(package.Name);
        foreach (string path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(path);
            Package package;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_packages.TryGetValue(name, out package!)) _packages.Add(name, package = new(name, path));
                package.Path = path;
                package.RecoveryEnabled = true;
                package.RecoveryAttempts = 0;
            }
            await StartChildAsync(package, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> StartChildAsync(Package package, CancellationToken cancellationToken)
    {
        Process? process = null;
        Stream? stream = null;
        SidecarHostSession? session = null;
        byte[] challenge = RandomNumberGenerator.GetBytes(SidecarBootstrap.ChallengeSize);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); package.Status = SidecarPackageStatus.Starting; package.FailureCode = null; }
        try
        {
            string path = package.Path;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(path + ".asc") & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked Sidecar images and signatures are not supported.");
            using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            SidecarExecutable.Validate(image);
            using var signature = new FileStream(path + ".asc", FileMode.Open, FileAccess.Read, FileShare.Read);
            if (signature.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("Invalid Sidecar signature size.");
            await _verify(image, signature, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            using var listener = SidecarIpcListener.Bind("nexa-" + Guid.NewGuid().ToString("N"));
            Task<Stream> accepting = listener.AcceptAsync(deadline.Token).AsTask();
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                RedirectStandardInput = true,
            };
            start.ArgumentList.Add("--nexa-sidecar");
            start.ArgumentList.Add("--endpoint");
            start.ArgumentList.Add(listener.Endpoint);
            try
            {
                process = Process.Start(start) ?? throw new IOException("Sidecar process did not start.");
                await process.StandardInput.BaseStream.WriteAsync(challenge, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                Task exit = process.WaitForExitAsync(deadline.Token);
                if (await Task.WhenAny(accepting, exit).ConfigureAwait(false) != accepting)
                    throw new IOException("Sidecar exited before connecting.");
                stream = await accepting.ConfigureAwait(false);
            }
            catch
            {
                deadline.Cancel();
                listener.Dispose();
                try { (await accepting.ConfigureAwait(false)).Dispose(); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                throw;
            }
            await SidecarBootstrap.AuthenticateAsync(stream, challenge, deadline.Token).ConfigureAwait(false);
            session = new(new SidecarConnection(stream), package.Name)
            { FunctionPatchAdmission = FunctionPatchAdmission, SignalAdmission = SignalAdmission, UiPatchAdmission = UiPatchAdmission, UiModuleAdmission = UiModuleAdmission };
            stream = null;
            await session.HandshakeAsync(deadline.Token).ConfigureAwait(false);
            await session.AcceptRegistrationAsync(deadline.Token).ConfigureAwait(false);
            await session.AcceptStateSnapshotAsync(deadline.Token).ConfigureAwait(false);
            await session.ActivateAsync(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var child = new Child(process, session);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                package.Child = child;
                package.Status = SidecarPackageStatus.Active;
                package.FailureCode = null;
                child.Monitor = Task.Run(() => MonitorAsync(package, child), CancellationToken.None);
                Track(child.Monitor);
            }
            process = null;
            session = null;
            Report(package.Name, "active");
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            lock (_gate) { package.Status = SidecarPackageStatus.Failed; package.FailureCode = "sidecar.start_failed"; }
            Report(package.Name, "failed: sidecar.start_failed");
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            stream?.Dispose();
            session?.Dispose();
            if (process is not null) await StopProcessAsync(process).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(Package package, Child child)
    {
        Task receive = child.Session.RunReceiveLoopAsync(child.Lifetime.Token).AsTask();
        try
        {
            Task exit = child.Process.WaitForExitAsync();
            await Task.WhenAny(receive, exit).ConfigureAwait(false);
            if (!child.IntentionalStop)
            {
                child.Session.Dispose();
                await receive.ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { Report(package.Name, "disconnected: sidecar.disconnected"); }
        finally
        {
            // Joining the receive loop is required even when process exit wins during an
            // intentional shutdown; the controller closes its transport within its deadline.
            try { await receive.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
            if (!child.IntentionalStop)
            {
                child.Session.Dispose();
                await child.StopAsync().ConfigureAwait(false);
                long version;
                bool recover;
                lock (_gate)
                {
                    recover = !_disposed && package.RecoveryEnabled && ReferenceEquals(package.Child, child);
                    version = package.ControlVersion;
                    if (ReferenceEquals(package.Child, child))
                    {
                        package.Child = null;
                        package.Status = recover && package.RecoveryAttempts < MaximumRecoveryAttempts
                            ? SidecarPackageStatus.Recovering : recover ? SidecarPackageStatus.Quarantined : SidecarPackageStatus.Stopped;
                        package.FailureCode = recover ? "sidecar.disconnected" : null;
                    }
                }
                child.EndReceive();
                if (recover) Track(RecoverAsync(package, version));
            }
        }
    }

    private async Task RecoverAsync(Package package, long version)
    {
        try
        {
            while (true)
            {
                int attempt;
                lock (_gate)
                {
                    if (_disposed || !package.RecoveryEnabled || package.ControlVersion != version || package.Child is not null) return;
                    attempt = package.RecoveryAttempts;
                    if (attempt >= MaximumRecoveryAttempts)
                    {
                        package.Status = SidecarPackageStatus.Quarantined;
                        package.FailureCode = "sidecar.recovery_exhausted";
                        return;
                    }
                    package.Status = SidecarPackageStatus.Recovering;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), _lifetime.Token).ConfigureAwait(false);
                await _controls.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (_disposed || !package.RecoveryEnabled || package.ControlVersion != version || package.Child is not null) return;
                        package.RecoveryAttempts++;
                    }
                    if (await StartChildAsync(package, _lifetime.Token).ConfigureAwait(false)) return;
                }
                finally { _controls.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task StopPackageAsync(Package package, SidecarPackageStatus status)
    {
        Child? child;
        lock (_gate)
        {
            package.ControlVersion++;
            package.RecoveryEnabled = false;
            child = package.Child;
            if (child is not null) child.IntentionalStop = true;
            package.Status = status;
            package.FailureCode = null;
        }
        if (child is not null)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await child.Session.ShutdownAsync(deadline.Token).ConfigureAwait(false);
                try { await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
            finally
            {
                child.Session.Dispose();
                child.CancelReceive();
                await child.StopAsync().ConfigureAwait(false);
                await child.Monitor.ConfigureAwait(false);
                child.EndReceive();
                lock (_gate) if (ReferenceEquals(package.Child, child)) package.Child = null;
            }
        }
        Report(package.Name, status.ToString().ToLowerInvariant());
    }

    private void Track(Task task)
    {
        lock (_gate) _workers.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_gate) _workers.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Report(string name, string status)
    {
        try { _diagnostic?.Invoke(name, status); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or TimeoutException) { }
        finally { process.Dispose(); }
    }

    /// <summary>Rejects admission immediately and asynchronously joins startup, recovery and child teardown.</summary>
    public ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task shutdown;
        lock (_gate)
        {
            if (_shutdown is null)
            {
                _disposed = true;
                _shutdown = Task.Run(ShutdownCoreAsync, CancellationToken.None);
            }
            shutdown = _shutdown;
        }
        return new(cancellationToken.CanBeCanceled ? shutdown.WaitAsync(cancellationToken) : shutdown);
    }

    private async Task ShutdownCoreAsync()
    {
        _lifetime.Cancel();
        await _controls.WaitAsync().ConfigureAwait(false);
        try
        {
            Package[] packages;
            lock (_gate) packages = _packages.Values.ToArray();
            // Independent children shut down concurrently; one stalled peer cannot multiply the exit deadline.
            await Task.WhenAll(packages.Select(package => StopPackageAsync(package, SidecarPackageStatus.Stopped))).ConfigureAwait(false);
        }
        finally { _controls.Release(); }
        try { await _startup.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
        Task[] workers;
        lock (_gate) workers = _workers.ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Consume the ValueTask and observe failures even when this synchronous fallback
        // has no caller joining DisposeAsync. The shared shutdown task retains ownership.
        _ = ShutdownAsync().AsTask().ContinueWith(static completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private sealed class Package(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; set; } = path;
        public Child? Child { get; set; }
        public SidecarPackageStatus Status { get; set; } = SidecarPackageStatus.Discovered;
        public int RecoveryAttempts { get; set; }
        public bool RecoveryEnabled { get; set; } = true;
        public long ControlVersion { get; set; }
        public string? FailureCode { get; set; }
    }

    private sealed class Child(Process process, SidecarHostSession session)
    {
        private readonly object _stopGate = new();
        private readonly object _receiveGate = new();
        private Task? _stopTask;
        private bool _receiveEnded;
        public Process Process { get; } = process;
        public int ProcessId { get; } = process.Id;
        public SidecarHostSession Session { get; } = session;
        public CancellationTokenSource Lifetime { get; } = new();
        public volatile bool IntentionalStop;
        public Task Monitor { get; set; } = Task.CompletedTask;
        public Task StopAsync() { lock (_stopGate) return _stopTask ??= StopProcessAsync(Process); }
        public void CancelReceive() { lock (_receiveGate) if (!_receiveEnded) Lifetime.Cancel(); }
        public void EndReceive()
        {
            lock (_receiveGate)
            {
                if (_receiveEnded) return;
                _receiveEnded = true;
                Lifetime.Cancel();
                Lifetime.Dispose();
            }
        }
    }
}
