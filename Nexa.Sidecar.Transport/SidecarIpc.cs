using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Nexa.Sidecar.Protocol;

namespace Nexa.Sidecar.Transport;

/// <summary>
/// Accepts one Sidecar connection at a time over the platform local IPC: named pipes on Windows,
/// Unix-domain sockets elsewhere. The stream factories are the only OS-specific surface; the
/// protocol and session layers stay transport-agnostic. On Unix the socket lives in a
/// randomized 0700 directory and the socket file itself is 0600 — same-user security is a
/// requirement, never a umask assumption.
/// </summary>
public sealed class SidecarIpcListener : IDisposable
{
    private readonly string _pipeName;
    private readonly string? _unixDirectory;
    private readonly object _gate = new();
    private NamedPipeServerStream? _windowsServer;
    private Socket? _unixSocket;
    private bool _disposed;
    private bool _accepting;

    private SidecarIpcListener(string pipeName, string? unixDirectory)
    {
        _pipeName = pipeName;
        _unixDirectory = unixDirectory;
    }

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Binds a listener. The pipe name is the endpoint both sides agree on.
    /// </summary>
    public static SidecarIpcListener Bind(string pipeName)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("No local IPC transport on this platform.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        string? unixDirectory = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            unixDirectory = CreateGuardedUnixDirectory();
        }

        SidecarIpcListener listener = new(pipeName, unixDirectory);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { listener.BindUnix(); }
            catch { listener.Dispose(); throw; }
        }

        return listener;
    }

    /// <summary>
    /// Accepts one connection. On Windows a fresh pipe instance is created per accept.
    /// </summary>
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NamedPipeServerStream? pipe = null;
        Socket? unixSocket;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_accepting) throw new InvalidOperationException("The sidecar listener already has an outstanding accept.");
            if (OperatingSystem.IsWindows()) pipe = CreateWindowsPipe();
            unixSocket = _unixSocket;
            _accepting = true;
        }
        try
        {
            if (pipe is not null)
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    // The returned stream belongs to the caller, not to the listener.
                    if (ReferenceEquals(_windowsServer, pipe)) _windowsServer = null;
                }
                return pipe;
            }
            if (unixSocket is not null)
            {
                Socket accepted = await unixSocket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
                    return new NetworkStream(accepted, ownsSocket: true);
                }
                catch { accepted.Dispose(); throw; }
            }
            throw new PlatformNotSupportedException("No local IPC transport on this platform.");
        }
        catch { pipe?.Dispose(); throw; }
        finally
        {
            lock (_gate)
            {
                _accepting = false;
                if (ReferenceEquals(_windowsServer, pipe)) _windowsServer = null;
            }
        }
    }

    public void Dispose()
    {
        NamedPipeServerStream? windowsServer;
        Socket? unixSocket;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            windowsServer = _windowsServer;
            _windowsServer = null;
            unixSocket = _unixSocket;
            _unixSocket = null;
        }

        if (OperatingSystem.IsWindows())
        {
            windowsServer?.Dispose();
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            unixSocket?.Dispose();
            try
            {
                if (File.Exists(SocketPath))
                {
                    File.Delete(SocketPath);
                }

                if (_unixDirectory is not null && Directory.Exists(_unixDirectory))
                {
                    Directory.Delete(_unixDirectory);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The socket file and directory are best-effort cleanup.
            }
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private void BindUnix()
    {
        string socketPath = SocketPath;
        _unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _unixSocket.Bind(new UnixDomainSocketEndPoint(socketPath));
        _unixSocket.Listen(1);
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Gets the endpoint the connector dialls: the pipe name on Windows, and on Unix the short
    /// socket path inside the guarded directory (Unix socket paths cap at ~104 bytes).
    /// </summary>
    public string Endpoint => OperatingSystem.IsWindows() ? _pipeName : SocketPath;

    private string SocketPath => _unixDirectory is null ? _pipeName : Path.Combine(_unixDirectory, "s.sock");

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static string CreateGuardedUnixDirectory()
    {
        // A randomized 0700 directory per listener: even a world-writable temp root cannot let
        // another user reach or pre-empt the endpoint.
        string directory = Path.Combine(
            Path.GetTempPath(),
            "pcl-n-sidecar-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private NamedPipeServerStream CreateWindowsPipe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _windowsServer?.Dispose();
            _windowsServer = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return _windowsServer;
        }
    }
}

/// <summary>
/// Connects to a Sidecar IPC listener. On Unix, pass the listener's
/// <see cref="SidecarIpcListener.Endpoint"/> — the guarded socket path — not the bare name.
/// </summary>
public static class SidecarIpcConnector
{
    public static async ValueTask<Stream> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(timeout: 10_000, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch { pipe.Dispose(); throw; }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Socket socket = new(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(pipeName), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }

        throw new PlatformNotSupportedException("No local IPC transport on this platform.");
    }
}
