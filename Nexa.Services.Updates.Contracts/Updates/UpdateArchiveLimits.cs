


namespace Nexa.Services.Updates;

public sealed record UpdateArchiveLimits
{
    public long MaximumArchiveBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumFileBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumExpandedBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaximumEntries { get; init; } = 65536;
    internal void Validate(string archive)
    {
        if (MaximumArchiveBytes <= 0 || MaximumFileBytes <= 0 || MaximumExpandedBytes <= 0 || MaximumEntries <= 0)
            throw new InvalidOperationException("更新归档预算必须为正数。");
        if (new FileInfo(archive).Length > MaximumArchiveBytes) throw new InvalidDataException("更新归档过大。");
    }
}
