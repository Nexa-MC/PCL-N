using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Nexa.Services.Minecraft.Process;

/// <summary>Private self-hosted command supervisor; dispatched before application composition.</summary>
public static partial class MinecraftLaunchHookWorker
{
    public const string WorkerArgument = "--nexa-launch-hook-worker";
    internal const int ReadyMagic = 0x4e4c4852;
    internal const int StartedMagic = 0x4e4c4853;
    internal const int ExitMagic = 0x4e4c4845;
    internal const byte DetachOperation = 1;
    private const int RequestMagic = 0x4e4c4831;
    private const int MaximumCommandCharacters = 32768;
    private const int MaximumCommandBytes = 4 * MaximumCommandCharacters;
    private const int MaximumDirectoryBytes = 32768;
    private const int MaximumFrameBytes = 20 + MaximumCommandBytes + MaximumDirectoryBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ProcessStartInfo CreateStartInfo()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("The pre-launch worker host could not be located.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assembly = Environment.GetCommandLineArgs()[0];
            if (!Path.IsPathFullyQualified(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The managed pre-launch worker host could not be located.");
            start.ArgumentList.Add(assembly);
        }
        start.ArgumentList.Add(WorkerArgument);
        return start;
    }

    internal static byte[] CreateRequest(string command, string directory)
    {
        ValidateCommand(command);
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new ArgumentException("A concrete hook working directory is required.", nameof(directory));
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, StrictUtf8, leaveOpen: true);
        writer.Write(RequestMagic); writer.Write(0L);
        WriteText(writer, command, MaximumCommandBytes); WriteText(writer, directory, MaximumDirectoryBytes);
        writer.Flush(); return buffer.ToArray();
    }

    private static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > MaximumCommandCharacters || command.Contains('\0'))
            throw new ArgumentException("Invalid pre-launch command.", nameof(command));
    }

    private static void WriteText(BinaryWriter writer, string value, int maximum)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length is 0 || bytes.Length > maximum) throw new ArgumentException("Hook text exceeds the binary transport bound.");
        writer.Write(bytes.Length); writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader, int maximum)
    {
        int length = reader.ReadInt32();
        if (length is < 1 || length > maximum) throw new InvalidDataException("Invalid hook text length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return StrictUtf8.GetString(bytes);
    }

    internal static async Task WriteRequestAsync(Stream stream, byte[] request, CancellationToken token)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, request.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<int> ReadInt32Async(Stream stream)
    {
        byte[] bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static async Task WriteInt32Async(Stream stream, int value)
    {
        byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await stream.WriteAsync(bytes).ConfigureAwait(false); await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<bool> ReadOwnerAsync(Stream stream)
    {
        byte[] control = new byte[1];
        return await stream.ReadAsync(control).ConfigureAwait(false) == 1 && control[0] == DetachOperation;
    }

    public static async Task<int> RunWorkerAsync()
    {
        bool isolated = false, detached = false;
        WindowsJob? job = null;
        System.Diagnostics.Process? shell = null;
        CancellationTokenSource? drainsLifetime = null;
        Task? drains = null;
        try
        {
            if (!OperatingSystem.IsWindows()) { CreateGroup(); isolated = true; }
            using Stream owner = Console.OpenStandardInput();
            using Stream status = Console.OpenStandardOutput();
            await WriteInt32Async(status, ReadyMagic).ConfigureAwait(false);
            int length = await ReadInt32Async(owner).ConfigureAwait(false);
            if (length is < 22 or > MaximumFrameBytes) throw new InvalidDataException("Invalid hook request frame.");
            byte[] frame = new byte[length];
            string command, directory;
            try
            {
                await owner.ReadExactlyAsync(frame).ConfigureAwait(false);
                using var buffer = new MemoryStream(frame, writable: false);
                using var reader = new BinaryReader(buffer, StrictUtf8, leaveOpen: true);
                if (reader.ReadInt32() != RequestMagic) throw new InvalidDataException("Invalid hook request magic.");
                long handle = reader.ReadInt64();
                if (OperatingSystem.IsWindows()) job = WindowsJob.FromTransferredHandle(handle);
                else if (handle != 0) throw new InvalidDataException("Unexpected hook Job handle.");
                command = ReadText(reader, MaximumCommandBytes); directory = ReadText(reader, MaximumDirectoryBytes);
                ValidateCommand(command);
                if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0') || buffer.Position != buffer.Length)
                    throw new InvalidDataException("Invalid hook working directory or trailing frame data.");
            }
            finally { frame.AsSpan().Clear(); }
            var start = new ProcessStartInfo(OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows()) start.Arguments = "/d /s /c \"" + command + "\"";
            else { start.ArgumentList.Add("-c"); start.ArgumentList.Add(command); }
            shell = new() { StartInfo = start };
            if (!shell.Start()) throw new IOException("The pre-launch shell could not be started.");
            shell.StandardInput.Close();
            drainsLifetime = new();
            drains = Task.WhenAll(shell.StandardOutput.BaseStream.CopyToAsync(Stream.Null, drainsLifetime.Token),
                shell.StandardError.BaseStream.CopyToAsync(Stream.Null, drainsLifetime.Token));
            await WriteInt32Async(status, StartedMagic).ConfigureAwait(false);
            Task exit = shell.WaitForExitAsync(CancellationToken.None);
            Task<bool> ownerRelease = ReadOwnerAsync(owner);
            if (await Task.WhenAny(exit, ownerRelease).ConfigureAwait(false) == ownerRelease)
            {
                detached = await ownerRelease.ConfigureAwait(false);
                if (!detached) return 2;
            }
            await exit.ConfigureAwait(false);
            await WriteInt32Async(status, ExitMagic).ConfigureAwait(false);
            await WriteInt32Async(status, shell.ExitCode).ConfigureAwait(false);
            if (!detached) detached = await ownerRelease.ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { return 2; }
        finally
        {
            if (!detached)
            {
                if (job is not null) job.Terminate();
                else if (isolated) KillGroup(Environment.ProcessId);
            }
            if (drainsLifetime is not null)
            {
                // Detached descendants may still write into inherited pipes. Keep discarding
                // their output until EOF, independently of the already reported shell exit.
                if (!detached) await drainsLifetime.CancelAsync().ConfigureAwait(false);
                try { if (drains is not null) await drains.ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
                drainsLifetime.Dispose();
            }
            shell?.Dispose(); job?.Dispose();
        }
    }
}
