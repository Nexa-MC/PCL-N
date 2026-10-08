using System.Collections.ObjectModel;
using Nexa.Platform;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>Revalidates native graphics eligibility immediately before process creation.</summary>
public sealed class MinecraftLaunchGraphicsPolicy(IPlatformGameGraphicsRuntime runtime)
{
    private readonly IPlatformGameGraphicsRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    public XsrResult<MinecraftLaunchPlan> Apply(MinecraftLaunchPlan plan, string gpuPreference, string rendererPreference)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var prepared = _runtime.Prepare(gpuPreference, rendererPreference);
        if (!prepared.Succeeded) return XsrResult.Failure<MinecraftLaunchPlan>(new(XsrErrorKind.Rejected,
            XsrSemanticId.Parse("minecraft.graphics." + prepared.Code), prepared.Message));
        Dictionary<string, string> environment = new(plan.EnvironmentVariables, StringComparer.Ordinal);
        foreach (var pair in prepared.Environment)
        {
            if (environment.TryGetValue(pair.Key, out string? explicitValue) && explicitValue != pair.Value)
                return XsrResult.Failure<MinecraftLaunchPlan>(new(XsrErrorKind.Rejected,
                    XsrSemanticId.Parse("minecraft.graphics.environment_conflict"), "自定义环境变量与已选 GPU / renderer 冲突，请先检查启动预览。"));
            environment[pair.Key] = pair.Value;
        }
        return XsrResult.Success(plan with { EnvironmentVariables = new ReadOnlyDictionary<string, string>(environment) });
    }
}
