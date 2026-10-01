namespace Nexa.Services.Updates;

/// <summary>
/// The one-way upgrade gate of the launcher update flow. The legacy `1.4.x` line may upgrade
/// to any `2.0.0` build — alpha, beta, or stable — and a launcher on any `2.0.0` build is
/// never moved to a lower version: downgrades do not exist, and the same version is a no-op.
/// This type is the single decision point; the discovery and orchestration layers never
/// compare versions themselves.
/// </summary>
public static class UpdateEligibility
{
    public static UpdateEligibilityResult Evaluate(string? currentVersion, string? candidateVersion)
    {
        if (!UpdateVersion.TryParse(currentVersion, out UpdateVersion current))
        {
            return new UpdateEligibilityResult(
                UpdateEligibilityDecision.Unrecognized,
                $"The running version '{currentVersion}' is not a recognized launcher version.");
        }

        if (!UpdateVersion.TryParse(candidateVersion, out UpdateVersion candidate))
        {
            return new UpdateEligibilityResult(
                UpdateEligibilityDecision.Unrecognized,
                $"The candidate version '{candidateVersion}' is not a recognized launcher version.");
        }

        if (UpdateVersion.Equals(current, candidate))
        {
            return new UpdateEligibilityResult(
                UpdateEligibilityDecision.SameVersion,
                $"The candidate '{candidateVersion}' is the running version.");
        }

        // A deliberate CI hop follows the build the operator asked for: two CI builds of the
        // same numeric version are ordered by nothing but their commit, so any different
        // commit of the same version is allowed while an identical commit is a no-op.
        if (current.Stage == UpdateVersionStage.Ci && candidate.Stage == UpdateVersionStage.Ci
            && current.Major == candidate.Major && current.Minor == candidate.Minor
            && current.Patch == candidate.Patch)
        {
            return new UpdateEligibilityResult(
                UpdateEligibilityDecision.Allowed,
                $"Moving to CI build '{candidateVersion}'.");
        }

        int comparison = current.CompareTo(candidate);
        if (comparison < 0)
        {
            return new UpdateEligibilityResult(
                UpdateEligibilityDecision.Allowed,
                $"The candidate '{candidateVersion}' is newer than the running '{currentVersion}'.");
        }

        return new UpdateEligibilityResult(
            UpdateEligibilityDecision.Downgrade,
            $"The candidate '{candidateVersion}' is not newer than the running '{currentVersion}'; downgrades are never offered.");
    }
}
