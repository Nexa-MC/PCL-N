using Nexa.Services.Capabilities;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Launch;

public sealed record LaunchPreflightPrompt(Guid Attempt, CapabilityPreflightReport Report);
public sealed record LaunchPreflightDecision(Guid Attempt, bool Continue);

public static class LaunchPreflightContract
{
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("minecraft.launch.preflight");
    public static readonly XsrSemanticId DecisionCommand = XsrSemanticId.Parse("minecraft.launch.preflight.decide");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<LaunchPreflightPrompt?>(StateKey, "Nexa.Services.Minecraft.Launch");
}
