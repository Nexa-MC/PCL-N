using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

public static class JavaManualDownloadContract
{
    public static readonly XsrSemanticId Preview = XsrSemanticId.Parse("java.runtime.manual.preview");
    public static readonly XsrSemanticId Install = XsrSemanticId.Parse("java.runtime.manual.install");
    public static readonly XsrSemanticId Cancel = XsrSemanticId.Parse("java.runtime.manual.cancel");
    public static readonly XsrSemanticId Status = XsrSemanticId.Parse("java.runtime.manual.status");
}
public sealed record JavaManualPreviewQuery(int Major, long ExpectedRegistryRevision);
public sealed record JavaManualInstallCommand(Guid PreviewId, bool AcceptLicense);
public sealed record JavaManualCancelCommand(Guid PreviewId);
public sealed record JavaManualStatusQuery(Guid PreviewId);
public sealed record JavaManualPreview(Guid Id, int Major, string Provider, string Version, string Platform,
    string TargetDirectory, int Files, long Bytes, string PlanFingerprint, IReadOnlyList<string> LicenseUrls, DateTimeOffset ExpiresAt);
public enum JavaManualInstallStatus { Downloading, Installed, InstalledUnverified, InstalledUnregistered, Canceled, Failed }
public sealed record JavaManualReceipt(Guid PreviewId, JavaManualInstallStatus Status, double Progress,
    int CompletedFiles, int TotalFiles, string Detail, string Executable, string ActualVersion, JavaBrand? ActualBrand, JavaArchitecture? ActualArchitecture);
