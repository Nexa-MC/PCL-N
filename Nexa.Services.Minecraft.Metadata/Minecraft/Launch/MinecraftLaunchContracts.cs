
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Libraries;
using Nexa.Services.Minecraft.ModLoaders;

namespace Nexa.Services.Minecraft.Launch;

public static class MinecraftLaunchPlanner
{
    public static MinecraftLaunchPlan CreatePlan(MinecraftLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlayerUuid);
        string root = Path.GetFullPath(request.MinecraftRootDirectory);
        string instance = Path.GetFullPath(request.InstanceDirectory);
        MinecraftModLoaderDescriptor loader = MinecraftModLoaderDetector.Detect(request.VersionJson);
        bool hasCleanroom = request.HasCleanroom || loader.Kind == MinecraftModLoaderKind.Cleanroom;
        JsonObject effectiveManifest = MergeManifests(request.VersionJson, request.InheritedVersionJsons);
        IReadOnlyList<MinecraftLibraryToken> libraries = MinecraftLibraryResolver.Resolve(new MinecraftLibraryResolutionRequest
        {
            VersionJson = effectiveManifest,
            MinecraftRootDirectory = root,
            TargetInstanceDirectory = instance,
            OperatingSystem = request.OperatingSystem,
            OperatingSystemVersion = request.OperatingSystemVersion,
            Is64BitArchitecture = request.Is64BitArchitecture,
            IsArm64Architecture = request.IsArm64Architecture,
            Features = request.Features,
            UseSystemGlfw = request.UseSystemGlfw,
        });
        if (!MinecraftVersionPaths.IsSafeReference(request.VersionId))
        {
            throw new InvalidDataException($"The version id is not a safe file name: {request.VersionId}");
        }

        MinecraftClientJarResolution clientJarResolution = MinecraftClientJarResolver.Resolve(new MinecraftClientJarResolutionRequest
        {
            VersionJson = request.VersionJson,
            InheritedVersionJsons = request.InheritedVersionJsons,
            VersionId = request.VersionId,
            InstanceDirectory = instance,
            MinecraftRootDirectory = root,
            ExplicitClientJarPath = request.ClientJarPath,
        });
        string clientJar = clientJarResolution.Path;

