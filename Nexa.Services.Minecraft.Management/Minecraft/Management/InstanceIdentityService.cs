using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Core.Media;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceIdentityService
{
    private static readonly MinecraftInstanceMetadataStore Metadata = new();
    private const int MetadataBudget = 512 * 1024;

    private static string ValidateInstance(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new InvalidDataException("实例目录必须是完整路径。");
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (Directory.GetParent(instance) is not { Name: "versions", Parent: not null }
            || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance)) || !Directory.Exists(instance))
            throw new InvalidDataException("请选择 versions 下的真实实例。");
        RecoveryBlobStore.CheckLinks(instance); return instance;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int budget, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, useAsync: true);
        if (file.Length > budget) throw new InvalidDataException("文件超过允许大小。");
        using var contents = new MemoryStream(); byte[] buffer = new byte[8192]; int read;
        while ((read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, budget - contents.Length + 1)), token).ConfigureAwait(false)) > 0)
        { if (contents.Length + read > budget) throw new InvalidDataException("文件实际大小超过允许限制。"); contents.Write(buffer, 0, read); }
        return contents.ToArray();
    }

    private static string ExistingPath(string instance)
    {
        string path = Metadata.GetMetadataPath(instance); RecoveryBlobStore.CheckLinks(path);
        if (File.Exists(path)) return path;
        string legacy = Path.Combine(instance, "PCL", MinecraftInstanceMetadataStore.MetadataFileName); RecoveryBlobStore.CheckLinks(legacy);
        return File.Exists(legacy) ? legacy : path;
    }

    private static async Task<(string Revision, MinecraftInstanceMetadata Metadata)> ReadStrictAsync(string instance, CancellationToken token)
    {
        string path = ExistingPath(instance);
        if (!File.Exists(path)) return ("missing", new());
        byte[] bytes = await ReadBoundedAsync(path, MetadataBudget, token).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize(bytes, MinecraftJsonContext.Default.MinecraftInstanceMetadata)
            ?? throw new InvalidDataException("现有实例信息无效，未覆盖文件。");
        if (metadata.SchemaVersion != MinecraftInstanceMetadata.CurrentSchemaVersion)
            throw new InvalidDataException("不支持现有实例信息版本，未覆盖文件。");
        MinecraftInstanceMetadataStore.ValidateShape(metadata);
        ValidateOwnedShape(metadata);
        return (Path.GetFileName(Path.GetDirectoryName(path)) + ":" + Convert.ToHexString(SHA256.HashData(bytes)), metadata);
    }

    private static void ValidateOwnedShape(MinecraftInstanceMetadata metadata)
    {
        static void StoredText(string? value, int budget)
        {
            if (value is null || value.Length > budget)
                throw new InvalidDataException("现有实例信息包含空值或超过限制的受管字段，未覆盖文件。");
        }
        static void StoredList(string[]? values, int count, int length)
        {
            if (values is null || values.Length > count || values.Any(value => value is null || value.Length > length))
                throw new InvalidDataException("现有实例信息包含空值或超过限制的集合，未覆盖文件。");
        }
        StoredText(metadata.Identity, 128); StoredText(metadata.DisplayName, 256); StoredText(metadata.Description, 8192);
        StoredText(metadata.LogoPath, 4096); StoredText(metadata.Group, 128); StoredText(metadata.Notes, 8192);
        StoredText(metadata.CustomInfo, 512); StoredText(metadata.AuthServerAddress, 2048);
        StoredText(metadata.ModpackProjectId, 256); StoredText(metadata.ModpackVersion, 128);
        StoredText(metadata.AuthRegisterAddress, 2048); StoredText(metadata.AuthServerDisplayName, 256);
        StoredText(metadata.ServerToEnter, 512); StoredText(metadata.ServerExpectedGameVersion, 64); StoredText(metadata.ServerExpectedLoader, 64);
        StoredList(metadata.Tags, 32, 64); StoredList(metadata.ServerRequiredMods, 128, 128);
    }

    public static async Task<InstanceIdentitySnapshot> ReadAsync(InstanceIdentityQuery query, CancellationToken token = default)
    {
        string instance = ValidateInstance(query.InstanceDirectory);
        var current = await ReadStrictAsync(instance, token).ConfigureAwait(false);
        var metadata = current.Metadata;
        string iconPath = metadata.LogoPath.Length == 0 ? "" : Path.GetFullPath(metadata.LogoPath, instance);
        PngImage? icon = null; string iconNotice = "";
        if (iconPath.Length > 0)
        {
            try { icon = PngImage.TryCreate(await ReadBoundedAsync(iconPath, 1024 * 1024, token).ConfigureAwait(false)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { iconNotice = "无法读取实例图标，请重新选择本地 PNG。"; }
            if (icon is null && iconNotice.Length == 0) iconNotice = "图标必须是完整静态 PNG，至多 1 MiB、1024×1024。";
        }
        var edit = await MinecraftInstallEditService.ReadAsync(new(Directory.GetParent(instance)!.Parent!.FullName, Path.GetFileName(instance)), token).ConfigureAwait(false);
        var manifest = await MinecraftVersionJsonReader.ReadAsync(Path.Combine(instance, Path.GetFileName(instance) + ".json"), token).ConfigureAwait(false);
        MinecraftModLoaderKind loader = MinecraftModLoaderDetector.Detect(manifest).Kind;
        LaunchModInventory? inventory = metadata.ServerRequiredMods.Length == 0 ? null
            : await LaunchModInventoryReader.ReadAsync(metadata.InstanceIsolation ? instance : Directory.GetParent(instance)!.Parent!.FullName, token).ConfigureAwait(false);
        return new(current.Revision, metadata.Identity,
            new(metadata.DisplayName, metadata.Description, iconPath, metadata.IsStarred, Array.AsReadOnly(metadata.Tags), metadata.Group, metadata.Notes, metadata.CustomInfo)
            { InstanceIsolation = metadata.InstanceIsolation, ModpackProject = metadata.ModpackProjectId, ModpackVersion = metadata.ModpackVersion },
            new(metadata.ServerLoginRequirement, metadata.AuthServerAddress, metadata.AuthRegisterAddress, metadata.AuthServerDisplayName,
                metadata.ServerToEnter, metadata.OfflineLaunchAllowed, metadata.ServerExpectedGameVersion, metadata.ServerExpectedLoader, Array.AsReadOnly(metadata.ServerRequiredMods)), metadata.AuthSettingsLocked)
        {
            Icon = icon,
            IconNotice = iconNotice,
            EnvironmentRequirementsDeclared = metadata.ServerExpectedGameVersion.Length > 0 || metadata.ServerExpectedLoader.Length > 0 || metadata.ServerRequiredMods.Length > 0,
            EnvironmentMismatches = MinecraftServerEnvironmentPolicy.CompareEnvironment(metadata, edit.GameVersion, loader, inventory),
            Loader = loader.ToString(),
            LaunchCount = metadata.LaunchCount,
            JavaPreference = metadata.JavaSelectionMode == 2 && metadata.SelectedJavaPath.Length > 0
                ? metadata.SelectedJavaPath : "继承设置中的 Java 偏好；启动时验证兼容性",
        };
    }

    public static async Task<XsrResult> SaveAsync(InstanceIdentitySaveCommand command, CancellationToken token = default)
    {
        try
        {
            string instance = ValidateInstance(command.InstanceDirectory);
            Validate(command.Fields, command.Server);
            if (command.Fields.IconPath.Length > 0)
            {
                var icon = PngImage.TryCreate(await ReadBoundedAsync(command.Fields.IconPath, 1024 * 1024, token).ConfigureAwait(false));
                if (icon is null) throw new InvalidDataException("请选择至多 1 MiB、1024×1024 的完整静态 PNG。");
            }
            using var operation = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(instance)!.Parent!.FullName, token).ConfigureAwait(false);
            var strict = await ReadStrictAsync(instance, token).ConfigureAwait(false);
            if (strict.Revision != command.ExpectedRevision) throw new IOException("实例信息已变化，请刷新后重新编辑。");
            await Metadata.UpdateValidatedAsync(instance, async (current, updateToken) =>
            {
                // Metadata.Update owns the shared store lock. Recheck the actual file there,
                // so other store instances cannot overwrite launch count or newer edits.
                string path = ExistingPath(instance);
                string revision = File.Exists(path) ? Path.GetFileName(Path.GetDirectoryName(path)) + ":" +
                    Convert.ToHexString(SHA256.HashData(await ReadBoundedAsync(path, MetadataBudget, updateToken).ConfigureAwait(false))) : "missing";
                if (revision != command.ExpectedRevision) throw new IOException("实例信息已变化，未覆盖其他修改。");
                if (current.AuthSettingsLocked && (current.ServerLoginRequirement != command.Server.LoginRequirement
                    || current.AuthServerAddress != command.Server.AuthServerAddress || current.AuthRegisterAddress != command.Server.AuthRegisterAddress
                    || current.AuthServerDisplayName != command.Server.AuthServerDisplayName))
                    throw new InvalidDataException("此实例的认证配置已锁定，不能修改登录要求或认证端点。");
                return current with
                {
                    Identity = current.Identity.Length == 0 ? Guid.NewGuid().ToString("N") : current.Identity,
                    DisplayName = command.Fields.DisplayName,
                    Description = command.Fields.Description,
                    LogoPath = command.Fields.IconPath,
                    IsStarred = command.Fields.Starred,
                    Tags = command.Fields.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Group = command.Fields.Group,
                    Notes = command.Fields.Notes,
                    CustomInfo = command.Fields.CustomInfo,
                    InstanceIsolation = command.Fields.InstanceIsolation,
                    ModpackProjectId = command.Fields.ModpackProject,
                    ModpackVersion = command.Fields.ModpackVersion,
                    ServerLoginRequirement = command.Server.LoginRequirement,
                    AuthServerAddress = command.Server.AuthServerAddress,
                    AuthRegisterAddress = command.Server.AuthRegisterAddress,
                    AuthServerDisplayName = command.Server.AuthServerDisplayName,
                    ServerToEnter = command.Server.DefaultServer,
                    OfflineLaunchAllowed = command.Server.OfflineLaunchAllowed,
                    ServerExpectedGameVersion = command.Server.ExpectedGameVersion,
                    ServerExpectedLoader = command.Server.ExpectedLoader,
                    ServerRequiredMods = command.Server.RequiredMods.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                };
            }, token).ConfigureAwait(false);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
    }

    private static void Validate(InstanceIdentityFields fields, InstanceServerPolicyFields server)
    {
        ArgumentNullException.ThrowIfNull(fields); ArgumentNullException.ThrowIfNull(server);
        static void Text(string value, int limit, bool multiline = false)
        {
            if (value is null || value.Length > limit || value.Any(character => char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')))
                throw new InvalidDataException("实例信息文字过长或包含禁止的控制字符。");
        }
        Text(fields.DisplayName, 256); Text(fields.Description, 8192, true); Text(fields.Group, 128); Text(fields.Notes, 8192, true); Text(fields.CustomInfo, 512);
        Text(fields.IconPath, 4096);
        Text(fields.ModpackProject, 256); Text(fields.ModpackVersion, 128);
        Text(server.AuthServerAddress, 2048); Text(server.AuthRegisterAddress, 2048);
        if (fields.Tags is null || fields.Tags.Count > 32 || fields.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl)))
            throw new InvalidDataException("标签最多 32 个，每个至多 64 个字符。");
        if (fields.IconPath.Length > 0 && (!Path.IsPathFullyQualified(fields.IconPath) || fields.IconPath.Length > 4096))
            throw new InvalidDataException("图标必须是本地完整 PNG 路径。");
        if (server.LoginRequirement is < 0 or > 3) throw new InvalidDataException("未知的服务器登录要求。");
        if (server.AuthServerAddress.Length > 0 && MinecraftServerEnvironmentPolicy.NormalizeAuthEndpoint(server.AuthServerAddress) is null
            || server.LoginRequirement is 2 or 3 && server.AuthServerAddress.Length == 0)
            throw new InvalidDataException("第三方认证要求有效 HTTPS 端点，不含凭据、查询或片段。");
        if (server.AuthRegisterAddress.Length > 0 && MinecraftServerEnvironmentPolicy.NormalizeAuthEndpoint(server.AuthRegisterAddress) is null)
            throw new InvalidDataException("注册页面必须是有效 HTTPS 地址。");
        Text(server.AuthServerDisplayName, 256); Text(server.DefaultServer, 512);
        if (server.DefaultServer.Any(char.IsWhiteSpace) || server.DefaultServer.Contains("://", StringComparison.Ordinal))
            throw new InvalidDataException("默认服务器填写主机和可选端口，不填写 URI。");
        Text(server.ExpectedGameVersion, 64); Text(server.ExpectedLoader, 64);
        if (server.ExpectedLoader.Length > 0 && (!Enum.TryParse<MinecraftModLoaderKind>(server.ExpectedLoader, true, out var loader) || !Enum.IsDefined(loader)))
            throw new InvalidDataException("未知的服务器加载器要求。");
        if (server.RequiredMods is null || server.RequiredMods.Count > 128 || server.RequiredMods.Any(id => id is null || id.Length is 0 or > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))))
            throw new InvalidDataException("必需模组最多 128 个，请填写模组 ID。");
    }
}
