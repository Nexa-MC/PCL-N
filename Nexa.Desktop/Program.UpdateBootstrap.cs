using System.Diagnostics;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

namespace Nexa.Desktop;

internal static partial class Program
{
    private static bool RedirectUpdatedLauncher(string[] args)
    {
        if (!string.Equals(Environment.ProcessPath, UpdateInstallation.Bootstrap,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        try
        {
            using IUpdateDirectory root = ProtectedUpdateDirectory.Open(UpdateInstallation.Root);
            using FileStream journal = root.OpenRead(AutomaticUpdateTransaction.ActivationName);
            string[]? active = UpdateTransactionJournal.Read(journal);
            if (active is not { Length: 4 } || active[1].Length == 0) return false;
            if (!UpdateVersion.TryParse(active[0], out var target) || !UpdateVersion.TryParse(ResolveInformationalVersion(), out var baseline)
                || target <= baseline || !active[1].StartsWith(".nexa-slot-", StringComparison.Ordinal) || active[1].Length != 43)
                return false;
            using IUpdateDirectory slot = root.OpenDirectory(active[1]);
            IUpdateDirectory? app = null, contents = null, binaries = null;
            try
            {
                if (OperatingSystem.IsMacOS())
                { app = slot.OpenDirectory("Nexa.app"); contents = app.OpenDirectory("Contents"); binaries = contents.OpenDirectory("MacOS"); }
                IUpdateDirectory payload = binaries ?? slot;
                string name = "Nexa.Desktop" + (OperatingSystem.IsWindows() ? ".exe" : "");
                using FileStream image = payload.OpenRead(name);
                var start = new ProcessStartInfo(Path.Combine(payload.Path, name)) { UseShellExecute = false, WorkingDirectory = payload.Path };
                foreach (string argument in args) start.ArgumentList.Add(argument);
                using Process process = Process.Start(start) ?? throw new IOException("无法启动更新版本。");
                return true;
            }
            finally { binaries?.Dispose(); contents?.Dispose(); app?.Dispose(); }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            // Stable bootstrap remains runnable; do not read or execute a user-owned fallback.
            return false;
        }
    }
}
