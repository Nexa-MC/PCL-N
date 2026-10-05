using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

/// <summary>
/// The host-side content cache populated at registration: UI modules and resources arrive
/// inline with their SHA-256 hashes, are verified, stored content-addressed, and are then
/// served from this cache — opening a registered plugin page or reading a resource performs
/// zero IPC.
/// </summary>
public sealed class SidecarHostCache
{
    private readonly Dictionary<XsrSemanticId, byte[]> _uiModules = [];
    private readonly Dictionary<XsrSemanticId, IReadOnlyList<XsrSemanticId>> _uiResources = [];
    private readonly Dictionary<XsrSemanticId, string> _resourceHashes = [];
    private readonly Dictionary<string, byte[]> _resourcesByHash = [];
    private readonly object _gate = new();

    /// <summary>
    /// Gets the number of distinct cached resource blobs (content-addressed by hash).
    /// </summary>
    public int ResourceCount
    {
        get
        {
            lock (_gate)
            {
                return _resourcesByHash.Count;
            }
        }
    }

    /// <summary>
    /// Stores one UI module after verifying its hash. A mismatched hash fails registration.
    /// </summary>
    public void AddUiModule(XsrSemanticId semantic, byte[] payload, byte[] contentHash)
        => AddUiModule(semantic, payload, contentHash, []);

    /// <summary>Stores a verified module and its already-cached resource dependencies.</summary>
    public void AddUiModule(XsrSemanticId semantic, byte[] payload, byte[] contentHash,
        IReadOnlyList<XsrSemanticId> requiredResources)
    {
        ArgumentNullException.ThrowIfNull(requiredResources);
        if (requiredResources.Count > 4096)
            throw new SidecarProtocolException("The UI module exceeds its resource reference budget.");
        XsrSemanticId[] references = requiredResources.ToArray();
        foreach (XsrSemanticId reference in references)
        {
            ValidateSemantic(reference);
        }
        byte[] owned = CopyAndVerifyHash(semantic, payload, contentHash, out _);
        lock (_gate)
        {
            foreach (XsrSemanticId reference in references)
                if (!_resourceHashes.ContainsKey(reference))
                    throw new SidecarProtocolException("The UI module references a resource outside the verified cache.");
            _uiModules[semantic] = owned;
            _uiResources[semantic] = Array.AsReadOnly(references);
        }
    }

    /// <summary>
    /// Opens a registered UI module from the local cache. This is the zero-IPC path the
    /// renderer uses when the user opens a registered plugin page.
    /// </summary>
    public bool TryOpenUiModule(XsrSemanticId semantic, out byte[]? payload)
    {
        lock (_gate)
        {
            if (_uiModules.TryGetValue(semantic, out byte[]? owned))
            {
                payload = owned.ToArray();
                return true;
            }
            payload = null;
            return false;
        }
    }

    /// <summary>Reads the validated resource references for a cached UI module without IPC.</summary>
    public bool TryGetRequiredUiResources(XsrSemanticId semantic, out IReadOnlyList<XsrSemanticId>? resources)
    {
        lock (_gate) return _uiResources.TryGetValue(semantic, out resources);
    }

    /// <summary>
    /// Stores one resource content-addressed by its verified hash. Transferring the same
    /// content twice stores it once; a mismatched hash fails registration.
    /// </summary>
    public void AddResource(XsrSemanticId semantic, byte[] payload, byte[] contentHash)
    {
        byte[] owned = CopyAndVerifyHash(semantic, payload, contentHash, out string hash);
        lock (_gate)
        {
            if (_resourcesByHash.TryGetValue(hash, out byte[]? existing))
            {
                if (!existing.AsSpan().SequenceEqual(owned))
                {
                    throw new SidecarProtocolException(
                        $"The resource '{semantic}' hashes to an already-cached blob with different content.");
                }

                _resourceHashes[semantic] = hash;
                return;
            }

            _resourcesByHash[hash] = owned;
            _resourceHashes[semantic] = hash;
        }
    }

    /// <summary>
    /// Reads a cached resource by its hash.
    /// </summary>
    public bool TryGetResource(byte[] contentHash, out byte[]? payload)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        if (contentHash.Length != 32)
        {
            payload = null;
            return false;
        }
        string hash = Convert.ToHexString(contentHash);
        lock (_gate)
        {
            return TryCopyResource(hash, out payload);
        }
    }

    /// <summary>Reads registered resource content by its semantic identity, entirely locally.</summary>
    public bool TryGetResource(XsrSemanticId semantic, out byte[]? payload)
    {
        lock (_gate)
        {
            if (_resourceHashes.TryGetValue(semantic, out string? hash)) return TryCopyResource(hash, out payload);
            payload = null;
            return false;
        }
    }

    /// <summary>Reads an owned copy of the hash admitted for a resource semantic.</summary>
    public bool TryGetResourceHash(XsrSemanticId semantic, out byte[]? contentHash)
    {
        lock (_gate)
        {
            if (_resourceHashes.TryGetValue(semantic, out string? hash))
            {
                contentHash = Convert.FromHexString(hash);
                return true;
            }
            contentHash = null;
            return false;
        }
    }

    private bool TryCopyResource(string hash, out byte[]? payload)
    {
        if (_resourcesByHash.TryGetValue(hash, out byte[]? owned))
        {
            payload = owned.ToArray();
            return true;
        }
        payload = null;
        return false;
    }

    private static byte[] CopyAndVerifyHash(XsrSemanticId semantic, byte[] payload, byte[] expectedHash, out string hash)
    {
        ValidateSemantic(semantic);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(expectedHash);
        byte[] owned = payload.ToArray();
        byte[] expected = expectedHash.ToArray();
        byte[] actual = SHA256.HashData(owned);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new SidecarProtocolException(
                $"The content of '{semantic}' does not match its declared SHA-256 hash.");
        }
        hash = Convert.ToHexString(actual);
        return owned;
    }

    private static void ValidateSemantic(XsrSemanticId semantic)
    {
        if (!semantic.IsAssigned || semantic.Value.Length > 256)
            throw new SidecarProtocolException("The cache semantic identity is missing or exceeds its budget.");
    }
}
