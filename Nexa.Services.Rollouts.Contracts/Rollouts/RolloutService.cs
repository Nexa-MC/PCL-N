



using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Rollouts;

public sealed record RolloutSnapshot(long Revision, bool CompactTelemetryBatches, bool Available);
public static class RolloutStateContract
{
    public static readonly XsrSemanticId StateKey = XsrSemanticId.Parse("launcher.rollout.snapshot");
    public static void DeclareState(XsrStateStoreBuilder builder) => builder.Cell<RolloutSnapshot>(StateKey, "Nexa.Services.Rollouts");
}
