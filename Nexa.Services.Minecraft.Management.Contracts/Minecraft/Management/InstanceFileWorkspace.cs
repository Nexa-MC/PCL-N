using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public sealed record InstanceFileListQuery(string InstanceDirectory, string Area, string RelativeDirectory = "");
public sealed record InstanceFileEntry(string Name, bool IsDirectory, long? Size, long ModifiedUtcTicks);
public sealed record InstanceFileListing(string InstanceDirectory, string Area, string RelativeDirectory, string Directory, IReadOnlyList<InstanceFileEntry> Entries, bool Complete);
public sealed record InstanceFileReadQuery(string InstanceDirectory, string Area, string RelativePath, long ExpectedSize, long ExpectedModifiedUtcTicks);
public sealed record InstanceFileDocument(InstanceFileReadQuery File, string Text, string Revision, string Encoding, bool Editable);
public sealed record InstanceFileSavePreviewQuery(InstanceFileDocument Original, string Text);
public sealed record InstanceFileSavePreview(InstanceFileDocument Original, string Text, string Revision, long PreviousBytes, long UpdatedBytes, int PreviousLines, int UpdatedLines);
public sealed record InstanceFileSaveCommand(InstanceFileSavePreview Preview);
public static class InstanceFileWorkspaceContract
{
    public static readonly XsrSemanticId List = XsrSemanticId.Parse("minecraft.instance.files.list");
    public static readonly XsrSemanticId Read = XsrSemanticId.Parse("minecraft.instance.files.read");
    public static readonly XsrSemanticId Preview = XsrSemanticId.Parse("minecraft.instance.files.preview-save");
    public static readonly XsrSemanticId Save = XsrSemanticId.Parse("minecraft.instance.files.save");
}
