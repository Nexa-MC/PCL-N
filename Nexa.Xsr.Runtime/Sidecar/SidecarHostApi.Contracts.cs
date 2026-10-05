using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

/// <summary>Host-internal routes; they never grant a Sidecar unrelated business capabilities.</summary>
public static class SidecarHostRoutes
{
    public static readonly XsrSemanticId Command = XsrSemanticId.Parse("sidecar.command");
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("sidecar.query");
    public static readonly XsrSemanticId BinaryCommand = XsrSemanticId.Parse("sidecar.binary.command");
    public static readonly XsrSemanticId BinaryQuery = XsrSemanticId.Parse("sidecar.binary.query");
    public static readonly XsrSemanticId State = XsrSemanticId.Parse("sidecar.state.read");
    public static readonly XsrSemanticId Resource = XsrSemanticId.Parse("sidecar.resource.read");
    public static readonly XsrSemanticId UiModule = XsrSemanticId.Parse("sidecar.ui.read");
    public static readonly XsrSemanticId Catalog = XsrSemanticId.Parse("sidecar.catalog");
    public static readonly XsrSemanticId Session = XsrSemanticId.Parse("sidecar.session");
    public static readonly XsrSemanticId Health = XsrSemanticId.Parse("sidecar.health");
    public static readonly XsrSemanticId Stream = XsrSemanticId.Parse("sidecar.stream.open");
    public static readonly XsrSemanticId Stop = XsrSemanticId.Parse("sidecar.stop");
    public static readonly XsrSemanticId Restart = XsrSemanticId.Parse("sidecar.restart");
    public static readonly XsrSemanticId Reload = XsrSemanticId.Parse("sidecar.reload");
}

public sealed record SidecarCommandCall(string PackageName, XsrSemanticId Contract, string? Argument = null, TimeSpan? Timeout = null);
public sealed record SidecarQueryCall(string PackageName, XsrSemanticId Contract, string? Argument = null, TimeSpan? Timeout = null);
public sealed record SidecarBinaryCall(string PackageName, XsrSemanticId Contract, SidecarBinaryValue? Argument = null, TimeSpan? Timeout = null);
public sealed record SidecarContractCall(string PackageName, XsrSemanticId Contract);
public sealed record SidecarPackageCall(string PackageName);
public sealed record SidecarHealthCall(string PackageName, TimeSpan? Timeout = null);
public sealed record SidecarCatalogQuery;
public sealed record SidecarReloadCall;
public sealed record SidecarStateRead(long Revision, XsrStateAvailability Availability, bool HasValue, SidecarBinaryValue? Value);
public sealed record SidecarSessionInfo(Guid SessionId, SidecarSessionState State, SidecarFeatures Features,
    IReadOnlyList<SidecarRegistrationEntry> Contracts, SidecarSessionMetrics Metrics);

/// <summary>Owned local content; its size follows registration budgets, not a wire value field.</summary>
public sealed class SidecarCachedContent
{
    private readonly byte[] _bytes;
    private readonly byte[] _hash;
    internal SidecarCachedContent(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();
        _hash = System.Security.Cryptography.SHA256.HashData(_bytes);
    }
    public int Length => _bytes.Length;
    public ReadOnlySpan<byte> Span => _bytes;
    public ReadOnlySpan<byte> ContentHash => _hash;
    public byte[] ToArray() => _bytes.ToArray();
}
