using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nexa.Desktop.Ui;

/// <summary>Embedded presentation data. No service calls, reflection serialization or network.</summary>
internal sealed class UiLocalizationCatalog
{
    private sealed record Entry(string Source, string English, string Traditional, string[]? Segments, string? EnglishOne);
    private readonly FrozenDictionary<string, Entry> _entries;
    private readonly Entry[] _templates;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    internal string Language { get; private set; } = "zh-Hans";

    internal UiLocalizationCatalog()
    {
        using var stream = typeof(UiLocalizationCatalog).Assembly.GetManifestResourceStream("Nexa.Desktop.Ui.Localization.messages.json")
            ?? throw new InvalidOperationException("The interface language catalog is missing.");
        using var json = JsonDocument.Parse(stream);
        List<Entry> entries = [];
        foreach (var row in json.RootElement.EnumerateArray())
        {
            string source = row.GetProperty("source").GetString()!;
            string english = row.GetProperty("en").GetString()!;
            string traditional = row.GetProperty("zh-Hant").GetString()!;
            bool template = row.TryGetProperty("template", out var flag) && flag.GetBoolean();
            if (source.Length == 0 || english.Length == 0 || traditional.Length == 0) throw new InvalidDataException("Language entries must be complete.");
            string? englishOne = row.TryGetProperty("en-one", out var singular) ? singular.GetString() : null;
            string[]? segments = template ? SplitTemplate(source) : null;
            if (segments is not null)
            {
                ValidateArguments(english, segments.Length - 1);
                ValidateArguments(traditional, segments.Length - 1);
                if (englishOne is not null) ValidateArguments(englishOne, segments.Length - 1);
            }
            entries.Add(new(source, english, traditional, segments, englishOne));
        }
        _entries = entries.Where(entry => entry.Segments is null).ToFrozenDictionary(entry => entry.Source, StringComparer.Ordinal);
        _templates = entries.Where(entry => entry.Segments is not null).OrderByDescending(entry => entry.Segments!.Sum(segment => segment.Length)).ToArray();
    }

    internal static string ResolveLanguage(string requested, string systemLanguage)
    {
        string value = (requested == "auto" || string.IsNullOrWhiteSpace(requested) ? systemLanguage : requested).Replace('_', '-');
        if (value.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || value.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            || value.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) || value.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hans";
        return "en";
    }

    internal bool SetLanguage(string requested, string? systemLanguage = null)
    {
        string language = ResolveLanguage(requested, systemLanguage ?? CultureInfo.CurrentUICulture.Name);
        if (Language == language) return false;
        Language = language; _cache.Clear(); return true;
    }

    internal string Translate(string source)
    {
        if (Language == "zh-Hans" || source.Length == 0) return source;
        if (_cache.TryGetValue(source, out string? cached)) return cached;
        string result = source;
        if (_entries.TryGetValue(source, out var entry)) result = Language == "en" ? entry.English : entry.Traditional;
        else if (source.Length <= 8192)
        {
            foreach (var template in _templates)
            {
                if (!TryCapture(source, template.Segments!, out var arguments)) continue;
                string translation = Language == "en" ? arguments[0] == "1" ? template.EnglishOne ?? template.English : template.English : template.Traditional;
                result = ApplyTemplate(translation, arguments); break;
            }
        }
        if (source.Length <= 2048)
        {
            if (_cache.Count >= 2048) _cache.Clear();
            _cache[source] = result;
        }
        return result;
    }

    private static string[] SplitTemplate(string source)
    {
        List<string> segments = []; int offset = 0, index = 0;
        while (source.IndexOf("{" + index + "}", offset, StringComparison.Ordinal) is int start && start >= 0)
        { segments.Add(source[offset..start]); offset = start + index.ToString(CultureInfo.InvariantCulture).Length + 2; index++; }
        segments.Add(source[offset..]);
        if (segments.Count < 2 || segments.Count > 17 || segments.Skip(1).SkipLast(1).Any(segment => segment.Length == 0))
            throw new InvalidDataException("Language templates must have bounded, separated arguments.");
        return segments.ToArray();
    }

    private static void ValidateArguments(string template, int count)
    {
        HashSet<int> seen = [];
        for (int offset = 0; offset < template.Length; offset++)
        {
            if (template[offset] != '{') continue;
            int end = template.IndexOf('}', offset + 1);
            if (end <= offset || !int.TryParse(template.AsSpan(offset + 1, end - offset - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out int index) || index < 0 || index >= count)
                throw new InvalidDataException("A translation contains an invalid template argument.");
            seen.Add(index); offset = end;
        }
        if (seen.Count != count) throw new InvalidDataException("A translation must preserve every template argument.");
    }

    private static bool TryCapture(string source, string[] segments, out string[] arguments)
    {
        arguments = [];
        if (!source.StartsWith(segments[0], StringComparison.Ordinal) || !source.EndsWith(segments[^1], StringComparison.Ordinal)) return false;
        List<string> values = []; int offset = segments[0].Length;
        for (int i = 1; i < segments.Length; i++)
        {
            int end = i == segments.Length - 1 ? source.Length - segments[i].Length : source.IndexOf(segments[i], offset, StringComparison.Ordinal);
            if (end < offset) return false;
            values.Add(source[offset..end]); offset = end + segments[i].Length;
        }
        if (offset != source.Length) return false;
        arguments = values.ToArray(); return true;
    }

    private static string ApplyTemplate(string template, string[] arguments)
    {
        StringBuilder result = new();
        for (int offset = 0; offset < template.Length; offset++)
        {
            if (template[offset] == '{' && template.IndexOf('}', offset + 1) is int end && end > offset
                && int.TryParse(template.AsSpan(offset + 1, end - offset - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && index >= 0 && index < arguments.Length)
            { result.Append(arguments[index]); offset = end; }
            else result.Append(template[offset]);
        }
        return result.ToString();
    }
}
