namespace Nexa.Services.Network;

/// <summary>One committed request transport policy. Credentials are local to the owning Host.</summary>
public sealed record NetworkPreferences
{
    public int ProxyMode { get; init; } = 1;
    public string ProxyAddress { get; init; } = string.Empty;
    public string ProxyUser { get; init; } = string.Empty;
    public string ProxyPassword { get; init; } = string.Empty;
    public bool DnsOverHttps { get; init; } = true;
    public string IpStack { get; init; } = "auto";
    public bool OfficialProviderEnabled { get; init; } = true;
    public bool ModrinthProviderEnabled { get; init; } = true;
    public bool CurseForgeProviderEnabled { get; init; } = true;
    public bool MirrorProviderEnabled { get; init; } = true;
    public bool TraceEnabled { get; init; }
    public bool AutoDiagnose { get; init; }

    public override string ToString() => $"NetworkPreferences {{ ProxyMode = {ProxyMode}, DnsOverHttps = {DnsOverHttps}, IpStack = {IpStack} }}";

    /// <summary>Admission shared by saved settings and transport generation creation.</summary>
    public static bool IsValidProxyAddress(string text) => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https" or "socks5" && uri.Host.Length > 0
        && uri.UserInfo.Length == 0 && uri.AbsolutePath is "" or "/"
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !text.Any(char.IsControl);

    public void Validate()
    {
        if (ProxyMode is < 0 or > 2 || ProxyMode == 2 && !IsValidProxyAddress(ProxyAddress))
            throw new ArgumentException("The network proxy configuration is invalid.");
        if (IpStack is not ("auto" or "ipv4" or "ipv6")) throw new ArgumentException("The preferred IP stack is invalid.");
        if (ProxyUser.Any(char.IsControl) || ProxyPassword.Any(char.IsControl))
            throw new ArgumentException("Proxy credentials cannot contain control characters.");
    }
}
