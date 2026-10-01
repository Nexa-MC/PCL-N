namespace Nexa.Xsr.State;

/// <summary>Stable sparse-change merge; identity equality is independent of key ordering.</summary>
internal static class XsrCollectionMerger<TItem, TKey> where TKey : notnull
{
    private readonly record struct Change(TItem Item, TKey Key, long Position);
    private readonly struct ChangeComparer(IComparer<TKey> comparer) : IComparer<Change>
    {
        public int Compare(Change a, Change b)
        {
            int order = comparer.Compare(a.Key, b.Key);
            return order != 0 ? order : a.Position.CompareTo(b.Position);
        }
    }

    internal static TItem[] Apply(IReadOnlyList<TItem> basis, IReadOnlyList<TItem> upserts,
        IReadOnlyList<TKey> removals, Func<TItem, TKey> keySelector, IComparer<TKey> comparer)
    {
        var positions = new Dictionary<TKey, int>(basis.Count);
        TKey[] keys = new TKey[basis.Count];
        bool orderedUnique = true;
        for (int i = 0; i < basis.Count; i++)
        {
            TKey key = keySelector(basis[i]); keys[i] = key;
            if (!positions.TryAdd(key, i) || (i > 0 && comparer.Compare(keys[i - 1], key) > 0))
                orderedUnique = false;
        }
        var changed = new Dictionary<TKey, Change>();
        for (int i = 0; i < upserts.Count; i++)
        {
            TItem item = upserts[i]; TKey key = keySelector(item);
            long position = changed.TryGetValue(key, out var prior) ? prior.Position
                : positions.TryGetValue(key, out int existing) ? existing : (long)basis.Count + i;
            changed[key] = new(item, key, position);
        }
        var removed = new HashSet<TKey>();
        foreach (TKey key in removals)
        {
            // Dictionary.Remove retains the previous null-key rejection contract.
            changed.Remove(key); removed.Add(key);
        }
        if (!orderedUnique)
        {
            Dictionary<TKey, TItem> normalized = [];
            for (int i = 0; i < basis.Count; i++) normalized[keys[i]] = basis[i];
            foreach (var entry in changed.Values) normalized[entry.Key] = entry.Item;
            foreach (TKey key in removed) normalized.Remove(key);
            return [.. normalized.Values.OrderBy(keySelector, comparer)];
        }

        Change[] additions = [.. changed.Values];
        if (additions.Length > 1) additions.AsSpan().Sort(new ChangeComparer(comparer));
        int retained = 0;
        foreach (TKey key in keys)
            if (!removed.Contains(key) && !changed.ContainsKey(key)) retained++;
        TItem[] result = new TItem[checked(retained + additions.Length)];
        int baseIndex = 0, changeIndex = 0, output = 0;
        while (output < result.Length)
        {
            while (baseIndex < keys.Length && (removed.Contains(keys[baseIndex]) || changed.ContainsKey(keys[baseIndex])))
                baseIndex++;
            if (changeIndex == additions.Length)
                result[output++] = basis[baseIndex++];
            else if (baseIndex == keys.Length)
                result[output++] = additions[changeIndex++].Item;
            else
            {
                var entry = additions[changeIndex];
                int order = comparer.Compare(entry.Key, keys[baseIndex]);
                result[output++] = order < 0 || (order == 0 && entry.Position < baseIndex)
                    ? additions[changeIndex++].Item : basis[baseIndex++];
            }
        }
        return result;
    }
}
