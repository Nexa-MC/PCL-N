
using System.Text.Json.Serialization;

using Nexa.Xsr;


namespace Nexa.Services.Minecraft;

public interface IMinecraftInstanceSource
{
    ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default);
}

public sealed record MinecraftLibraryDirectory(string Path, string SelectedInstanceId = "", string Name = "")
{
    [JsonIgnore]
    public bool IsOfficial => Nexa.Core.PathIdentity.Comparer.Equals(Path, MinecraftLibraryContract.OfficialDirectory);
    [JsonIgnore]
    public string DisplayName => IsOfficial
        ? "官方文件夹"
        : string.IsNullOrWhiteSpace(Name)
            ? LeafName()
            : Name;

    private string LeafName()
    {
        string trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string leaf = System.IO.Path.GetFileName(trimmed);
        return leaf.Length == 0 ? Path : leaf;
    }
    [JsonIgnore]
    public bool HasName => IsOfficial || !string.IsNullOrWhiteSpace(Name);
}

public sealed record MinecraftLibrarySnapshot(long Revision, IReadOnlyList<MinecraftLibraryDirectory> Directories,
    string RootDirectory, IReadOnlyList<MinecraftInstanceDescriptor> Instances, string SelectedInstanceId,
    bool IsLoading, XsrError? Error = null)
{
    public bool IsProvisional { get; init; }
    public MinecraftInstanceDescriptor? SelectedInstance => IsProvisional ? null : Instances.FirstOrDefault(instance => instance.Id == SelectedInstanceId);
}

public sealed record MinecraftLibraryRefreshCommand;
public sealed record MinecraftLibraryDirectoryCommand(string Path, bool Add = false);
public sealed record MinecraftLibraryForgetCommand(string Path);
public sealed record MinecraftLibraryDeleteCommand(string RootDirectory, string InstanceId);
public sealed record MinecraftLibrarySelectCommand(string RootDirectory, string InstanceId);
public sealed record MinecraftLibraryRenameCommand(string Path, string Name);
