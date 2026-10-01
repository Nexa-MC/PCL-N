
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>
/// The current manifest followed by inherited manifests in nearest-parent-to-root order.
/// </summary>
public sealed record MinecraftResolvedVersionManifests(
    JsonObject Current,
    IReadOnlyList<JsonObject> Inherited);