        MinecraftClasspathPlan classpath = MinecraftClasspathPlanner.CreatePlan(new MinecraftClasspathPlanRequest
        {
            Libraries = libraries,
            ClasspathHeadEntries = [clientJar, .. request.ClasspathHeadEntries],
            HasCleanroom = hasCleanroom,
        });
        string gameDirectory = request.IsolatedGameDirectory ? instance : root;
        string assetsRoot = Path.Combine(root, "assets");
        string assetsIndex = effectiveManifest["assetIndex"]?["id"]?.ToString() ?? effectiveManifest["assets"]?.ToString() ?? "legacy";
        string nativesDirectory = Path.GetFullPath(request.NativesDirectory ?? Path.Combine(instance, "natives"));
        string classpathSeparator = Path.PathSeparator.ToString();
        string libraryDirectory = Path.Combine(root, "libraries");
        string classpathValue = string.Join(classpathSeparator, classpath.Entries);
        MinecraftLaunchTokenContext tokenContext = MinecraftLaunchTokenContext.Create(
            request,
            gameDirectory,
            assetsRoot,
            assetsIndex,
            nativesDirectory,
            classpathSeparator,
            libraryDirectory,
            classpathValue);
        string mainClass = effectiveManifest["mainClass"]?.ToString() ?? loader.MainClass ?? "net.minecraft.client.main.Main";
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(request.AuthlibInjectorPath))
        {
            string agent = "-javaagent:" + request.AuthlibInjectorPath;
            if (!string.IsNullOrWhiteSpace(request.AuthlibServer)) agent += "=" + request.AuthlibServer;
            args.Add(agent);
            if (!string.IsNullOrWhiteSpace(request.AuthlibPrefetchedMetadata))
            {
                string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(request.AuthlibPrefetchedMetadata));
                args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + encoded);
            }
        }
        args.Add("-Xmx" + Math.Max(256, request.MemoryMegabytes) + "m");
        if (request.JavaMajorVersion >= 18) args.Add("-Dfile.encoding=COMPAT");
        AddVersionJvmArguments(args, effectiveManifest, request, tokenContext);
        AddTokens(args, request.CustomJvmArguments, tokenContext);
        // Legacy minecraftArguments manifests have no JVM section to declare the native directory.
        if (!args.Any(argument => argument.StartsWith("-Djava.library.path=", StringComparison.Ordinal)))
            args.Add("-Djava.library.path=" + nativesDirectory);
        // BootstrapLauncher matches JAR filenames, not launcher instance identities.
        for (int i = 0; i < args.Count; i++)
            if (args[i].StartsWith("-DignoreList=", StringComparison.Ordinal))
            {
                string filename = Path.GetFileName(clientJar);
                if (!args[i][13..].Split(',').Contains(filename, StringComparer.Ordinal))
                    args[i] += "," + filename;
            }
        args.Add("-cp");
        args.Add(classpathValue);
        args.Add(mainClass);

        int gameArgumentStart = args.Count;
        List<string> gameArgs = ReadGameArguments(effectiveManifest, request);
        if (gameArgs.Count == 0) gameArgs = Tokenize(effectiveManifest["minecraftArguments"]?.ToString());
        foreach (string token in gameArgs)
        {
            string value = tokenContext.Replace(token);
            if (value.Length > 0) args.Add(value);
        }
        if (!string.IsNullOrWhiteSpace(request.CustomGameArguments)) AddTokens(args, request.CustomGameArguments, tokenContext);
        if (gameArgs.Count == 0)
        {
            args.Add("--username"); args.Add(request.PlayerName);
            args.Add("--version"); args.Add(request.VersionId);
            args.Add("--gameDir"); args.Add(gameDirectory);
            args.Add("--assetsDir"); args.Add(assetsRoot);
            args.Add("--assetIndex"); args.Add(assetsIndex);
            args.Add("--uuid"); args.Add(request.PlayerUuid);
            args.Add("--accessToken"); args.Add(request.AccessToken);
            args.Add("--userType"); args.Add(request.IdentityMode switch { MinecraftLaunchIdentityMode.Offline => "legacy", MinecraftLaunchIdentityMode.ThirdParty => "mojang", _ => "msa" });
            args.Add("--versionType"); args.Add(request.VersionType);
        }
        if (request.Width > 0 && request.Height > 0)
        {
            SetGameOption(args, gameArgumentStart, "--width", request.Width);
            SetGameOption(args, gameArgumentStart, "--height", request.Height);
        }
        if (request.Fullscreen) args.Add("--fullscreen");
        AddJoinArguments(args, request);
        // Custom JVM arguments are part of the final argv too; known tokens are expanded and
        // unknown ones remain visible to the strict validation below.
        ReplaceArguments(args, tokenContext);
        EnsureNoUnresolvedTokens(args);
        return new MinecraftLaunchPlan(request.JavaExecutablePath, instance, args, classpath.Entries, libraries, loader)
        {
            MainClassIndex = gameArgumentStart - 1,
            NativesDirectory = nativesDirectory,
            InstanceDirectory = instance,
            GameDirectory = gameDirectory,
            MinecraftRootDirectory = root,
            ClientJarPath = clientJar,
            JavaMajorVersion = request.JavaMajorVersion,
            ProcessPriority = request.ProcessPriority,
            WindowTitle = request.WindowTitle,
            LauncherVisibility = request.LauncherVisibility,
            HeapLimitMiB = args.Count(static arg => arg.StartsWith("-Xmx", StringComparison.Ordinal)) == 1
                ? Math.Max(256, request.MemoryMegabytes) : -1,
            IsInheritedClientJar = clientJarResolution.IsInherited,
        };
    }

    private static void SetGameOption(List<string> args, int start, string name, int value)
    {
        for (int i = args.Count - 1; i >= start; i--)
        {
            if (args[i] == name)
            {
                if (i + 1 < args.Count) args.RemoveAt(i + 1);
                args.RemoveAt(i);
            }
            else if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) args.RemoveAt(i);
        }
        args.Add(name);
        args.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static List<string> ReadGameArguments(JsonObject json, MinecraftLaunchRequest request)
    {
        JsonArray? game = json["arguments"]?["game"]?.AsArray();
        if (game is null) return [];
        return ReadArgumentArray(game, request);
    }

    private static void AddVersionJvmArguments(
        List<string> target,
        JsonObject manifest,
        MinecraftLaunchRequest request,
        MinecraftLaunchTokenContext tokenContext)
    {
        JsonArray? jvm = manifest["arguments"]?["jvm"]?.AsArray();
        if (jvm is null) return;
        List<string> values = ReadArgumentArray(jvm, request);
        for (int index = 0; index < values.Count; index++)
        {
            string value = values[index];
            // The planner owns the canonical classpath. Only the explicit classpath switches and
            // their standalone placeholder are consumed; all other unknown tokens remain for the
            // final validation rather than being silently removed.
            if (value is "-cp" or "-classpath" or "${classpath}") continue;
            // A few third-party manifests encode the switch and its placeholder as one array
            // element. Consume that known pair as well, while retaining a pair containing an
            // unknown token so strict validation can report it.
            if (value.StartsWith("-cp ", StringComparison.Ordinal) || value.StartsWith("-classpath ", StringComparison.Ordinal))
            {
                int separator = value.IndexOf(' ');
                if (separator >= 0 && string.Equals(value[(separator + 1)..].Trim(), "${classpath}", StringComparison.Ordinal))
                    continue;
            }
            string replaced = tokenContext.Replace(value);
            if (replaced.Length > 0) target.Add(replaced);
        }
    }

    private static List<string> ReadArgumentArray(JsonArray array, MinecraftLaunchRequest request)
    {
        List<string> result = [];
        foreach (JsonNode? node in array)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrEmpty(text))
            {
                result.Add(text);
                continue;
            }

            if (node is JsonObject conditional && conditional["value"] is JsonNode conditionalValue && IsRuleAllowed(conditional["rules"], request))
            {
                if (conditionalValue is JsonArray values)
                {
                    result.AddRange(values.Select(item => item?.ToString() ?? string.Empty).Where(static item => item.Length > 0));
                }
                else if (conditionalValue.ToString().Length > 0)
                {
                    result.Add(conditionalValue.ToString());
                }
            }
        }
        return result;
    }

    private static bool IsRuleAllowed(JsonNode? node, MinecraftLaunchRequest request)
    {
        return MinecraftRuleEvaluator.IsAllowed(
            node,
            MinecraftRuleContext.From(
                request.OperatingSystem,
                request.OperatingSystemVersion,
                request.Is64BitArchitecture,
                request.IsArm64Architecture,
                request.Features));
    }

    internal static JsonObject MergeManifests(JsonObject current, IReadOnlyList<JsonObject> inherited)
    {
        JsonObject result = new();
        foreach (JsonObject manifest in inherited.Reverse())
        {
            foreach ((string key, JsonNode? value) in manifest)
                result[key] = value?.DeepClone();
        }
        foreach ((string key, JsonNode? value) in current)
        {
            if (key is "libraries" or "arguments") continue;
            result[key] = value?.DeepClone();
        }

        JsonArray libraries = [];
        foreach (JsonObject manifest in inherited.Reverse())
            if (manifest["libraries"] is JsonArray parentLibraries) foreach (JsonNode? library in parentLibraries) libraries.Add(library?.DeepClone());
        if (current["libraries"] is JsonArray currentLibraries) foreach (JsonNode? library in currentLibraries) libraries.Add(library?.DeepClone());
        if (libraries.Count > 0) result["libraries"] = libraries;

        JsonObject arguments = new();
        foreach (JsonObject manifest in inherited.Reverse())
            if (manifest["arguments"] is JsonObject parentArguments)
                foreach ((string key, JsonNode? value) in parentArguments)
                    if (value is JsonArray parentArray) arguments[key] = parentArray.DeepClone();
        if (current["arguments"] is JsonObject currentArguments)
        {
            if (currentArguments["game"] is JsonArray && arguments["game"] is null
                && current["minecraftArguments"] is null && result["minecraftArguments"] is { } legacyArguments)
                arguments["game"] = new JsonArray(Tokenize(legacyArguments.ToString()).Select(text => (JsonNode?)JsonValue.Create(text)).ToArray());
            foreach ((string key, JsonNode? value) in currentArguments)
            {
                if (value is JsonArray currentArray && arguments[key] is JsonArray existing)
                {
                    JsonArray combined = [];
                    foreach (JsonNode? item in existing) combined.Add(item?.DeepClone());
                    foreach (JsonNode? item in currentArray) combined.Add(item?.DeepClone());
                    arguments[key] = combined;
                }
                else arguments[key] = value?.DeepClone();
            }
        }
        if (arguments.Count > 0) result["arguments"] = arguments;
        return result;
    }

    private static void AddJoinArguments(List<string> args, MinecraftLaunchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Server))
        {
            if (request.ReleaseTime is { } release && release >= new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero))
            {
                args.Add("--quickPlayMultiplayer");
                args.Add(request.Server!);
            }
            else
            {
                string[] split = request.Server!.Split(':', 2);
                args.Add("--server"); args.Add(split[0]);
                if (split.Length == 2 && int.TryParse(split[1], out _)) { args.Add("--port"); args.Add(split[1]); }
            }
        }
        if (!string.IsNullOrWhiteSpace(request.WorldName)) args.AddRange(["--quickPlaySingleplayer", request.WorldName!]);
    }

    private static void EnsureNoUnresolvedTokens(IReadOnlyList<string> arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument.Contains("${", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The launch arguments contain an unresolved ${...} token: "
                    + argument);
            }
        }
    }

    private static void ReplaceArguments(List<string> arguments, MinecraftLaunchTokenContext context)
    {
        for (int index = 0; index < arguments.Count; index++)
            arguments[index] = context.Replace(arguments[index]);
    }

    private static void AddTokens(List<string> target, string? commandLine, MinecraftLaunchTokenContext? tokenContext = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return;
        foreach (string token in Tokenize(commandLine))
            target.Add(tokenContext?.Replace(token) ?? token);
    }

    private static List<string> Tokenize(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        bool quoted = false;
        foreach (char character in commandLine)
        {
            if (character == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
