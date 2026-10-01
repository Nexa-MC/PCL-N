using Nexa.Desktop.Ui;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void StartupInstallRecoveryOwnsDispatchAndShutdown()
    {
        using var entered = new ManualResetEventSlim();
        using var stopped = new ManualResetEventSlim();
        string? observed = null;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        XsrCommandRouterBuilder builder = new();
        builder.Register<MinecraftInstallRecoveryCommand>(MinecraftInstallRoutes.Recover, async (command, token) =>
        {
            observed = command.RootDirectories[0]; entered.Set();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped.Set(); await release.Task.ConfigureAwait(false); }
            return XsrResult.Success();
        });
        List<string> reports = [];
        string[] roots = ["original-root"];
        using var session = new DesktopInstallRecoverySession(builder.Build(new NoopDispatchObserver()), roots, reports.Add);
        roots[0] = "changed-root";
        AssertTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        session.Dispose(); session.Dispose();
        Task shutdown = session.DisposeAsync().AsTask();
        AssertFalse(shutdown.IsCompleted);
        release.SetResult();
        shutdown.GetAwaiter().GetResult();
        AssertTrue(stopped.IsSet); AssertEqual("original-root", observed); AssertEqual(0, reports.Count);
    }
}
