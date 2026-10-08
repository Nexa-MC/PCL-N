using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public static class InstanceIdentityRuntime
{
    public static void Register(XsrCommandRouterBuilder commands, XsrQueryRouterBuilder queries)
    {
        commands.Register<InstanceIdentitySaveCommand>(InstanceIdentityContract.Save,
            async (command, token) => await InstanceIdentityService.SaveAsync(command, token).ConfigureAwait(false));
        queries.Register<InstanceIdentityQuery, InstanceIdentitySnapshot>(InstanceIdentityContract.Query,
            async (query, token) => XsrResult.Success(await InstanceIdentityService.ReadAsync(query, token).ConfigureAwait(false)));
    }
}

public static class MinecraftSafeLaunchSessionRuntime
{
    public static void Register(XsrCommandRouterBuilder commands, XsrQueryRouterBuilder queries, MinecraftLaunchCoordinator coordinator)
    {
        commands.Register<MinecraftSafeLaunchSessionCommand>(MinecraftSafeLaunchSessionContract.Set,
            (command, _) => ValueTask.FromResult(coordinator.SetSafeLaunchSession(command)));
        queries.Register<MinecraftSafeLaunchSessionQuery, MinecraftSafeLaunchSessionSnapshot>(MinecraftSafeLaunchSessionContract.Query,
            (query, _) => ValueTask.FromResult(XsrResult.Success(coordinator.ReadSafeLaunchSession(query))));
    }
}
