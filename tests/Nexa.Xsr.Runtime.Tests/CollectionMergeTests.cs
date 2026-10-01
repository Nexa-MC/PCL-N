using Nexa.Xsr.State;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static NamedItem[] PriorCollectionMerge(IReadOnlyList<NamedItem> basis, IReadOnlyList<NamedItem> upserts,
        IReadOnlyList<string> removals, IComparer<string> comparer)
    {
        Dictionary<string, NamedItem> merged = [];
        foreach (var item in basis) merged[item.Name] = item;
        foreach (var item in upserts) merged[item.Name] = item;
        foreach (string key in removals) merged.Remove(key);
        return [.. merged.Values.OrderBy(static item => item.Name, comparer)];
    }

    private static void CollectionSparseMergeMatchesStableNormalization()
    {
        IComparer<string>[] comparers = [StringComparer.Ordinal, StringComparer.OrdinalIgnoreCase,
            Comparer<string>.Create(static (_, _) => 0), Comparer<string>.Create(static (a, b) => b.Length.CompareTo(a.Length))];
        string[] names = ["a", "A", "b", "B", "c", "C", "aa", "AA", "ab", "AB", "tail"];
        var random = new Random(732);
        foreach (var comparer in comparers)
        {
            for (int sample = 0; sample < 200; sample++)
            {
                NamedItem[] basis = Enumerable.Range(0, random.Next(40))
                    .Select(i => new NamedItem(names[random.Next(names.Length)], i)).ToArray();
                if (sample % 3 == 1) basis = basis.DistinctBy(static item => item.Name).OrderBy(static item => item.Name, comparer).ToArray();
                else if (sample % 3 == 2) basis = basis.OrderBy(static item => item.Name, comparer).ToArray();
                NamedItem[] before = [.. basis];
                NamedItem[] changes = Enumerable.Range(0, random.Next(20))
                    .Select(i => new NamedItem(names[random.Next(names.Length)], 100 + i)).ToArray();
                string[] removals = Enumerable.Range(0, random.Next(10)).Select(_ => names[random.Next(names.Length)]).ToArray();
                var delta = new XsrCollectionDelta<NamedItem, string>(7, changes, removals);
                AssertTrue(delta.TryApplyTo(basis, 7, static item => item.Name, comparer, out var result).IsApplied);
                AssertTrue(PriorCollectionMerge(basis, changes, removals, comparer).SequenceEqual(result));
                AssertTrue(before.SequenceEqual(basis));
                AssertFalse(delta.TryApplyTo(basis, 8, static _ => throw new InvalidOperationException(), comparer, out var rejected).IsApplied);
                AssertTrue(ReferenceEquals(basis, rejected));

                var builder = new XsrStateStoreBuilder();
                builder.Collection<NamedItem, string>(XsrSemanticId.Parse("merge.parity"), "Test", static item => item.Name, comparer);
                var store = builder.Build(); var id = store.Resolve(XsrSemanticId.Parse("merge.parity"));
                store.PublishDelta(id, new XsrCollectionDelta<NamedItem, string>(0, basis, []));
                var old = store.ReadCollection<NamedItem>(id);
                NamedItem[] oldCopy = [.. old.Items];
                store.PublishDelta(id, new XsrCollectionDelta<NamedItem, string>(1, changes, removals));
                AssertTrue(PriorCollectionMerge(oldCopy, changes, removals, comparer).SequenceEqual(store.ReadCollection<NamedItem>(id).Items));
                AssertTrue(oldCopy.SequenceEqual(old.Items));
            }
        }
    }

    private static void CollectionSparseMergeBoundsComparisonsAndPreservesSnapshots()
    {
        var basis = Enumerable.Range(0, 10_000).Select(i => new NamedItem(i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), i)).ToArray();
        int comparisons = 0;
        var comparer = Comparer<string>.Create((a, b) => { comparisons++; return StringComparer.Ordinal.Compare(a, b); });
        var delta = new XsrCollectionDelta<NamedItem, string>(1, [new("05000", -1), new("10000", 10_000)], ["00000"]);
        delta.TryApplyTo(basis, 1, static item => item.Name, comparer, out var result);
        AssertTrue(comparisons < 3 * basis.Length);
        int sparseComparisons = comparisons;
        comparisons = 0;
        var expected = PriorCollectionMerge(basis, delta.Upserts, delta.Removals, comparer);
        AssertTrue(comparisons > sparseComparisons * 2);
        AssertTrue(expected.SequenceEqual(result));
        AssertEqual(5000, basis[5000].Value);
        AssertTrue(!ReferenceEquals(basis, result));
        var unchanged = new XsrCollectionDelta<NamedItem, string>(1, [], []);
        unchanged.TryApplyTo(basis, 1, static item => item.Name, comparer, out var copied);
        AssertTrue(basis.SequenceEqual(copied) && !ReferenceEquals(basis, copied));

        // A previously published mutable item can change key; do not reuse a cached key index.
        var builder = new XsrStateStoreBuilder();
        builder.Collection<MutableCollectionItem, string>(XsrSemanticId.Parse("merge.mutable"), "Test", static item => item.Name, StringComparer.Ordinal);
        var store = builder.Build(); var id = store.Resolve(XsrSemanticId.Parse("merge.mutable"));
        var first = new MutableCollectionItem("a"); var second = new MutableCollectionItem("b");
        store.PublishDelta(id, new XsrCollectionDelta<MutableCollectionItem, string>(0, [first, second], []));
        var original = store.ReadCollection<MutableCollectionItem>(id);
        AssertTrue(ReferenceEquals(original, store.ReadCollection<MutableCollectionItem>(id)));
        long beforeReads = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) _ = store.ReadCollection<MutableCollectionItem>(id);
        AssertEqual(0L, GC.GetAllocatedBytesForCurrentThread() - beforeReads);
        first.Name = "z";
        store.PublishDelta(id, new XsrCollectionDelta<MutableCollectionItem, string>(1, [], []));
        var updated = store.ReadCollection<MutableCollectionItem>(id);
        AssertTrue(ReferenceEquals(second, updated.Items[0]));
        AssertTrue(!ReferenceEquals(original, updated));
        AssertTrue(ReferenceEquals(first, original.Items[0]));
        store.MarkAvailability(id, XsrStateAvailability.Stale);
        var stale = store.ReadCollection<MutableCollectionItem>(id);
        AssertEqual(XsrStateAvailability.Stale, stale.Availability);
        AssertEqual(XsrStateAvailability.Available, updated.Availability);
        AssertTrue(!ReferenceEquals(updated, stale) && ReferenceEquals(stale, store.ReadCollection<MutableCollectionItem>(id)));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        AssertThrows<OperationCanceledException>(() => store.ReadCollection<MutableCollectionItem>(id, stop.Token));
    }

    private static void CollectionFailedMergeLeavesRevisionAndNotificationsUnchanged()
    {
        bool failOrder = false;
        var observer = new RecordingStateObserver();
        var builder = new XsrStateStoreBuilder();
        builder.Collection<NamedItem, string>(XsrSemanticId.Parse("merge.atomic"), "Test",
            static item => item.Name == "explode" ? throw new InvalidOperationException("selector") : item.Name,
            Comparer<string>.Create((a, b) => failOrder ? throw new InvalidOperationException("order") : StringComparer.OrdinalIgnoreCase.Compare(a, b)));
        var store = builder.Build(observer); var id = store.Resolve(XsrSemanticId.Parse("merge.atomic"));
        store.PublishDelta(id, new XsrCollectionDelta<NamedItem, string>(0, [new("a", 1), new("A", 2), new("b", 3)], []));
        var snapshot = store.ReadCollection<NamedItem>(id);
        int notifications = observer.Changes.Length;
        AssertThrows<InvalidOperationException>(() => store.PublishDelta(id, new XsrCollectionDelta<NamedItem, string>(1, [new("explode", 9)], [])));
        failOrder = true;
        AssertThrows<InvalidOperationException>(() => store.PublishDelta(id, new XsrCollectionDelta<NamedItem, string>(1, [new("c", 9)], [])));
        failOrder = false;
        AssertEqual(1L, store.ReadCollection<NamedItem>(id).Revision);
        AssertEqual(notifications, observer.Changes.Length);
        AssertTrue(snapshot.Items.SequenceEqual(store.ReadCollection<NamedItem>(id).Items));
        store.PublishDelta(id, new XsrCollectionDelta<NamedItem, string>(1, [new("a", 4), new("a", 5), new("AA", 6)], ["b"]));
        AssertEqual(2L, store.ReadCollection<NamedItem>(id).Revision);
        AssertTrue(store.ReadCollection<NamedItem>(id).Items.SequenceEqual(new NamedItem[] { new("a", 5), new("A", 2), new("AA", 6) }));
        AssertEqual(1, snapshot.Items[0].Value);
    }

    private sealed class MutableCollectionItem(string name)
    {
        public string Name { get; set; } = name;
    }
}
