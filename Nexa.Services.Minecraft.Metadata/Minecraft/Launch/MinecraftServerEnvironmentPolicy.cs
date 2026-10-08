using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;

namespace Nexa.Services.Minecraft.Launch;

public static class MinecraftServerEnvironmentPolicy
{
    public static string? ValidateIdentity(MinecraftInstanceMetadata metadata, MinecraftLaunchIdentity identity)
    {
        if (!metadata.OfflineLaunchAllowed && identity.Mode == MinecraftLaunchIdentityMode.Offline)
            return "这个实例禁止使用离线档案，请选择有效的在线账户。";
        bool microsoft = identity.Mode == MinecraftLaunchIdentityMode.Microsoft;
        bool thirdParty = identity.Mode == MinecraftLaunchIdentityMode.ThirdParty;
        bool admitted = metadata.ServerLoginRequirement switch
        { 0 => true, 1 => microsoft, 2 => thirdParty, 3 => microsoft || thirdParty, _ => false };
        if (!admitted) return "账户类型不符合此实例的服务器登录要求。";
        if (thirdParty && metadata.ServerLoginRequirement is 2 or 3)
        {
            string? expected = NormalizeAuthEndpoint(metadata.AuthServerAddress), actual = NormalizeAuthEndpoint(identity.AuthServer);
            if (expected is null || actual != expected) return "所选账户的认证服务器与此实例要求不匹配。";
        }
        return null;
    }

    public static string? NormalizeAuthEndpoint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048 || address.Any(char.IsControl)
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || string.IsNullOrEmpty(uri.Host)) return null;
        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
    }

    public static IReadOnlyList<string> CompareEnvironment(MinecraftInstanceMetadata metadata, string gameVersion,
        MinecraftModLoaderKind loader, LaunchModInventory? inventory)
    {
        List<string> errors = [];
        if (metadata.ServerExpectedGameVersion.Length > 0 && metadata.ServerExpectedGameVersion != gameVersion)
            errors.Add("Minecraft 版本不匹配：需要 " + metadata.ServerExpectedGameVersion + "，当前 " + gameVersion + "。");
        if (metadata.ServerExpectedLoader.Length > 0 && !metadata.ServerExpectedLoader.Equals(loader.ToString(), StringComparison.OrdinalIgnoreCase))
            errors.Add("加载器不匹配：需要 " + metadata.ServerExpectedLoader + "，当前 " + loader + "。");
        if (metadata.ServerRequiredMods.Length > 0)
        {
            if (inventory is null || !inventory.Complete) errors.Add("无法完整读取模组清单，不能确认服务器环境匹配。");
            else
            {
                var installed = inventory.Mods.Where(mod => mod.Enabled).SelectMany(mod => mod.ProvidedIds.Keys.Prepend(mod.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string required in metadata.ServerRequiredMods)
                    if (!installed.Contains(required)) errors.Add("缺少服务器要求的模组：" + required + "。");
            }
        }
        return errors.AsReadOnly();
    }
}
