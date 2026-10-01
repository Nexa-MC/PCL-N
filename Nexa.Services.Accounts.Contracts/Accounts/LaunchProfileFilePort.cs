


namespace Nexa.Services.Accounts;

/// <summary>
/// Persistence boundary of the launch profile store.
/// </summary>
public interface ILaunchProfilePort
{
    /// <summary>
    /// Reads the whole profile set. A missing store is an empty set, not a failure. Unreadable
    /// or unsupported-schema stores surface as <see cref="IOException"/>.
    /// </summary>
    LaunchProfileSet Load();

    /// <summary>
    /// Replaces the persisted store with exactly the given set.
    /// </summary>
    void Save(LaunchProfileSet profiles);
}

/// <summary>Asynchronous initialization for stores backed by OS key rings.</summary>
public interface IAsyncLaunchProfilePort : ILaunchProfilePort
{
    bool RequiresAsyncInitialization => true;
    ValueTask<LaunchProfileSet> LoadAsync(CancellationToken cancellationToken = default);
}
