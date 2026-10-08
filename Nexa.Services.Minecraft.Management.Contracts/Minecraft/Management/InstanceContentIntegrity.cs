using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceContentIntegrityQuery(string InstanceDirectory, string PageId, string Name, long ExpectedSize, long ExpectedModifiedUtcTicks);
public enum InstanceContentBaselineState { Missing, ManagedUpdate, Incomplete }
public sealed record InstanceContentIntegrity(InstanceContentIntegrityQuery File, DateTimeOffset CapturedAt, string Sha256, string Sha512,
    InstanceContentBaselineState BaselineState, string? BaselineSha256, Guid? BaselineTransactionId, bool? Modified);
public static class InstanceContentIntegrityContract
{
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.instance.content.integrity");
}
