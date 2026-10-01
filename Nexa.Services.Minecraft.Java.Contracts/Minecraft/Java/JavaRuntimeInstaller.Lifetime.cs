
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public sealed record JavaInstallStopCommand(bool Pause);
public sealed record JavaInstallRecoverCommand;
public static class JavaInstallRoutes
{
    public static readonly XsrSemanticId Stop = XsrSemanticId.Parse("minecraft.java.install.stop");
    public static readonly XsrSemanticId Recover = XsrSemanticId.Parse("minecraft.java.install.recover");
}
