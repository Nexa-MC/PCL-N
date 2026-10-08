using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Desktop;

internal enum DesktopDestination : byte { Launch, Install, Resources, Settings, Activate, Tasks, Java, Storage, About }
internal readonly record struct DesktopInstanceActivation(DesktopDestination Destination, string? File);

internal static class DesktopActivation
{
    internal const int MaximumFileBytes = 8192;
    internal static bool TryFile(string value, out string file)
    {
        file = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumFileBytes || value.Any(char.IsControl)
            || !Path.IsPathFullyQualified(value) || value.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        string extension = Path.GetExtension(value);
        if (!extension.Equals(".mrpack", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".nexapack", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            file = Path.GetFullPath(value);
            if (new UTF8Encoding(false, true).GetByteCount(file) > MaximumFileBytes) { file = string.Empty; return false; }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { file = string.Empty; return false; }
    }
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
            case "tasks": destination = DesktopDestination.Tasks; return true;
            case "java": destination = DesktopDestination.Java; return true;
            case "storage": destination = DesktopDestination.Storage; return true;
            case "about": destination = DesktopDestination.About; return true;
            default: return false;
        }
    }
}

/// <summary>A user-level bootstrap lease and bounded, asynchronous activation mailbox.</summary>
internal sealed class DesktopSingleInstance : IAsyncDisposable
{
    private readonly FileStream _lease;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<DesktopInstanceActivation> _pending = new();
    private readonly object _gate = new();
    private readonly Task _listener;
    private Action? _wake;
    private volatile bool _closing;

    private DesktopSingleInstance(FileStream lease, NamedPipeServerStream first, DesktopInstanceActivation initial)
    {
        _lease = lease;
        _pending.Enqueue(initial);
        _listener = ListenAsync(first);
    }

    internal Action? Wake
    {
        set { lock (_gate) _wake = value; }
    }

    internal static async Task<DesktopSingleInstance?> AcquireAsync(string directory, DesktopDestination destination, string? file = null, bool forwardActivation = true)
    {
        if (!Enum.IsDefined(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        if (file is not null)
        {
            if (!DesktopActivation.TryFile(file, out string admitted)) throw new ArgumentException("不支持的本地整合包路径。", nameof(file));
            file = admitted;
        }
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
                if (!forwardActivation) return null;
                byte[] reply = new byte[1];
                try
                {
                    using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    // Recheck the lease when a closing owner has already retired its pipe.
                    // A single connection attempt must not consume the whole bootstrap budget.
                    await client.ConnectAsync(250, deadline.Token).ConfigureAwait(false);
                    byte[] packet;
                    if (file is null) packet = [1, (byte)destination];
                    else
                    {
                        byte[] encoded = Encoding.UTF8.GetBytes(file);
                        packet = new byte[4 + encoded.Length]; packet[0] = 2; packet[1] = (byte)destination;
                        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), (ushort)encoded.Length);
                        encoded.CopyTo(packet, 4);
                    }
                    await client.WriteAsync(packet, deadline.Token).ConfigureAwait(false);
                    await client.ReadExactlyAsync(reply, deadline.Token).ConfigureAwait(false);
                    // Windows disconnect discards unread output. Confirm receipt before the
                    // server retires this connection; neither side blocks a native UI thread.
                    await client.WriteAsync(new byte[] { 0xA6 }, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or TimeoutException)
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
                var primary = new DesktopSingleInstance(lease, CreateServer(pipe), new(destination, file));
                return primary;
            }
            catch { lease.Dispose(); throw; }
        }
    }

    private static NamedPipeServerStream CreateServer(string pipe) =>
        new(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private bool Enqueue(DesktopInstanceActivation activation)
    {
        Action? wake;
        lock (_gate)
        {
            if (_closing || _pending.Count >= 16) return false;
            _pending.Enqueue(activation);
            wake = _wake;
        }
        wake?.Invoke();
        return true;
    }

    internal bool TryTake(out DesktopDestination destination)
    {
        bool taken = TryTakeActivation(out DesktopInstanceActivation activation);
        destination = activation.Destination;
        return taken;
    }
    internal bool TryTakeActivation(out DesktopInstanceActivation activation) { lock (_gate) return _pending.TryDequeue(out activation); }
    internal bool QueueActivation(DesktopDestination destination, string? file = null)
    {
        if (!Enum.IsDefined(destination) || file is not null && !DesktopActivation.TryFile(file, out _)) return false;
        return Enqueue(new(destination, file));
    }
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
                    string? file = null;
                    bool valid = packet[0] == 1;
                    if (packet[0] == 2)
                    {
                        byte[] size = new byte[2];
                        await server.ReadExactlyAsync(size, deadline.Token).ConfigureAwait(false);
                        int length = BinaryPrimitives.ReadUInt16LittleEndian(size);
                        if (length is > 0 and <= DesktopActivation.MaximumFileBytes)
                        {
                            byte[] encoded = new byte[length];
                            await server.ReadExactlyAsync(encoded, deadline.Token).ConfigureAwait(false);
                            try { valid = DesktopActivation.TryFile(new UTF8Encoding(false, true).GetString(encoded), out file); }
                            catch (DecoderFallbackException) { valid = false; }
                        }
                    }
                    bool accepted = valid && Enum.IsDefined((DesktopDestination)packet[1])
                        && Enqueue(new((DesktopDestination)packet[1], file));
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
