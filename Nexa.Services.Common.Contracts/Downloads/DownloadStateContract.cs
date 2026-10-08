using Nexa.Xsr;

namespace Nexa.Services.Downloads;

/// <summary>Published active transfers; terminal transfers are removed by their producer.</summary>
public static class DownloadStateContract
{
    public static readonly XsrSemanticId TransfersKey = XsrSemanticId.Parse("download.transfers");
}
