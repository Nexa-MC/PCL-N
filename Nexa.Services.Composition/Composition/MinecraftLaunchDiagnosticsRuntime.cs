using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public static class MinecraftLaunchDiagnosticsRuntime
{
    public static void Register(XsrQueryRouterBuilder queries, MinecraftLaunchPlanDiagnostics diagnostics)
    {
        queries.Register<MinecraftLaunchPlanDiagnosticQuery, MinecraftLaunchPlanDiagnosticSnapshot>(MinecraftLaunchPlanDiagnosticContract.Query,
            (query, _) => ValueTask.FromResult(XsrResult.Success(diagnostics.Read(query))));
        queries.Register<InstanceLocalDocumentsQuery, InstanceLocalDocumentsSnapshot>(InstanceLocalDocumentsContract.Query,
            async (query, token) => XsrResult.Success(await InstanceLocalDocumentsService.ReadAsync(query, token).ConfigureAwait(false)));
    }
}
