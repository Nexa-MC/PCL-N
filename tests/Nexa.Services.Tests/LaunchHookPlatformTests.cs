using System.Diagnostics;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LaunchHookPlatformReapsAndKillsOwnedCommands()
    {
        string root = CreateTempDirectory();
        try
        {
            var port = new SystemMinecraftLaunchHookPort();
            string cwd = OperatingSystem.IsWindows() ? "cd > cwd.txt & exit /b 7" : "pwd > cwd.txt; exit 7";
            await using (var process = await port.StartAsync(cwd, root))
            {
                AssertEqual(7, await process.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                AssertEqual(root, (await File.ReadAllTextAsync(Path.Combine(root, "cwd.txt"))).Trim());
            }
            string output = OperatingSystem.IsWindows()
                ? "for /L %i in (1,1,5000) do @echo discarded output & echo discarded error 1>&2"
                : "i=0; while [ $i -lt 5000 ]; do printf 'discarded output\\n'; printf 'discarded error\\n' >&2; i=$((i+1)); done";
            await using (var process = await port.StartAsync(output, root))
                AssertEqual(0, await process.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

            var detachedExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var process = await port.StartAsync(OperatingSystem.IsWindows()
                ? "echo detached > detached.txt & exit /b 23" : "echo detached > detached.txt; exit 23", root))
                process.Detach(code => detachedExit.TrySetResult(code));
            AssertEqual(23, await detachedExit.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            AssertTrue(File.Exists(Path.Combine(root, "detached.txt")));

            // A cancelled wait alone is not cleanup; disposal owns both shell and child.
            // The child would create orphan.txt after the readiness marker if it survived.
            string waiting = OperatingSystem.IsWindows()
                ? "echo ready > ready.txt & ping -n 3 127.0.0.1 > nul & echo orphan > orphan.txt"
                : "echo ready > ready.txt; (sleep 1; echo orphan > orphan.txt) & wait";
            var owned = await port.StartAsync(waiting, root);
            try
            {
                using var cancellation = new CancellationTokenSource();
                Task<int> wait = owned.WaitForExitAsync(cancellation.Token).AsTask();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(Path.Combine(root, "ready.txt")) && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                AssertTrue(File.Exists(Path.Combine(root, "ready.txt")));
                cancellation.Cancel();
                bool cancelled = false;
                try { await wait; } catch (OperationCanceledException) { cancelled = true; }
                AssertTrue(cancelled);
            }
            finally { await owned.DisposeAsync(); }
            await Task.Delay(OperatingSystem.IsWindows() ? 2200 : 1100);
            AssertFalse(File.Exists(Path.Combine(root, "orphan.txt")));

            using var beforeStart = new CancellationTokenSource(); beforeStart.Cancel();
            bool refused = false;
            try { await port.StartAsync("echo no > unwanted.txt", root, beforeStart.Token); }
            catch (OperationCanceledException) { refused = true; }
            AssertTrue(refused); AssertFalse(File.Exists(Path.Combine(root, "unwanted.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask LaunchHookPlatformKillsChildrenAfterShellExit()
    {
        string root = CreateTempDirectory();
        IMinecraftLaunchHookSession? owned = null;
        Task? cleanup = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string command = await WriteHookPlatformBackgroundCommandAsync(root, 17);
            owned = await new SystemMinecraftLaunchHookPort().StartAsync(command, root, deadline.Token);
            using var worker = Process.GetProcessById(SystemMinecraftLaunchHookPort.GetWorkerProcessId(owned));
            // The child keeps stdout/stderr open while its parent shell has already exited.
            AssertEqual(17, await owned.WaitForExitAsync(deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), deadline.Token));
            AssertTrue(File.Exists(Path.Combine(root, "child-ready.txt")));
            AssertFalse(worker.HasExited);
            AssertFalse(File.Exists(Path.Combine(root, "child-survived.txt")));
            AssertFalse(File.Exists(Path.Combine(root, "child-expired.txt")));

            cleanup = owned.DisposeAsync().AsTask();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(1), deadline.Token);
            await worker.WaitForExitAsync(deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "child-release.txt"), "release", deadline.Token);
            await Task.Delay(OperatingSystem.IsWindows() ? 1200 : 250, deadline.Token);
            AssertFalse(File.Exists(Path.Combine(root, "child-survived.txt")));
            AssertFalse(File.Exists(Path.Combine(root, "child-expired.txt")));
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "child-release.txt"), "release");
                if (cleanup is not null) await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                else if (owned is not null) await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { await DeleteHookPlatformDirectoryAsync(root); }
        }
    }

    private static async ValueTask LaunchHookPlatformStartupFailureKillsWaitedBackgroundChildren()
    {
        string root = CreateTempDirectory();
        var hooks = new CapturingPlatformHookPort();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var processes = new MinecraftProcessService();
            var host = new HookFixtureHost(processes, []) { FailStart = true };
            var executor = new MinecraftLaunchExecutor(host, log: null, hooks: hooks);
            var plan = HookPlan(root) with
            {
                PreLaunchCommand = await WriteHookPlatformBackgroundCommandAsync(root, 0),
                WaitForPreLaunchCommand = true,
            };
            bool failed = false;
            try { await executor.ExecuteAsync(plan, "waited-platform-failure", cancellationToken: deadline.Token).AsTask().WaitAsync(deadline.Token); }
            catch (IOException error) when (error.Message == "fixture-start-failed") { failed = true; }
            AssertTrue(failed);
            AssertTrue(File.Exists(Path.Combine(root, "child-ready.txt")));
            AssertFalse(File.Exists(Path.Combine(root, "child-expired.txt")));
            AssertTrue(hooks.Worker is not null);
            await hooks.Worker!.WaitForExitAsync(deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "child-release.txt"), "release", deadline.Token);
            await Task.Delay(OperatingSystem.IsWindows() ? 1200 : 250, deadline.Token);
            AssertFalse(File.Exists(Path.Combine(root, "child-survived.txt")));
            AssertFalse(File.Exists(Path.Combine(root, "child-expired.txt")));
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "child-release.txt"), "release");
                if (hooks.Session is not null)
                    await hooks.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if (hooks.Worker is not null)
                    await hooks.Worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { hooks.Worker?.Dispose(); await DeleteHookPlatformDirectoryAsync(root); }
        }
    }

    private static async ValueTask LaunchHookPlatformDetachRetainsChildrenAndReapsWorker()
    {
        string root = CreateTempDirectory();
        IMinecraftLaunchHookSession? session = null;
        Process? worker = null;
        bool detached = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string command = await WriteHookPlatformBackgroundCommandAsync(root, 23);
            session = await new SystemMinecraftLaunchHookPort().StartAsync(command, root, deadline.Token);
            worker = Process.GetProcessById(SystemMinecraftLaunchHookPort.GetWorkerProcessId(session));
            AssertEqual(23, await session.WaitForExitAsync(deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), deadline.Token));
            AssertTrue(File.Exists(Path.Combine(root, "child-ready.txt")));
            AssertFalse(File.Exists(Path.Combine(root, "child-survived.txt")));
            AssertFalse(worker.HasExited);
            var detachedExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Detach(code => detachedExit.TrySetResult(code));
            detached = true;
            await session.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            AssertEqual(23, await detachedExit.Task.WaitAsync(deadline.Token));
            AssertFalse(File.Exists(Path.Combine(root, "child-survived.txt")));

            // Dispose after Detach must leave the surviving child able to finish its work.
            await File.WriteAllTextAsync(Path.Combine(root, "child-release.txt"), "release", deadline.Token);
            await WaitForHookPlatformMarkerAsync(root, "child-survived.txt", deadline.Token);
            AssertFalse(File.Exists(Path.Combine(root, "child-expired.txt")));
            await worker.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "child-release.txt"), "release");
                if (session is not null) await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                if (detached && !File.Exists(Path.Combine(root, "child-expired.txt")))
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await WaitForHookPlatformMarkerAsync(root, "child-survived.txt", cleanupDeadline.Token);
                }
                if (worker is not null) await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { worker?.Dispose(); await DeleteHookPlatformDirectoryAsync(root); }
        }
    }

    private static async Task<string> WriteHookPlatformBackgroundCommandAsync(string root, int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(root, "child.cmd"), """
                @echo off
                echo ready > child-ready.txt
                echo child output
                echo child error 1>&2
                for /L %%i in (1,1,4) do (
                  if exist child-release.txt goto released
                  ping -n 2 127.0.0.1 > nul
                )
                echo expired > child-expired.txt
                exit /b 0
                :released
                echo resumed output
                if errorlevel 1 exit /b 31
                echo resumed error 1>&2
                if errorlevel 1 exit /b 32
                echo survived > child-survived.txt
                exit /b 0
                """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(root, "background.cmd"), $$"""
                @echo off
                start "" /b cmd.exe /d /c child.cmd
                for /L %%i in (1,1,2) do (
                  if exist child-ready.txt exit /b {{exitCode}}
                  ping -n 2 127.0.0.1 > nul
                )
                exit /b 29
                """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
            return "background.cmd";
        }

        await File.WriteAllTextAsync(Path.Combine(root, "child.sh"), """
            set -e
            printf ready > child-ready.txt
            printf 'child output\n'
            printf 'child error\n' >&2
            i=0
            while [ ! -f child-release.txt ] && [ "$i" -lt 80 ]; do
              sleep 0.05
              i=$((i+1))
            done
            if [ -f child-release.txt ]; then
              printf 'resumed output\n'
              printf 'resumed error\n' >&2
              printf survived > child-survived.txt
            else
              printf expired > child-expired.txt
            fi
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "background.sh"), $$"""
            /bin/sh child.sh &
            i=0
            while [ ! -f child-ready.txt ] && [ "$i" -lt 40 ]; do
              sleep 0.05
              i=$((i+1))
            done
            [ -f child-ready.txt ] || exit 29
            exit {{exitCode}}
            """);
        return "exec /bin/sh background.sh";
    }

    private static async Task WaitForHookPlatformMarkerAsync(string root, string marker, CancellationToken token)
    {
        while (!File.Exists(Path.Combine(root, marker))) await Task.Delay(10, token);
    }

    private static async Task DeleteHookPlatformDirectoryAsync(string root)
    {
        // A detached cmd may still hold its batch file during its final exit instruction.
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(root, true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(25); }
        }
    }

    private sealed class CapturingPlatformHookPort : IMinecraftLaunchHookPort
    {
        public IMinecraftLaunchHookSession? Session { get; private set; }
        public Process? Worker { get; private set; }
        public async ValueTask<IMinecraftLaunchHookSession> StartAsync(string command, string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Session = await new SystemMinecraftLaunchHookPort().StartAsync(command, workingDirectory, cancellationToken);
            Worker = Process.GetProcessById(SystemMinecraftLaunchHookPort.GetWorkerProcessId(Session));
            return Session;
        }
    }
}
