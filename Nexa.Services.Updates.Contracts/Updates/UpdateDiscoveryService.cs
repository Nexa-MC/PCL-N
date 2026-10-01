

namespace Nexa.Services.Updates;

/// <summary>
/// The outcome of one update discovery. The one-way gate speaks first: only an
/// <see cref="UpdateEligibilityDecision.Allowed"/> decision carries a package, and that
/// package is either the plain full fallback or an index-planned patch/block package.
/// </summary>
/// <param name="Decision">The eligibility verdict for the candidate.</param>
/// <param name="Package">The planned package, or null when the candidate is not offered.</param>
public sealed record UpdateDiscoveryResult(UpdateEligibilityDecision Decision, UpdatePackage? Package)
{
    public bool IsAllowed => Decision == UpdateEligibilityDecision.Allowed && Package is not null;
}
