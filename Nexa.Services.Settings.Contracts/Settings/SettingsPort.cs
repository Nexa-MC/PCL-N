

namespace Nexa.Services.Settings;

/// <summary>
/// Persistence boundary of one settings family. The service owns when and what is saved; the
/// port only moves raw string entries. Failures surface as exceptions and become stable errors.
/// </summary>
public interface ISettingsPort
{
    /// <summary>
    /// Reads every persisted entry. A missing store means an empty view, not a failure.
    /// </summary>
    IReadOnlyDictionary<string, string> Load();

    /// <summary>
    /// Replaces the persisted store with exactly the given entries.
    /// </summary>
    void Save(IReadOnlyDictionary<string, string> values);
}
