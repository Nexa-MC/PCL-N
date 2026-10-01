using Nexa.Services.Minecraft.Process;
namespace Nexa.Services.Resources;

public static class ResourceDependencyVerifier
{
    public static void Verify(LaunchModInventory added, LaunchModInventory existing)
    {
        if (!added.Complete || added.UnknownFiles > 0 || added.Mods.Any(m => !m.DependenciesComplete)) throw new InvalidDataException("下载文件的实际依赖表无法完整读取，未安装任何模组。");
        var existingIds = existing.Mods.Where(mod => mod.Enabled && !mod.NestedCandidate).Select(mod => mod.Id).ToHashSet(StringComparer.Ordinal);
        if (added.Mods.Any(mod => !mod.NestedCandidate && existingIds.Contains(mod.Id))
            || added.Mods.Where(mod => !mod.NestedCandidate).GroupBy(mod => mod.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("已存在相同模组标识的文件，请先处理现有版本，未安装任何模组。");
        var enabled = existing.Mods.Concat(added.Mods).Where(m => m.Enabled)
            .SelectMany(mod => mod.ProvidedIds.Prepend(KeyValuePair.Create(mod.Id, mod.Version)))
            .GroupBy(identity => identity.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(identity => identity.Value).ToArray(), StringComparer.Ordinal);
        List<string> missing = [];
        foreach (var mod in added.Mods)
            foreach (var dependency in mod.Dependencies)
            {
                // Runtime/loader requirements are checked by the existing launch preflight.
                if (dependency.Key is "minecraft" or "java" or "fabricloader" or "quilt_loader" or "forge" or "neoforge" or "cleanroom") continue;
                if (!enabled.TryGetValue(dependency.Key, out var candidates) || !candidates.Any(version => Satisfies(version, dependency.Value)))
                    missing.Add($"{mod.Id} 需要 {dependency.Key} {dependency.Value}");
            }
        if (missing.Count > 0) throw new InvalidDataException("实际依赖核验未通过，未安装任何模组：\n" + string.Join('\n', missing.Distinct().Take(20)));
    }
    public static bool Satisfies(string actual, string range)
    {
        if (range is "*" or "") return true;
        if (actual == "unknown") return false;
        if (range.Contains("||", StringComparison.Ordinal)) return range.Split("||", StringSplitOptions.TrimEntries).Any(r => Satisfies(actual, r));
        if (range.StartsWith('[') || range.StartsWith('('))
        {
            int close = range.IndexOfAny([']', ')']);
            if (close < 0) return false;
            if (close < range.Length - 1) return Satisfies(actual, range[..(close + 1)]) || Satisfies(actual, range[(close + 1)..].TrimStart(','));
            var limits = range[1..^1].Split(',');
            if (limits.Length == 1) return Equal(actual, limits[0]);
            if (limits.Length != 2) return false;
            int? low = limits[0].Length == 0 ? null : Compare(actual, limits[0]);
            int? high = limits[1].Length == 0 ? null : Compare(actual, limits[1]);
            return (limits[0].Length == 0 || low is not null && (range[0] == '[' ? low >= 0 : low > 0))
                && (limits[1].Length == 0 || high is not null && (range[^1] == ']' ? high <= 0 : high < 0));
        }
        var terms = range.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 1) return terms.All(t => Satisfies(actual, t));
        string op = range.StartsWith(">=", StringComparison.Ordinal) || range.StartsWith("<=", StringComparison.Ordinal) ? range[..2]
            : range[0] is '>' or '<' or '=' or '^' or '~' ? range[..1] : "";
        string expected = range[op.Length..];
        if (expected.Contains('*') || expected.Contains('x'))
        {
            var pattern = expected.Split('.'); var parts = actual.Split('.');
            return pattern.Select((part, i) => part is "*" or "x" || i < parts.Length && parts[i] == part).All(v => v);
        }
        if (op.Length == 0 || op == "=") return Equal(actual, expected);
        int? comparison = Compare(actual, expected); if (comparison is null) return false;
        if (op is "^" or "~")
        {
            var parts = expected.Split('.').Select(p => int.TryParse(p, out int n) ? n : -1).ToArray();
            if (parts.Any(n => n < 0) || parts.Length > 4) return false;
            int bump = op == "~" ? Math.Min(1, parts.Length - 1) : Array.FindIndex(parts, p => p != 0);
            if (bump < 0) bump = parts.Length - 1;
            parts[bump]++; for (int i = bump + 1; i < parts.Length; i++) parts[i] = 0;
            return comparison >= 0 && Compare(actual, string.Join('.', parts)) < 0;
        }
        return op switch { ">=" => comparison >= 0, "<=" => comparison <= 0, ">" => comparison > 0, "<" => comparison < 0, _ => false };
    }
    private static bool Equal(string actual, string expected) => actual == expected || Compare(actual, expected) == 0;
    private static int? Compare(string actual, string expected)
    {
        var a = actual.Split('+')[0].Split('-', 2); var b = expected.Split('+')[0].Split('-', 2);
        var av = a[0].Split('.'); var bv = b[0].Split('.');
        for (int i = 0; i < Math.Max(av.Length, bv.Length); i++)
        {
            int x = 0, y = 0;
            if (i < av.Length && !int.TryParse(av[i], out x) || i < bv.Length && !int.TryParse(bv[i], out y)) return null;
            if (x != y) return x.CompareTo(y);
        }
        if (a.Length == 1 || b.Length == 1) return a.Length == b.Length ? 0 : a.Length == 1 ? 1 : -1;
        var ap = a[1].Split('.'); var bp = b[1].Split('.');
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            bool an = long.TryParse(ap[i], out long x), bn = long.TryParse(bp[i], out long y);
            int comparison = an && bn ? x.CompareTo(y) : an != bn ? an ? -1 : 1 : string.Compare(ap[i], bp[i], StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }
        return ap.Length.CompareTo(bp.Length);
    }
}
