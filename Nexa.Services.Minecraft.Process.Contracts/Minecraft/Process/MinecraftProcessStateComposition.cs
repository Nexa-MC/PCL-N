
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Process;


public static class MinecraftProcessStateComposition
{
    /// <summary>Ordered collection state key: snapshots keyed by session id.</summary>
    public static readonly XsrSemanticId SessionsKey = XsrSemanticId.Parse("minecraft.process.sessions");

    public static readonly XsrSemanticId FailuresKey = XsrSemanticId.Parse("minecraft.process.failures");

    public static void DeclareState(XsrStateStoreBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Collection<MinecraftProcessFailure, Guid>(FailuresKey, "Nexa.Services.Minecraft.Process", static failure => failure.SessionId);
        builder.Collection<MinecraftProcessSnapshot, Guid>(
            SessionsKey,
            "Nexa.Services.Minecraft.Process",
            static snapshot => snapshot.SessionId);
        JvmHostStateContract.DeclareState(builder);
    }
}
