using System.Text;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask FileWorkspacePreservesEncodingAndRequiresCurrentExplicitPreview()
    {
        foreach (var codec in new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
        {
            string root = CreateTempDirectory();
            try
            {
                string instance = ResourceInstanceFixture(root), directory = Path.Combine(instance, "config"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "example.properties"), original = "a=1\r\nb=\told\r\n", updated = "a=2\r\nb=\tnew\r\n";
                byte[] initial = codec.GetPreamble().Concat(codec.GetBytes(original)).ToArray(); await File.WriteAllBytesAsync(path, initial);
                var listing = await InstanceFileWorkspaceService.ListAsync(new(instance, "config")); AssertEqual(1, listing.Entries.Count); AssertTrue(listing.Complete);
                var entry = listing.Entries[0]; var query = new InstanceFileReadQuery(instance, "config", entry.Name, entry.Size!.Value, entry.ModifiedUtcTicks);
                var document = await InstanceFileWorkspaceService.ReadAsync(query); AssertEqual(original, document.Text); AssertTrue(document.Editable);
                var preview = await InstanceFileWorkspaceService.PreviewAsync(new(document, updated)); AssertEqual(initial.LongLength, preview.PreviousBytes);
                AssertTrue((await InstanceFileWorkspaceService.SaveAsync(new(preview), ContentCompletionStore())).IsSuccess);
                byte[] expected = codec.GetPreamble().Concat(codec.GetBytes(updated)).ToArray();
                AssertTrue((await File.ReadAllBytesAsync(path)).SequenceEqual(expected)); AssertTrue((await File.ReadAllBytesAsync(path + ".nexa-backup")).SequenceEqual(initial));
                AssertFalse(Directory.EnumerateFiles(directory, ".nexa-edit-*").Any());
                // The old preview cannot overwrite a subsequent save or an external edit.
                AssertFalse((await InstanceFileWorkspaceService.SaveAsync(new(preview), ContentCompletionStore())).IsSuccess);
                AssertTrue((await File.ReadAllBytesAsync(path)).SequenceEqual(expected));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static async ValueTask FileWorkspaceRejectsTraversalBinaryLogsAndRunningWrites()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root); Directory.CreateDirectory(Path.Combine(instance, "config")); Directory.CreateDirectory(Path.Combine(instance, "logs"));
            string path = Path.Combine(instance, "config", "test.json"); await File.WriteAllTextAsync(path, "{\"value\":1}"); var file = new FileInfo(path);
            var document = await InstanceFileWorkspaceService.ReadAsync(new(instance, "config", file.Name, file.Length, file.LastWriteTimeUtc.Ticks));
            bool invalidJson = false; try { await InstanceFileWorkspaceService.PreviewAsync(new(document, "{broken")); } catch (System.Text.Json.JsonException) { invalidJson = true; }
            AssertTrue(invalidJson);
            var preview = await InstanceFileWorkspaceService.PreviewAsync(new(document, "{\"value\":2}")); var store = ContentCompletionStore();
            var session = new MinecraftProcessSnapshot(Guid.NewGuid(), "fixture", 42, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null) { GameDirectory = instance };
            store.PublishDelta(store.Resolve(MinecraftProcessStateComposition.SessionsKey), new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [session], []));
            AssertFalse((await InstanceFileWorkspaceService.SaveAsync(new(preview), store)).IsSuccess); AssertEqual("{\"value\":1}", await File.ReadAllTextAsync(path));
            string log = Path.Combine(instance, "logs", "latest.log"); await File.WriteAllTextAsync(log, "fixture\n"); var info = new FileInfo(log);
            var logs = await InstanceFileWorkspaceService.ReadAsync(new(instance, "logs", info.Name, info.Length, info.LastWriteTimeUtc.Ticks)); AssertFalse(logs.Editable);
            bool readOnly = false; try { await InstanceFileWorkspaceService.PreviewAsync(new(logs with { Editable = true }, "changed")); } catch (IOException) { readOnly = true; }
            AssertTrue(readOnly);
            var otherListing = await InstanceFileWorkspaceService.ListAsync(new(instance, "other")); AssertTrue(otherListing.Entries.Any(entry => entry.Name == "config" && entry.IsDirectory));
            var other = await InstanceFileWorkspaceService.ReadAsync(new(instance, "other", "config/test.json", file.Length, file.LastWriteTimeUtc.Ticks)); AssertFalse(other.Editable);
            bool otherWrite = false; try { await InstanceFileWorkspaceService.PreviewAsync(new(other with { Editable = true }, "{\"value\":3}")); } catch (IOException) { otherWrite = true; }
            AssertTrue(otherWrite);
            foreach (var (area, relative) in new[] { ("config", "../escape"), ("config", "/root"), ("config", "nested\\escape"), ("other", "../escape"), ("mods", "example.jar") })
            {
                bool rejected = false; try { await InstanceFileWorkspaceService.ListAsync(new(instance, area, relative)); } catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected);
            }
            string binary = Path.Combine(instance, "config", "opaque.cfg"); await File.WriteAllBytesAsync(binary, new byte[] { 1, 0, 2 }); var opaque = new FileInfo(binary);
            bool refused = false; try { await InstanceFileWorkspaceService.ReadAsync(new(instance, "config", opaque.Name, opaque.Length, opaque.LastWriteTimeUtc.Ticks)); } catch (InvalidDataException) { refused = true; }
            AssertTrue(refused);
            if (!OperatingSystem.IsWindows())
            {
                string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
                Directory.CreateSymbolicLink(Path.Combine(instance, "config", "linked"), outside);
                var listing = await InstanceFileWorkspaceService.ListAsync(new(instance, "config")); AssertFalse(listing.Complete); AssertFalse(listing.Entries.Any(entry => entry.Name == "linked"));
                bool refusedLink = false; try { await InstanceFileWorkspaceService.ListAsync(new(instance, "config", "linked")); } catch (IOException) { refusedLink = true; }
                AssertTrue(refusedLink);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
