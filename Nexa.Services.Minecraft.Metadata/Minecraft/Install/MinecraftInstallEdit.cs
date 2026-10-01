using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Launch;



namespace Nexa.Services.Minecraft.Install;

public static class MinecraftInstallEditService
{
    public static Task<MinecraftInstallEditSnapshot> ReadAsync(MinecraftInstallEditQuery query, CancellationToken token = default) =>
        Task.Run(() => ReadCoreAsync(query, token), token);

    private static async Task<MinecraftInstallEditSnapshot> ReadCoreAsync(MinecraftInstallEditQuery query, CancellationToken token)
    {
        if (!MinecraftVersionPaths.IsSafeReference(query.InstanceId)) throw new InvalidDataException("版本目录无效。");
        string root = Path.GetFullPath(query.RootDirectory);
        string path = Nexa.Core.PathIdentity.Contained(root, $"versions/{query.InstanceId}/{query.InstanceId}.json");
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        var json = JsonNode.Parse(bytes)!.AsObject();
        List<InstallBuildSelection> selections = [];
        string? game = json["_nexaInstall"]?["game"]?.ToString();
        if (json["_nexaInstall"] is JsonObject receipt)
        {
            if (Enum.TryParse<InstallLoader>(receipt["loader"]?.ToString(), out var loader) && receipt["build"] is { } build)
                selections.Add(new(loader, build.ToString()));
            foreach (var addon in receipt["addons"] as JsonArray ?? [])
                if (Enum.TryParse<InstallLoader>(addon?["loader"]?.ToString(), out var kind) && addon?["build"] is { } value)
                    selections.Add(new(kind, value.ToString()));
        }
        {
            foreach (var lib in json["libraries"] as JsonArray ?? [])
            {
                string[] coordinate = (lib?["name"]?.ToString() ?? "").Split(':');
                if (coordinate.Length < 3) continue;
                InstallLoader? loader = (coordinate[0], coordinate[1]) switch
                {
                    ("net.minecraftforge", "forge") => InstallLoader.Forge,
                    ("net.neoforged", "neoforge" or "forge") => InstallLoader.NeoForge,
                    ("com.cleanroommc", "cleanroom") => InstallLoader.Cleanroom,
                    ("net.fabricmc", "fabric-loader") => InstallLoader.Fabric,
                    ("net.legacyfabric", "fabric-loader") => InstallLoader.LegacyFabric,
                    ("org.quiltmc", "quilt-loader") => InstallLoader.Quilt,
                    ("optifine", "OptiFine") => InstallLoader.OptiFine,
                    ("com.mumfrey", "liteloader") => InstallLoader.LiteLoader,
                    ("net.labymod", "LabyMod") => InstallLoader.LabyMod,
                    _ => null,
                };
                if (loader is null) continue;
                string build = coordinate[2];
                if (loader == InstallLoader.Forge || loader == InstallLoader.NeoForge && coordinate[1] == "forge")
                { int dash = build.IndexOf('-'); if (dash > 0) { game ??= build[..dash]; build = build[(dash + 1)..]; } }
                if (loader == InstallLoader.LabyMod)
                {
                    string url = lib?["downloads"]?["artifact"]?["url"]?.ToString() ?? "";
                    int separator = build.LastIndexOf('-');
                    if (separator > 0)
                        build = (url.Contains("/snapshot/", StringComparison.Ordinal) ? "snapshot" : "production") + "+" + build[..separator] + "+" + build[(separator + 1)..];
                }
                if (loader == InstallLoader.Cleanroom) game ??= "1.12.2";
                if (loader == InstallLoader.OptiFine) game ??= build.Split('_')[0];
                int index = selections.FindIndex(item => item.Loader == loader);
                if (index < 0) selections.Add(new(loader.Value, build));
                else selections[index] = new(loader.Value, build);
            }
        }
        var arguments = (json["arguments"]?["game"] as JsonArray ?? []).OfType<JsonValue>().Select(value => value.ToString()).ToArray();
        string? Option(string name)
        {
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
        void Select(InstallLoader loader, string version)
        {
            selections.RemoveAll(item => item.Loader == loader);
            selections.Insert(0, new(loader, version));
        }
        string? neo = Option("--fml.neoForgeVersion"), forge = Option("--fml.forgeVersion");
        if (neo is not null) Select(InstallLoader.NeoForge, neo);
        else if (forge is not null && !selections.Any(item => item.Loader is InstallLoader.Cleanroom or InstallLoader.NeoForge)) Select(InstallLoader.Forge, forge);
        if (selections.Any(item => item.Loader == InstallLoader.Cleanroom)) selections.RemoveAll(item => item.Loader == InstallLoader.Forge);
        game = Option("--fml.mcVersion") ?? game;
        game ??= json["_minecraftVersion"]?.ToString() ?? json["clientVersion"]?.ToString() ?? json["jar"]?.ToString();
        var current = json; HashSet<string> visited = [query.InstanceId];
        while (game is null && current["inheritsFrom"] is { } parent)
        {
            string id = parent.ToString();
            if (!MinecraftVersionPaths.IsSafeReference(id) || !visited.Add(id)) throw new InvalidDataException("版本继承关系无效。");
            current = await MinecraftVersionJsonReader.ReadAsync(Nexa.Core.PathIdentity.Contained(root, $"versions/{id}/{id}.json"), token).ConfigureAwait(false);
            if (current["inheritsFrom"] is null) game = current["id"]?.ToString();
        }
        game ??= json["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(game) || !MinecraftVersionPaths.IsSafeReference(game)) throw new InvalidDataException("无法确定 Minecraft 本体版本。");
        var managed = (json["_nexaInstall"]?["managedMods"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(file => new MinecraftInstallManagedFile(file["path"]!.ToString(), file["sha256"]!.ToString(),
                Enum.TryParse<InstallLoader>(file["loader"]?.ToString(), out var kind) ? kind : null)).ToList();
        string localMods = Nexa.Core.PathIdentity.Contained(root, $"versions/{query.InstanceId}/mods");
        string modsRelative = Directory.Exists(localMods) ? $"versions/{query.InstanceId}/mods" : "mods";
        string mods = Nexa.Core.PathIdentity.Contained(root, modsRelative);
        if (Directory.Exists(mods))
            foreach (string file in Directory.EnumerateFiles(mods, "*.jar"))
            {
                token.ThrowIfCancellationRequested();
                InstallBuildSelection? component = null;
                try
                {
                    using var archive = ZipFile.OpenRead(file);
                    var entry = archive.GetEntry("fabric.mod.json") ?? archive.GetEntry("quilt.mod.json");
                    if (entry is not null && entry.Length <= 1024 * 1024)
                    {
                        using var reader = new StreamReader(entry.Open());
                        var descriptor = JsonNode.Parse(await reader.ReadToEndAsync(token).ConfigureAwait(false))!;
                        descriptor = descriptor["quilt_loader"] ?? descriptor;
                        InstallLoader? kind = descriptor["id"]?.ToString() switch
                        { "fabric-api" => InstallLoader.FabricApi, "qsl" => InstallLoader.Qsl, "optifabric" => InstallLoader.OptiFabric, _ => null };
                        if (kind is not null && descriptor["version"] is { } version) component = new(kind.Value, version.ToString());
                    }
                    string filename = Path.GetFileNameWithoutExtension(file);
                    if (component is null && filename.StartsWith("OptiFine_", StringComparison.OrdinalIgnoreCase)) component = new(InstallLoader.OptiFine, filename[9..]);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { continue; }
                if (component is null) continue;
                int selectedIndex = selections.FindIndex(item => item.Loader == component.Loader);
                if (selectedIndex < 0) selections.Add(component);
                else selections[selectedIndex] = component;
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!managed.Any(item => item.Path == relative))
                {
                    await using var stream = File.OpenRead(file);
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    managed.Add(new(relative, hash, component.Loader));
                }
            }
        return new(root, query.InstanceId, game, selections.AsReadOnly(), Convert.ToHexString(SHA256.HashData(bytes)))
        { ManagedMods = managed.AsReadOnly(), ModsRelativeDirectory = modsRelative };
    }
}
