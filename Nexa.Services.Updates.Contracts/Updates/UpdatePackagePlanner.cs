

namespace Nexa.Services.Updates;

/// <summary>
/// Deployment-side inputs of the planner: where release assets live and whether that
/// distribution endpoint is the signed Cloudflare origin (GitHub fallbacks then never apply).
/// </summary>
public sealed record UpdatePlannerOptions(
    string DistributionBaseUrl,
    bool CloudflareOnly,
    string AssetNamePrefix = "PCL_N_");
