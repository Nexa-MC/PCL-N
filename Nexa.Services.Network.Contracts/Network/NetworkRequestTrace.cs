namespace Nexa.Services.Network;

/// <summary>No URL path, query, user information, headers, body or credentials are retained.</summary>
public sealed record NetworkRequestTrace(long Sequence, DateTimeOffset Timestamp, string Host,
    string Kind, int? StatusCode, double ElapsedMilliseconds, string? ErrorKind);

public enum NetworkContentProvider { Unclassified, Official, Modrinth, CurseForge, Mirror }

public static class NetworkProviderAdmission
{
    public static NetworkContentProvider Classify(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string host = uri.IdnHost;
        if (host is "api.modrinth.com" or "cdn.modrinth.com") return NetworkContentProvider.Modrinth;
        if (host is "api.curseforge.com" or "media.forgecdn.net" or "mediafilez.forgecdn.net" or "edge.forgecdn.net") return NetworkContentProvider.CurseForge;
        if (host is "piston-meta.mojang.com" or "piston-data.mojang.com" or "libraries.minecraft.net"
            or "resources.download.minecraft.net" or "launchermeta.mojang.com" or "launcher.mojang.com") return NetworkContentProvider.Official;
        if (host is "bmclapi2.bangbang93.com" or "bmclapi.bangbang93.com" or "download.mcbbs.net"
            or "mod.mcimirror.top" or "mcim-files.pysio.online") return NetworkContentProvider.Mirror;
        return NetworkContentProvider.Unclassified;
    }

    public static bool IsAllowed(NetworkPreferences preferences, Uri uri)
    {
        if (uri.IdnHost is "mod.mcimirror.top" or "mcim-files.pysio.online")
        {
            if (!preferences.MirrorProviderEnabled) return false;
            if (uri.AbsolutePath.StartsWith("/modrinth/", StringComparison.Ordinal)
                || uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal)) return preferences.ModrinthProviderEnabled;
            if (uri.AbsolutePath.StartsWith("/curseforge/", StringComparison.Ordinal)
                || uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)) return preferences.CurseForgeProviderEnabled;
        }
        return Classify(uri) switch
        {
            NetworkContentProvider.Official => preferences.OfficialProviderEnabled,
            NetworkContentProvider.Modrinth => preferences.ModrinthProviderEnabled,
            NetworkContentProvider.CurseForge => preferences.CurseForgeProviderEnabled,
            NetworkContentProvider.Mirror => preferences.MirrorProviderEnabled,
            _ => true,
        };
    }
}
