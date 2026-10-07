using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Desktop;

internal enum DesktopDestination : byte { Launch, Install, Resources, Settings, Activate }

internal static class DesktopActivation
{
    internal static bool TryParse(string value, out DesktopDestination destination)
    {
        destination = DesktopDestination.Launch;
        if (value.Equals("nexacl://", StringComparison.OrdinalIgnoreCase) || value.Equals("nexacl:", StringComparison.OrdinalIgnoreCase))
        { destination = DesktopDestination.Activate; return true; }
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "nexacl" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        string route = uri.Host == "open" ? uri.AbsolutePath.Trim('/') : uri.Host;
        if (uri.Host != "open" && uri.AbsolutePath is not "" and not "/") return false;
        switch (route)
        {
            case "": case "launch": destination = DesktopDestination.Launch; return true;
            case "install": destination = DesktopDestination.Install; return true;
            case "resources": destination = DesktopDestination.Resources; return true;
            case "settings": destination = DesktopDestination.Settings; return true;
            default: return false;
        }
    }
}

/// <summary>A user-level bootstrap lease and bounded, asynchronous activation mailbox.</summary>
internal sealed class DesktopSingleInstance : IAsyncDisposable
{
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<DesktopDestination> _pending = new();
    private readonly object _gate = new();
    private readonly Task _listener;
    private Action? _wake;
    private volatile bool _closing;

    private DesktopSingleInstance(FileStream lease, NamedPipeServerStream first)
    {
        _lease = lease;
        _listener = ListenAsync(first);
    }

    internal Action? Wake
    {
        set { lock (_gate) _wake = value; }
    }

    internal static async Task<DesktopSingleInstance?> AcquireAsync(string directory, DesktopDestination destination)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(Path.GetFullPath(directory), "instance.lock");
        string pipe = "nexacl-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            FileStream lease;
            try { lease = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                byte[] reply = new byte[1];
                try
                {
                    using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    await client.WriteAsync(new byte[] { 1, (byte)destination }, deadline.Token).ConfigureAwait(false);
                    await client.ReadExactlyAsync(reply, deadline.Token).ConfigureAwait(false);
                    // Windows disconnect discards unread output. Confirm receipt before the
                    // server retires this connection; neither side blocks a native UI thread.
                    await client.WriteAsync(new byte[] { 0xA6 }, deadline.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (reply[0] == 1) return null;
                if (reply[0] != 2) throw new IOException("主进程尚不能接收激活请求，请稍后重试。");
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
                continue;
            }
            try
            {
                var primary = new DesktopSingleInstance(lease, CreateServer(pipe));
                primary.Enqueue(destination);
                return primary;
            }
            catch { lease.Dispose(); throw; }
        }
    }

    private static NamedPipeServerStream CreateServer(string pipe) =>
        new(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private bool Enqueue(DesktopDestination destination)
    {
        Action? wake;
        lock (_gate)
        {
            if (_closing || _pending.Count >= 16) return false;
            _pending.Enqueue(destination);
            wake = _wake;
        }
        wake?.Invoke();
        return true;
    }

    internal bool TryTake(out DesktopDestination destination)
    {
        lock (_gate) return _pending.TryDequeue(out destination);
    }
    internal bool QueueActivation(DesktopDestination destination) => Enqueue(destination);
    internal void BeginShutdown() { lock (_gate) { _closing = true; _wake = null; } }

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        using NamedPipeServerStream server = first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    byte[] packet = new byte[2];
                    await server.ReadExactlyAsync(packet, deadline.Token).ConfigureAwait(false);
                    bool accepted = packet[0] == 1 && packet[1] <= (byte)DesktopDestination.Activate
                        && Enqueue((DesktopDestination)packet[1]);
                    await server.WriteAsync(new byte[] { accepted ? (byte)1 : _closing ? (byte)2 : (byte)0 }, deadline.Token).ConfigureAwait(false);
                    await server.FlushAsync(deadline.Token).ConfigureAwait(false);
                    byte[] receipt = new byte[1];
                    await server.ReadExactlyAsync(receipt, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
                server.Disconnect();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        BeginShutdown();
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _listener.ConfigureAwait(false); }
        finally { _stop.Dispose(); _lease.Dispose(); }
    }
}
