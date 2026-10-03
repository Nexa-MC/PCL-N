using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static ValueTask InstancePathProbesPreserveExistingFilesAndIsolateConcurrentCalls()
    {
        string directory = CreateTempDirectory();
        try
        {
            string existing = Path.Combine(directory, ".nexa-write-probe");
            File.WriteAllText(existing, "retained");
            Parallel.For(0, 32, _ =>
            {
                var facts = MachineInstanceCatalog.CollectPathScope(directory, DateTimeOffset.UtcNow);
                var writable = (Capability<bool>)facts.Single(f => f.Id == MachineInstanceCatalog.InstancePathWritable.Id);
                AssertTrue(writable.Value);
            });
            AssertEqual("retained", File.ReadAllText(existing));
            AssertEqual(1, Directory.EnumerateFiles(directory).Count());
        }
        finally { Directory.Delete(directory, true); }
        return ValueTask.CompletedTask;
    }
}
