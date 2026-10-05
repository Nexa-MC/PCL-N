using Nexa.Xsr;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static async ValueTask HostFacadeReloadReturnsStableFailureForCatalogBudget()
    {
        string directory = Directory.CreateTempSubdirectory("nexa-host-facade-budget-").FullName;
        try
        {
            int verificationCalls = 0;
            await using var supervisor = new SidecarSupervisor((_, _, _) =>
            {
                Interlocked.Increment(ref verificationCalls);
                return Task.CompletedTask;
            });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await supervisor.StartAsync(directory, deadline.Token);
            var api = new SidecarHostApi(supervisor);
            for (int index = 0; index < 65; index++)
                await File.WriteAllBytesAsync(Path.Combine(directory, $"package-{index}.nsc"), [], deadline.Token);

            XsrResult direct = await api.ReloadAsync(deadline.Token);
            AssertFalse(direct.IsSuccess);
            AssertEqual(XsrErrorKind.Faulted, direct.Error!.Kind);
            AssertEqual("xsr.handler_faulted", direct.Error.Code.Value);

            XsrResult routed = await CompleteCommand(api, SidecarHostRoutes.Reload,
                new SidecarReloadCall(), deadline.Token);
            AssertFalse(routed.IsSuccess);
            AssertEqual(XsrErrorKind.Faulted, routed.Error!.Kind);
            AssertEqual(direct.Error.Code, routed.Error.Code);
            AssertEqual(0, verificationCalls);
            AssertEqual(0, supervisor.PackageSnapshots.Count);
            AssertEqual(0, supervisor.Sessions.Count);
            await supervisor.ShutdownAsync(deadline.Token);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
