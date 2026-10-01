using System.Globalization;

namespace Nexa.Services.Minecraft;

/// <summary>
/// Reads options.txt exactly as the game writes it: one `key:value` per line, UTF-8 with a
/// BOM, comments start with '#'. The parser is tolerant — an unreadable or partial file
/// reports Missing rather than throwing, because settings facts are advisory.
/// </summary>
public static class MinecraftOptionsReader
{
    public static async Task<MinecraftOptionsSnapshot> ReadAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        string path = Path.Combine(gameDirectory, "options.txt");
        if (!File.Exists(path))
        {
            return MinecraftOptionsSnapshot.Missing;
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        try
        {
            foreach (string line in (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
                         .Where(static line => line.Length > 0 && line[0] != '#'))
            {
                int split = line.IndexOf(':', StringComparison.Ordinal);
                if (split <= 0 || split == line.Length - 1)
                {
                    continue;
                }

                values[line[..split]] = line[(split + 1)..];
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return MinecraftOptionsSnapshot.Missing;
        }

        return new MinecraftOptionsSnapshot
        {
            Readable = true,
            RenderDistance = Int("renderDistance", 12),
            SimulationDistance = Int("simulationDistance", 12),
            MipmapLevels = Int("mipmapLevels", 4),
            GraphicsMode = Get("graphicsMode", "fancy"),
            Fullscreen = Get("fullscreen", "false") == "true",
            Particles = Get("particles", "all"),
            EntityDistance = Double("entityDistanceScale", 1.0),
            EntityShadows = Get("entityShadows", "true") == "true",
            ResourcePacks = Get("resourcePacks", ""),
        };

        int Int(string key, int fallback) =>
            values.TryGetValue(key, out string? raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;

        double Double(string key, double fallback) =>
            values.TryGetValue(key, out string? raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : fallback;

        string Get(string key, string fallback) => values.TryGetValue(key, out string? raw) && raw.Length > 0 ? raw : fallback;
    }
}
