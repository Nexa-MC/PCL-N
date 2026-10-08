using Nexa.Services.Caching;
using Nexa.Services.Minecraft;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask LibraryOfflineRootRaceRetainsRealSnapshotAndAcceptsEmptyRoot()
    {
        using LibraryFixture fixture = new();
        string root = fixture.AddRoot("offline-game"), offlineRoot = root + ".offline";
        LibraryVersion(root, "local");
        using SharedStateCache cache = new();
        MinecraftInstanceDiscovery discovery = new(cache, fixture.AddRoot("discovery-cache"));
        using (var initial = new MinecraftLibraryService(fixture.Host.Settings, root, discovery))
        {
            AssertTrue((await initial.RefreshAsync()).IsSuccess);
            AssertEqual("local", fixture.Snapshot.SelectedInstance!.Id);
        }
        string remembered = fixture.Host.Settings.GetValue<string>(MinecraftLibraryService.SettingKey).Value;

        // Move the real root after Library's existence precheck and before actual discovery.
        using (var racing = new MinecraftLibraryService(fixture.Host.Settings, root,
            new MovingLibraryRootSource(discovery, root, offlineRoot)))
        {
            AssertFalse((await racing.RefreshAsync()).IsSuccess);
            AssertTrue(fixture.Snapshot.IsProvisional);
            AssertFalse(fixture.Snapshot.IsLoading);
            AssertEqual("local", fixture.Snapshot.Instances.Single().Id);
            AssertTrue(fixture.Snapshot.SelectedInstance is null);
            AssertTrue(fixture.Snapshot.Error is not null);
            AssertEqual(remembered, fixture.Host.Settings.GetValue<string>(MinecraftLibraryService.SettingKey).Value);
        }
        bool unavailable = false;
        try { await discovery.DiscoverAsync(root); }
        catch (IOException) { unavailable = true; }
        AssertTrue(unavailable);
        AssertEqual(0, (await new MinecraftInstanceDiscovery().DiscoverAsync(root)).Count);
        AssertEqual("local", (await discovery.LoadSnapshotAsync(root))!.Instances.Single().Id);

        using var restarted = new MinecraftLibraryService(fixture.Host.Settings, root, discovery);
        AssertFalse((await restarted.RefreshAsync()).IsSuccess);
        AssertTrue(fixture.Snapshot.IsProvisional);
        AssertEqual("local", fixture.Snapshot.Instances.Single().Id);
        AssertTrue(fixture.Snapshot.SelectedInstance is null);
        AssertEqual(remembered, fixture.Host.Settings.GetValue<string>(MinecraftLibraryService.SettingKey).Value);

        // A reachable root without versions is authoritative empty, not an offline failure.
        Directory.CreateDirectory(root);
        AssertTrue((await restarted.RefreshAsync()).IsSuccess);
        AssertFalse(fixture.Snapshot.IsProvisional);
        AssertEqual(0, fixture.Snapshot.Instances.Count);
        AssertTrue(fixture.Snapshot.Error is null);
        AssertEqual(0, (await discovery.LoadSnapshotAsync(root))!.Instances.Count);
        Directory.CreateDirectory(Path.Combine(root, "versions"));
        AssertEqual(0, (await discovery.DiscoverAsync(root)).Count);
    }

    private sealed class MovingLibraryRootSource(MinecraftInstanceDiscovery discovery, string root, string offlineRoot)
        : IMinecraftInstanceSnapshotSource
    {
        public ValueTask<MinecraftInstanceDiscoverySnapshot?> LoadSnapshotAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
            => discovery.LoadSnapshotAsync(minecraftRootDirectory, cancellationToken);
        public ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
        {
            Directory.Move(root, offlineRoot);
            return discovery.DiscoverAsync(minecraftRootDirectory, cancellationToken);
        }
        public ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> RefreshAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
            => discovery.RefreshAsync(minecraftRootDirectory, cancellationToken);
    }

    private static async ValueTask LibrarySnapshotsAreDisplayOnlyUntilReconciled()
    {
        using LibraryFixture fixture = new();
        string root = fixture.AddRoot("durable");
        var source = new SnapshotLibrarySource(root);
        using var library = new MinecraftLibraryService(fixture.Host.Settings, root, source);
        var originalSelection = fixture.Host.Settings.GetValue<string>(MinecraftLibraryService.SettingKey).Value;
        var initial = library.RefreshAsync();
        AssertTrue(fixture.Snapshot.IsProvisional);
        AssertTrue(fixture.Snapshot.IsLoading);
        AssertEqual("old", fixture.Snapshot.Instances.Single().Id);
        AssertTrue(fixture.Snapshot.SelectedInstance is null);
        AssertFalse(library.SelectInstance(root, "old").IsSuccess);
        AssertFalse((await library.DeleteInstanceAsync(root, "old")).IsSuccess);
        source.Current.SetException(new IOException("offline"));
        AssertFalse((await initial).IsSuccess);
        AssertTrue(fixture.Snapshot.IsProvisional);
        AssertFalse(fixture.Snapshot.IsLoading);
        AssertEqual("old", fixture.Snapshot.Instances.Single().Id);
        AssertTrue(fixture.Snapshot.Error is not null);
        AssertEqual(originalSelection, fixture.Host.Settings.GetValue<string>(MinecraftLibraryService.SettingKey).Value);
        source.Current = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconcile = library.RefreshAsync();
        source.Current.SetResult([LibraryDescriptor(root, "live")]);
        AssertTrue((await reconcile).IsSuccess);
        AssertFalse(fixture.Snapshot.IsProvisional);
        AssertEqual("live", fixture.Snapshot.SelectedInstance!.Id);
        AssertTrue(library.SelectInstance(root, "live").IsSuccess);
    }

    private sealed class SnapshotLibrarySource(string root) : IMinecraftInstanceSnapshotSource
    {
        public TaskCompletionSource<IReadOnlyList<MinecraftInstanceDescriptor>> Current { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<MinecraftInstanceDiscoverySnapshot?> LoadSnapshotAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MinecraftInstanceDiscoverySnapshot?>(new(root, DateTimeOffset.UtcNow, [LibraryDescriptor(root, "old")]));
        public async ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> DiscoverAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
            => await Current.Task.ConfigureAwait(false);
        public ValueTask<IReadOnlyList<MinecraftInstanceDescriptor>> RefreshAsync(string minecraftRootDirectory, CancellationToken cancellationToken = default)
            => DiscoverAsync(minecraftRootDirectory, cancellationToken);
    }
}
