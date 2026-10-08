using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexa.Jvm.Host;

/// <summary>Java child calls from mods that discover this executable through ProcessHandle.</summary>
internal static partial class JvmChildLaunch
{
    internal const string JavaEnvironmentVariable = "NEXACL_JVM_CHILD_JAVA";
    private const int MaximumArguments = 16384;
    private const int MaximumStringBytes = 1024 * 1024;
    private const int MaximumCommandBytes = 4 * 1024 * 1024;

    internal static void Configure(string javaExecutable)
    {
        // Unix managed environment changes alone do not update libc's environment consumed
        // by the embedded JVM. Both views contain only the selected runtime path.
        Environment.SetEnvironmentVariable(JavaEnvironmentVariable, javaExecutable);
        if (OperatingSystem.IsLinux() && LinuxSetEnvironment(JavaEnvironmentVariable, javaExecutable, 1) != 0
            || OperatingSystem.IsMacOS() && MacSetEnvironment(JavaEnvironmentVariable, javaExecutable, 1) != 0)
            throw new InvalidOperationException("Cannot configure JVM child runtime.");
    }

    internal static ProcessStartInfo? CreateStartInfo(string? javaExecutable, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(javaExecutable) || !Path.IsPathFullyQualified(javaExecutable)
            || !File.Exists(javaExecutable) || arguments.Count is 0 or > MaximumArguments)
            return null;
        string java = new FileInfo(javaExecutable).ResolveLinkTarget(true)?.FullName ?? javaExecutable;
        string filename = Path.GetFileName(java);
        if (OperatingSystem.IsWindows()
            ? !filename.Equals("java.exe", StringComparison.OrdinalIgnoreCase) && !filename.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)
            : filename != "java") return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Environment.ProcessPath is { } host && Path.GetFullPath(host).Equals(Path.GetFullPath(java), comparison))
            return null;

        int bytes = 0;
        bool classpath = false, target = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is null || argument.Contains('\0')) return null;
            int length = Encoding.UTF8.GetByteCount(argument);
            if (length > MaximumStringBytes || bytes > MaximumCommandBytes - length - 1) return null;
            bytes += length + 1;
        }
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument is "--jvm-host" or "--" or "-m" or "--module" || argument.Length == 0 || argument[0] == '@'
                || argument.StartsWith("--module=", StringComparison.Ordinal)) return null;
            if (argument is "-cp" or "-classpath" or "--class-path")
            {
                if (++index >= arguments.Count || !IsOperand(arguments[index])) return null;
                classpath = true;
            }
            else if (argument.StartsWith("--class-path=", StringComparison.Ordinal))
            {
                if (argument.Length == 13) return null;
                classpath = true;
            }
            else if (argument == "-jar")
            {
                if (++index >= arguments.Count || !IsOperand(arguments[index])) return null;
                target = true;
                break;
            }
            else if (argument is "--module-path" or "-p" or "--add-modules" or "--add-exports"
                or "--add-opens" or "--add-reads" or "--limit-modules" or "--patch-module" or "--upgrade-module-path")
            {
                if (++index >= arguments.Count || !IsOperand(arguments[index])) return null;
            }
            else if (argument[0] != '-')
            {
                if (!classpath || argument.Any(static c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '$')))
                    return null;
                target = true;
                break;
            }
        }
        if (!target) return null;
        ProcessStartInfo start = new(java)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove(JavaEnvironmentVariable);
        return start;
    }

    private static bool IsOperand(string value) => value.Length > 0 && value[0] != '@' && value != "--jvm-host";

    internal static int Run(string[] arguments)
    {
        try
        {
            var start = CreateStartInfo(Environment.GetEnvironmentVariable(JavaEnvironmentVariable), arguments);
            if (start is null) return 2;
            using var lease = JvmChildRuntimeLease.AcquireIfEnrolled(start.FileName);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    using Process bridge = Process.GetCurrentProcess();
                    lease?.Bind(bridge);
                    return ReplaceProcess(start);
                }
                finally
                {
                    // Successful exec never returns. Only a failed handoff reaches cleanup.
                    lease?.ConfirmNoLiveChild();
                }
            }
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start JVM child.");
            try
            {
                lease?.Bind(child);
                child.WaitForExit();
                lease?.ConfirmNoLiveChild();
                return child.ExitCode;
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                child.WaitForExit();
                lease?.ConfirmNoLiveChild();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            // A child command can contain private mod data. Never echo arguments or exception text.
            Console.Error.WriteLine("Nexa JVM Host: Java child launch failed.");
            return 1;
        }
    }

    private static int ReplaceProcess(ProcessStartInfo start)
    {
        Environment.SetEnvironmentVariable(JavaEnvironmentVariable, null);
        int cleared = OperatingSystem.IsMacOS() ? MacUnsetEnvironment(JavaEnvironmentVariable) : LinuxUnsetEnvironment(JavaEnvironmentVariable);
        if (cleared != 0) throw new InvalidOperationException("Cannot prepare JVM child environment.");
        nint[] vector = new nint[start.ArgumentList.Count + 2];
        try
        {
            vector[0] = Marshal.StringToCoTaskMemUTF8(start.FileName);
            for (int index = 0; index < start.ArgumentList.Count; index++)
                vector[index + 1] = Marshal.StringToCoTaskMemUTF8(start.ArgumentList[index]);
            // execv replaces the bridge with Java: PID, exit code and inherited IO remain exact.
            if (OperatingSystem.IsMacOS()) _ = MacExec(vector[0], vector);
            else _ = LinuxExec(vector[0], vector);
            throw new InvalidOperationException("Cannot replace JVM child process.");
        }
        finally
        {
            foreach (nint value in vector) if (value != 0) Marshal.FreeCoTaskMem(value);
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxSetEnvironment(string name, string value, int overwrite);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacSetEnvironment(string name, string value, int overwrite);
    [LibraryImport("libc", EntryPoint = "unsetenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxUnsetEnvironment(string name);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "unsetenv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacUnsetEnvironment(string name);
    [DllImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static extern int LinuxExec(nint path, [In] nint[] arguments);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "execv", SetLastError = true)]
    private static extern int MacExec(nint path, [In] nint[] arguments);
}
