

using Nexa.Xsr;

namespace Nexa.Services.Minecraft;


public static class MinecraftErrors
{
    public static readonly XsrSemanticId InvalidRequestCode = XsrSemanticId.Parse("minecraft.invalid_request");
    public static readonly XsrSemanticId LaunchFailedCode = XsrSemanticId.Parse("minecraft.launch_failed");
    public static readonly XsrSemanticId LaunchPreparationFailedCode = XsrSemanticId.Parse("minecraft.launch_preparation_failed");
    public static readonly XsrSemanticId InstanceNotFoundCode = XsrSemanticId.Parse("minecraft.instance_not_found");
    public static readonly XsrSemanticId JavaUnavailableCode = XsrSemanticId.Parse("minecraft.java_unavailable");
    public static readonly XsrSemanticId UnsupportedAccountCode = XsrSemanticId.Parse("minecraft.unsupported_account");
    public static readonly XsrSemanticId ProcessNotFoundCode = XsrSemanticId.Parse("minecraft.process_not_found");
    public static readonly XsrSemanticId LaunchAlreadyActiveCode = XsrSemanticId.Parse("minecraft.launch_already_active");
    public static readonly XsrSemanticId ExitedBeforeWindowCode = XsrSemanticId.Parse("minecraft.exited_before_window");

    public static XsrError InvalidRequest(string reason) => new(XsrErrorKind.Rejected, InvalidRequestCode, $"The Minecraft request was rejected: {reason}");
    public static XsrError LaunchFailed(string reason) => new(XsrErrorKind.Unavailable, LaunchFailedCode, $"The Minecraft process could not be started: {reason}");
    public static XsrError LaunchPreparationFailed(string reason) => new(XsrErrorKind.Rejected, LaunchPreparationFailedCode, $"The Minecraft launch could not be prepared: {reason}");
    public static XsrError InstanceNotFound(string instanceId) => new(XsrErrorKind.NotFound, InstanceNotFoundCode, $"The Minecraft instance '{instanceId}' is not installed.");
    public static XsrError JavaUnavailable(string reason) => new(XsrErrorKind.Unavailable, JavaUnavailableCode, $"A compatible Java runtime is unavailable: {reason}");
    public static XsrError UnsupportedAccount(string reason) => new(XsrErrorKind.Rejected, UnsupportedAccountCode, $"The launch account cannot be used: {reason}");
    public static XsrError ProcessNotFound(Guid sessionId) => new(XsrErrorKind.NotFound, ProcessNotFoundCode, $"The Minecraft process session '{sessionId}' was not found or has already ended.");
    public static XsrError LaunchAlreadyActive() => new(XsrErrorKind.Rejected, LaunchAlreadyActiveCode, "A Minecraft launch pipeline is already running; cancel it before starting another.");
    public static XsrError ExitedBeforeWindow() => new(XsrErrorKind.Unavailable, ExitedBeforeWindowCode, "The Minecraft process exited before its window appeared.");
}
