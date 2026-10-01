

using Nexa.Xsr;

namespace Nexa.Services.Updates;

public sealed record NexaUpdateQuery(string CurrentVersion, string RuntimeIdentifier, string Channel);
public sealed record NexaUpdateOffer(string Version, string InstallerUrl, string PortableUrl, string ReleaseUrl);
public sealed record NexaUpdateStatus(NexaUpdateOffer? Offer);
public static class NexaUpdateContract
{
    public static readonly XsrSemanticId Check = XsrSemanticId.Parse("nexa.update.check");
}
