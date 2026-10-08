using System.Diagnostics;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Jvm.Host;

/// <summary>Enrolls auxiliary Java only in an already enrolled managed-runtime component.</summary>
internal sealed class JvmChildRuntimeLease : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _retain;

    private JvmChildRuntimeLease(FileStream stream, string path) { _stream = stream; _path = path; }

    internal static JvmChildRuntimeLease? AcquireIfEnrolled(string javaExecutable)
    {
        string java = Path.GetFullPath(javaExecutable);
        for (string? root = Path.GetDirectoryName(java); root is not null; root = Path.GetDirectoryName(root))
        {
            string jobs = Path.Combine(root, JvmRuntimeUseRecord.JobsDirectory);
            if (!Directory.Exists(jobs)) continue;
            string relative = Path.GetRelativePath(root, java);
            string component = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || component is "" or "." or ".." || component.Length > 128
                || !component.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                throw new InvalidDataException("Invalid JVM child runtime containment.");
            string uses = Path.Combine(root, JvmRuntimeUseRecord.UsesDirectory, component);
            string rootLock = Path.Combine(root, JvmRuntimeUseRecord.RootLockName);
            // The Java owner creates these during verified parent enrollment. Directory
            // location alone cannot bootstrap new enrollment or grant deletion authority.
            if (!Directory.Exists(uses) || !File.Exists(rootLock)) return null;
            CheckLinks(java); CheckLinks(jobs); CheckLinks(uses); CheckLinks(rootLock);
            using var authorityLock = new FileStream(rootLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (!File.Exists(java) || !Directory.Exists(uses)) throw new IOException("JVM child runtime changed.");
            string? enrollment = Directory.EnumerateFiles(uses, "*.lease").FirstOrDefault(static file =>
                Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _));
            if (enrollment is null) throw new IOException("JVM child runtime enrollment has retired.");
            CheckLinks(enrollment);
            string path = Path.Combine(uses, Guid.NewGuid().ToString("N") + ".lease");
            CheckLinks(path);
            return new(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), path);
        }
        return null;
    }

    internal void Bind(Process process)
    {
        // Creation has happened. Any uncertain identity leaves a conservative partial record.
        _retain = true;
        if (process.HasExited) { _retain = false; return; }
        long ticks = process.StartTime.ToUniversalTime().Ticks;
        if (ticks <= 0 || ticks > DateTime.UtcNow.Ticks) throw new IOException("Unknown JVM child process identity.");
        Span<byte> record = stackalloc byte[JvmRuntimeUseRecord.Length];
        JvmRuntimeUseRecord.Write(record, process.Id, ticks);
        _stream.SetLength(record.Length); _stream.Position = 0;
        _stream.Write(record); _stream.Flush(flushToDisk: true);
    }

    internal void ConfirmNoLiveChild() => _retain = false;

    public void Dispose()
    {
        _stream.Dispose();
        if (_retain) return;
        try { CheckLinks(_path); File.Delete(_path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("JVM child runtime lease paths cannot contain links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
