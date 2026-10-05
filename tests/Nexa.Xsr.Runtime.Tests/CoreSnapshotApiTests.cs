using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static XsrStateStore InitialPrimitiveStore(string owner = "initial", string firstSemantic = "initial.count",
        bool firstAsLong = false, bool firstAsObject = false)
    {
        XsrStateStoreBuilder builder = new();
        XsrSemanticId first = XsrSemanticId.Parse(firstSemantic);
        if (firstAsObject) builder.Cell<object>(first, owner);
        else if (firstAsLong) builder.Cell<long>(first, owner);
        else builder.Cell<int>(first, owner);
        builder.Cell<byte[]>(XsrSemanticId.Parse("initial.bytes"), owner);
        return builder.Build();
    }

    private static void FillInitialPrimitiveStore(XsrStateStore store, byte[]? bytes = null)
    {
        store.Publish(store.Resolve(XsrSemanticId.Parse("initial.count")), 42);
        store.Publish(store.Resolve(XsrSemanticId.Parse("initial.bytes")), bytes ?? [1, 2, 3]);
    }

    private static void CoreInitialSnapshotPreservesStoreAndOwnsCandidateValues()
    {
        XsrStateStore original = InitialPrimitiveStore();
        XsrStateStore retained = original;
        XsrStateStore candidate = InitialPrimitiveStore();
        byte[] sourceBytes = [1, 2, 3];
        FillInitialPrimitiveStore(candidate, sourceBytes);
        XsrStateSnapshot prior = original.CaptureSnapshot();
        int notifications = 0;
        List<XsrStateSnapshot> observedSnapshots = [];
        original.Changed += change =>
        {
            _ = change;
            notifications++;
            observedSnapshots.Add(retained.CaptureSnapshot());
        };
        original.CommitInitialSnapshot(candidate);
        AssertTrue(ReferenceEquals(retained, original));
        AssertEqual(2, notifications);
        foreach (XsrStateSnapshot complete in observedSnapshots)
        {
            AssertTrue(complete.Entries.All(entry => entry.Availability == XsrStateAvailability.Available));
            AssertTrue(complete.Entries.All(entry => entry.Revision == 1));
            AssertEqual(42, (int)complete.Entries.Single(static entry => entry.SemanticId.Value == "initial.count").Value!);
            AssertTrue(new byte[] { 1, 2, 3 }.AsSpan().SequenceEqual(
                (byte[])complete.Entries.Single(static entry => entry.SemanticId.Value == "initial.bytes").Value!));
        }
        AssertTrue(prior.Entries.All(entry => entry.Revision == 0 && entry.Availability == XsrStateAvailability.Unavailable));
        XsrStateId count = original.Resolve(XsrSemanticId.Parse("initial.count"));
        XsrStateId bytes = original.Resolve(XsrSemanticId.Parse("initial.bytes"));
        sourceBytes[0] = 99;
        candidate.Read<byte[]>(bytes).Value[1] = 88;
        candidate.Publish(count, 77);
        candidate.MarkAvailability(bytes, XsrStateAvailability.Unavailable);
        AssertEqual(42, original.Read<int>(count).Value);
        AssertTrue(new byte[] { 1, 2, 3 }.AsSpan().SequenceEqual(original.Read<byte[]>(bytes).Value));
        AssertEqual(1L, original.Read<byte[]>(bytes).Revision);
        AssertEqual(2, notifications);
        original.Publish(count, 84);
        AssertEqual(84, retained.Read<int>(count).Value);
        AssertEqual(3, notifications);
        AssertEqual(84, (int)observedSnapshots[^1].Entries.Single(static entry => entry.SemanticId.Value == "initial.count").Value!);
        AssertThrows<InvalidOperationException>(() => original.CommitInitialSnapshot(InitialPrimitiveStore()));
    }

    private static void CoreInitialSnapshotRejectsInvalidCandidatesWithoutPublication()
    {
        List<XsrStateStore> candidates =
        [
            InitialPrimitiveStore(), InitialPrimitiveStore(owner: "other"),
            InitialPrimitiveStore(firstSemantic: "initial.other"), InitialPrimitiveStore(firstAsLong: true),
            InitialPrimitiveStore(firstAsObject: true),
        ];
        XsrStateStoreBuilder fewerBuilder = new();
        fewerBuilder.Cell<int>(XsrSemanticId.Parse("initial.count"), "initial");
        candidates.Add(fewerBuilder.Build());
        XsrStateStore partial = InitialPrimitiveStore();
        partial.Publish(partial.Resolve(XsrSemanticId.Parse("initial.count")), 42);
        candidates.Add(partial);
        XsrStateStore repeated = InitialPrimitiveStore();
        FillInitialPrimitiveStore(repeated);
        repeated.Publish(repeated.Resolve(XsrSemanticId.Parse("initial.count")), 99);
        candidates.Add(repeated);
        XsrStateStore pending = InitialPrimitiveStore();
        FillInitialPrimitiveStore(pending);
        pending.PublishCoalesced(pending.Resolve(XsrSemanticId.Parse("initial.count")), 99);
        candidates.Add(pending);
        XsrStateStore stale = InitialPrimitiveStore();
        FillInitialPrimitiveStore(stale);
        stale.MarkAvailability(stale.Resolve(XsrSemanticId.Parse("initial.bytes")), XsrStateAvailability.Stale);
        candidates.Add(stale);
        XsrStateStore oversized = InitialPrimitiveStore();
        FillInitialPrimitiveStore(oversized, new byte[32 * 1024 * 1024 + 1]);
        candidates.Add(oversized);
        XsrStateStore nullBytes = InitialPrimitiveStore();
        nullBytes.Publish(nullBytes.Resolve(XsrSemanticId.Parse("initial.count")), 42);
        nullBytes.Publish<byte[]>(nullBytes.Resolve(XsrSemanticId.Parse("initial.bytes")), null!);
        candidates.Add(nullBytes);
        foreach (XsrStateStore candidate in candidates)
        {
            XsrStateStore original = InitialPrimitiveStore();
            int notifications = 0;
            original.Changed += _ => notifications++;
            AssertThrows<InvalidOperationException>(() => original.CommitInitialSnapshot(candidate));
            AssertEqual(0, notifications);
            AssertTrue(original.CaptureSnapshot().Entries.All(entry => entry.Revision == 0 && entry.Value is null));
            XsrStateStore valid = InitialPrimitiveStore();
            FillInitialPrimitiveStore(valid);
            original.CommitInitialSnapshot(valid);
            AssertEqual(2, notifications);
        }

        XsrStateStore same = InitialPrimitiveStore();
        AssertThrows<InvalidOperationException>(() => same.CommitInitialSnapshot(same));
        XsrStateStore written = InitialPrimitiveStore();
        written.Publish(written.Resolve(XsrSemanticId.Parse("initial.count")), 9);
        XsrStateStore validCandidate = InitialPrimitiveStore();
        FillInitialPrimitiveStore(validCandidate);
        AssertThrows<InvalidOperationException>(() => written.CommitInitialSnapshot(validCandidate));
        AssertEqual(9, written.Read<int>(written.Resolve(XsrSemanticId.Parse("initial.count"))).Value);
        XsrStateStore deferred = InitialPrimitiveStore();
        deferred.PublishCoalesced(deferred.Resolve(XsrSemanticId.Parse("initial.count")), 9);
        AssertThrows<InvalidOperationException>(() => deferred.CommitInitialSnapshot(validCandidate));
        AssertEqual(9, deferred.Read<int>(deferred.Resolve(XsrSemanticId.Parse("initial.count"))).Value);

        XsrStateStore unsupported = InitialPrimitiveStore(firstAsObject: true);
        XsrStateStore unsupportedCandidate = InitialPrimitiveStore(firstAsObject: true);
        unsupportedCandidate.Publish<object>(unsupportedCandidate.Resolve(XsrSemanticId.Parse("initial.count")), new List<int> { 1 });
        unsupportedCandidate.Publish(unsupportedCandidate.Resolve(XsrSemanticId.Parse("initial.bytes")), new byte[] { 1 });
        AssertThrows<InvalidOperationException>(() => unsupported.CommitInitialSnapshot(unsupportedCandidate));
        AssertTrue(unsupported.CaptureSnapshot().Entries.All(entry => entry.Revision == 0));
        foreach (bool collection in new[] { false, true })
        {
            XsrStateStoreBuilder builder = new();
            XsrSemanticId first = XsrSemanticId.Parse("initial.count");
            XsrSemanticId second = XsrSemanticId.Parse("initial.bytes");
            if (collection) builder.Collection<int, int>(first, "initial", static value => value);
            else builder.Derived<int>(first, "initial", [second], static (_, _) => 0);
            builder.Cell<byte[]>(second, "initial");
            AssertThrows<InvalidOperationException>(() => builder.Build().CommitInitialSnapshot(validCandidate));
        }
        XsrStateStoreBuilder oversizedOriginalBuilder = new();
        XsrStateStoreBuilder oversizedCandidateBuilder = new();
        for (int index = 0; index < 4097; index++)
        {
            XsrSemanticId semantic = XsrSemanticId.Parse("initial.limit." + index);
            oversizedOriginalBuilder.Cell<int>(semantic, "initial");
            oversizedCandidateBuilder.Cell<int>(semantic, "initial");
        }
        XsrStateStore oversizedOriginal = oversizedOriginalBuilder.Build();
        XsrStateStore oversizedCandidate = oversizedCandidateBuilder.Build();
        AssertThrows<InvalidOperationException>(() => oversizedOriginal.CommitInitialSnapshot(oversizedCandidate));
    }

    private static async ValueTask CoreInitialSnapshotRacesWritersAndReadersWithoutLosingWrites()
    {
        // Each outcome has a linear boundary: an admitted ordinary writer prevents initial
        // commit, or commits after the complete initialized table. Its value is never lost.
        for (int attempt = 0; attempt < 64; attempt++)
        {
            XsrStateStore original = InitialPrimitiveStore();
            XsrStateStore candidate = InitialPrimitiveStore();
            FillInitialPrimitiveStore(candidate);
            XsrStateId count = original.Resolve(XsrSemanticId.Parse("initial.count"));
            using ManualResetEventSlim start = new();
            Task commit = Task.Run(() =>
            {
                start.Wait();
                try { original.CommitInitialSnapshot(candidate); }
                catch (InvalidOperationException) { }
            });
            Task writer = Task.Run(() => { start.Wait(); original.Publish(count, 99); });
            start.Set();
            await Task.WhenAll(commit, writer).WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(99, original.Read<int>(count).Value);
        }

        for (int attempt = 0; attempt < 16; attempt++)
        {
            XsrStateStore original = InitialPrimitiveStore();
            XsrStateStore candidate = InitialPrimitiveStore();
            FillInitialPrimitiveStore(candidate);
            using ManualResetEventSlim start = new();
            Task reader = Task.Run(() =>
            {
                start.Wait();
                for (int index = 0; index < 256; index++)
                {
                    XsrStateSnapshot snapshot = original.CaptureSnapshot();
                    bool unavailable = snapshot.Entries.All(entry => entry.Revision == 0
                        && entry.Availability == XsrStateAvailability.Unavailable);
                    bool available = snapshot.Entries.All(entry => entry.Revision == 1
                        && entry.Availability == XsrStateAvailability.Available);
                    AssertTrue(unavailable || available);
                }
            });
            Task commit = Task.Run(() => { start.Wait(); original.CommitInitialSnapshot(candidate); });
            start.Set();
            await Task.WhenAll(commit, reader).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
