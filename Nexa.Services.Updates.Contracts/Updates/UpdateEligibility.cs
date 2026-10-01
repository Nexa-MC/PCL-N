

namespace Nexa.Services.Updates;

/// <summary>
/// The eligibility decision for one offered update. Only <see cref="Allowed"/> may proceed;
/// every other outcome must be refused without offering the candidate.
/// </summary>
public enum UpdateEligibilityDecision
{
    Allowed,
    SameVersion,
    Downgrade,
    Unrecognized,
}

/// <summary>
/// One eligibility verdict with the versions it compared.
/// </summary>
/// <param name="Decision">The verdict.</param>
/// <param name="Reason">A stable, human-readable explanation.</param>
public readonly record struct UpdateEligibilityResult(
    UpdateEligibilityDecision Decision,
    string Reason)
{
    public bool IsAllowed => Decision == UpdateEligibilityDecision.Allowed;
}
