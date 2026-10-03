using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask AutomaticUpdateRecoversEveryCommitBoundary()
    {
        foreach (string boundary in new[] { "received", "prepared", "high-water", "accepted", "activated", "cancelled" })
        {
            string path = CreateTempDirectory();
            try
            {
                using var directory = new UpdateFixtureDirectory(path);
                using var stop = new CancellationTokenSource();
                var fixture = CreateAutomaticUpdateFixture();
                var transaction = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source)
                { FaultBoundary = phase => { if (boundary == "cancelled" && phase == "received") stop.Cancel(); else if (phase == boundary) throw new IOException("Simulated crash"); } };
                try { await transaction.InstallAsync("2.0.0.alpha.6", "alpha", stop.Token); throw new InvalidOperationException("Fault did not execute"); }
                catch (IOException) { }
                catch (OperationCanceledException) when (boundary == "cancelled")
                {
                    using FileStream status = directory.OpenRead(AutomaticUpdateTransaction.StatusName);
                    AssertEqual("paused", UpdateTransactionJournal.Read(status)![2]);
                }
                using (FileStream state = directory.OpenRead(AutomaticUpdateTransaction.ActivationName))
                    AssertEqual(boundary == "activated", UpdateTransactionJournal.Read(state) is not null);
                fixture.Source.Offline = true;
                var recovered = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source);
                string slot = await recovered.InstallAsync("2.0.0.alpha.6", "alpha");
                using (IUpdateDirectory payload = directory.OpenDirectory(slot))
                using (FileStream image = payload.OpenRead("Nexa.Desktop.exe")) AssertEqual(3L, image.Length);
                recovered.Rollback();
                using (FileStream state = directory.OpenRead(AutomaticUpdateTransaction.ActivationName)) AssertEqual("", UpdateTransactionJournal.Read(state)![1]);
                using (FileStream state = directory.OpenRead(AutomaticUpdateTransaction.StatusName))
                {
                    string[] selected = UpdateTransactionJournal.Read(state)!;
                    AssertEqual("2.0.0.alpha.5", selected[0]);
                    AssertEqual("rolledback", selected[2]);
                }
                using (FileStream highWater = directory.OpenRead("highest-accepted-version.journal")) AssertEqual("2.0.0.alpha.6", UpdateHighWaterJournal.Read(highWater).Version);
                await ExpectReleaseRejection(() => recovered.InstallAsync("2.0.0.alpha.6", "alpha"));
            }
            finally { Directory.Delete(path, recursive: true); }
        }
    }

    private static async ValueTask AutomaticUpdateRejectsPackageBeforeActivation()
    {
        string path = CreateTempDirectory();
        try
        {
            using var directory = new UpdateFixtureDirectory(path);
            var fixture = CreateAutomaticUpdateFixture();
            fixture.Source.Package[^1] ^= 1;
            await ExpectReleaseRejection(() => new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source).InstallAsync("2.0.0.alpha.6", "alpha"));
            using FileStream state = directory.OpenRead(AutomaticUpdateTransaction.ActivationName);
            AssertNull(UpdateTransactionJournal.Read(state));
            using FileStream highWater = directory.OpenRead("highest-accepted-version.journal");
            AssertNull(UpdateHighWaterJournal.Read(highWater).Version);
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private static void AutomaticUpdateJournalPreservesEveryInterruptedFrame()
    {
        byte[] first = UpdateTransactionJournal.Encode(["2.0.0.alpha.5", "slot-a"]);
        byte[] next = UpdateTransactionJournal.Encode(["2.0.0.alpha.6", "slot-b"]);
        for (int i = 0; i < next.Length; i++)
        {
            using var stream = new MemoryStream(first.Concat(next.Take(i)).ToArray());
            AssertEqual("slot-a", UpdateTransactionJournal.Read(stream)![1]);
        }
        next[^8] ^= 1;
        using var corrupted = new MemoryStream(first.Concat(next).ToArray());
        AssertThrows<InvalidDataException>(() => UpdateTransactionJournal.Read(corrupted));
    }

    private static async ValueTask AutomaticUpdateNativeProtectedTransaction()
    {
        if (Environment.GetEnvironmentVariable("NEXA_TEST_PROTECTED_TRANSACTION") != "1")
        { Console.WriteLine("SKIP: dedicated privileged native transaction fixture."); return; }
        string root = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : OperatingSystem.IsMacOS() ? "/Library" : "/usr/lib";
        using IUpdateDirectory parent = ProtectedUpdateDirectory.Open(root);
        using IUpdateDirectory directory = parent.CreateDirectory(".nexa-ci-" + Guid.NewGuid().ToString("N"), publicRead: true);
        // Projection reopens this same public namespace while the creator lease is held.
        using (IUpdateDirectory observer = ProtectedUpdateDirectory.Open(directory.Path))
            AssertEqual(directory.Path, observer.Path);
        using (FileStream writer = directory.OpenState("public-progress", publicRead: true, exclusive: false))
        {
            UpdateTransactionJournal.Append(writer, "progress");
            using FileStream reader = directory.OpenRead("public-progress");
            AssertEqual("progress", UpdateTransactionJournal.Read(reader)![0]);
            UpdateTransactionJournal.Append(writer, "complete");
            AssertEqual("complete", UpdateTransactionJournal.Read(reader)![0]);
        }
        var fixture = CreateAutomaticUpdateFixture(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        var transaction = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source)
        { FaultBoundary = phase => { if (phase == "high-water") throw new IOException("Simulated exit"); } };
        try { await transaction.InstallAsync("2.0.0.alpha.6", "alpha"); throw new InvalidOperationException("Missing fault"); }
        catch (IOException) { }
        fixture.Source.Offline = true;
        var recovered = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source);
        string slotName = await recovered.InstallAsync("2.0.0.alpha.6", "alpha");
        using (IUpdateDirectory slot = directory.OpenDirectory(slotName)) AssertTrue(slot.Path.StartsWith(directory.Path, StringComparison.Ordinal));
        recovered.Rollback();
        using FileStream active = directory.OpenRead(AutomaticUpdateTransaction.ActivationName);
        AssertEqual("", UpdateTransactionJournal.Read(active)![1]);
        using (FileStream helper = directory.CreateFile("Nexa.Update.Helper" + (OperatingSystem.IsWindows() ? ".exe" : ""), publicRead: true))
        { helper.Write([1, 2, 3]); helper.Flush(true); }
        using (FileStream status = directory.OpenState(AutomaticUpdateTransaction.StatusName, publicRead: true, exclusive: false))
            UpdateTransactionJournal.Append(status, "2.0.0.alpha.6", "alpha", "complete"); // Exit before rollback status flush.
        var projected = await new AutomaticUpdateControl(new AutomaticUpdateProjectionHost(directory.Path)).ReadAsync(default);
        AssertEqual("2.0.0.alpha.5", projected.Version);
        AssertEqual("rolledback", projected.Phase);
        // Fixture remains in a disposable hosted runner; no path-recursive privileged cleanup.
    }

    private sealed class AutomaticUpdateProjectionHost(string path) : IUpdateHost
    {
        public string InstallationPath => path;
        public bool IsSystemInstallation => true;
        public Task<int> RunHelperAsync(string version, string channel, CancellationToken token) => throw new InvalidOperationException();
        public void RestartLauncher() => throw new InvalidOperationException();
    }

    private static (UpdateBuildIdentity Identity, UpdateGpgVerifier Verifier, AutomaticUpdateFixtureSource Source) CreateAutomaticUpdateFixture(string rid = "win-x64")
    {
        using var buffer = new MemoryStream();
        bool windows = rid.StartsWith("win-", StringComparison.Ordinal);
        string[] names = windows ? ["Nexa.Desktop.exe", "Nexa.Jvm.Host.exe", "assets/icon.png"] : ["Nexa.Desktop", "Nexa.Jvm.Host", "assets/icon.png"];
        if (windows)
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true);
            foreach (string name in names) { using Stream entry = zip.CreateEntry(name).Open(); entry.Write([1, 2, 3]); }
        }
        else
        {
            using var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true);
            using var tar = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true);
            foreach (string name in names)
            {
                using var data = new MemoryStream([1, 2, 3]);
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile,
                    (rid.StartsWith("osx-", StringComparison.Ordinal) ? "Nexa.app/Contents/MacOS/" : "Nexa/") + name)
                { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute };
                tar.WriteEntry(entry);
            }
        }
        byte[] package = buffer.ToArray();
        JsonObject manifest = JsonNode.Parse(ReleaseManifestFixture())!.AsObject();
        foreach (var item in manifest["assets"]!.AsArray())
        {
            var asset = item!.AsObject();
            if (asset["rid"]!.GetValue<string>() == rid && asset["format"]!.GetValue<string>() == (windows ? "portable.zip" : "portable.tar.gz"))
            { asset["size"] = package.Length; asset["sha256"] = Convert.ToHexStringLower(SHA256.HashData(package)); }
        }
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var key = GenerateSigningKey();
        return (new("2.0.0.alpha.5", rid, "nativeaot-self-contained", "Release"),
            new(key.ArmoredKey, key.Fingerprint), new(bytes, Encoding.ASCII.GetBytes(key.Sign(bytes)), package) { PublicKey = key.ArmoredKey });
    }

    private sealed class AutomaticUpdateFixtureSource(byte[] manifest, byte[] signature, byte[] package) : IUpdateReleaseSource
    {
        internal bool Offline { get; set; }
        internal string PublicKey { get; init; } = "";
        internal byte[] Manifest => manifest;
        internal byte[] Signature => signature;
        internal byte[] Package => package;
        public Task<(byte[] Manifest, byte[] Signature)> ReadReleaseAsync(string version, CancellationToken token)
            => Offline ? throw new IOException("Offline") : Task.FromResult((manifest, signature));
        public Task<Stream> OpenPackageAsync(string version, string name, CancellationToken token)
            => Offline ? throw new IOException("Offline") : Task.FromResult<Stream>(new MemoryStream(package, writable: false));
    }

    // Test-only port; the real helper exclusively constructs ProtectedUpdateDirectory.
    private sealed class UpdateFixtureDirectory(string path) : IUpdateDirectory
    {
        public string Path => path;
        public IUpdateDirectory OpenDirectory(string name) => new UpdateFixtureDirectory(System.IO.Path.Combine(path, name));
        public IUpdateDirectory CreateDirectory(string name, bool publicRead)
        { string child = System.IO.Path.Combine(path, name); Directory.CreateDirectory(child); return new UpdateFixtureDirectory(child); }
        public FileStream OpenRead(string name) => new(System.IO.Path.Combine(path, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        public FileStream OpenState(string name, bool publicRead = false, bool exclusive = true) => new(System.IO.Path.Combine(path, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.Read);
        public FileStream CreateFile(string name, bool publicRead = false, bool executable = false) => new(System.IO.Path.Combine(path, name), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        public void Flush() { }
        public void Dispose() { }
    }
}
