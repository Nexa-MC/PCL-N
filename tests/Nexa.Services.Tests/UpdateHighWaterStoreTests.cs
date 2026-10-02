using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async Task UpdateHighWaterStoreIsMonotonicAndDurable()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nexa-high-water-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new UpdateHighWaterStore(directory);
            AssertNull(store.Read());
            store.Advance("2.0.0.alpha.5");
            AssertEqual("2.0.0.alpha.5", new UpdateHighWaterStore(directory).Read());
            AssertThrows<InvalidOperationException>(() => store.Advance("2.0.0.alpha.5"));
            AssertThrows<InvalidOperationException>(() => store.Advance("1.4.14"));

            File.WriteAllText(Path.Combine(directory, "highest-accepted-version.tmp-orphan"), "9.0.0\n");
            AssertEqual("2.0.0.alpha.5", store.Read());

            string[] versions = Enumerable.Range(6, 16).Select(value => $"2.0.0.alpha.{value}").ToArray();
            await Task.WhenAll(versions.Select(version => Task.Run(() =>
            {
                try { new UpdateHighWaterStore(directory).Advance(version); }
                catch (InvalidOperationException) { }
            })));
            AssertEqual("2.0.0.alpha.21", store.Read());

            File.WriteAllText(Path.Combine(directory, "highest-accepted-version"), "invalid\n");
            AssertThrows<InvalidDataException>(() => store.Read());
            AssertThrows<InvalidDataException>(() => store.Advance("2.0.0.beta.1"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
