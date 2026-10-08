using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceLocalDocumentsQuery(string InstanceDirectory);
public sealed record InstanceLocalDocument(string Kind, string RelativePath, string Status, IReadOnlyList<string> Lines, bool Truncated);
public sealed record InstanceLocalDocumentsSnapshot(string InstanceDirectory, DateTimeOffset CapturedAt,
    IReadOnlyList<InstanceLocalDocument> Documents, bool Complete);

public static class InstanceLocalDocumentsContract
{
    public static readonly XsrSemanticId Query = XsrSemanticId.Parse("minecraft.instance.local-documents.query");
}
