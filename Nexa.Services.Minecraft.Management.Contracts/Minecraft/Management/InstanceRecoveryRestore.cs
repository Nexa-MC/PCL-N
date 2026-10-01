



namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceRecoveryRestoreCommand(string InstanceDirectory, Guid BaselineRevision,
    string Fingerprint, IReadOnlyList<InstanceRecoveryChange> Changes);
