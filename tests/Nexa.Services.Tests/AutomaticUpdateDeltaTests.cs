using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask AutomaticUpdateDeltaReconstructsAllPlatforms()
    {
        foreach (string rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            string path = CreateTempDirectory();
            try
            {
                using var directory = new UpdateFixtureDirectory(path);
                var fixture = CreateDeltaFixture(rid);
                SeedDeltaSource(directory, fixture);
                var transaction = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source);
                string slot = await transaction.InstallAsync("2.0.0.alpha.6", "alpha");
                AssertDeltaOutput(directory, slot, fixture);
                AssertEqual(0, fixture.Source.FullRequests);
                AssertEqual(1, fixture.Source.DeltaRequests);
                AssertTrue(fixture.Source.Bundle.Length < fixture.Source.Full.Length / 10);
                AssertTrue(File.Exists(Path.Combine(path, "obsolete.txt")));
                AssertFalse(File.Exists(Path.Combine(path, slot, "obsolete.txt")));
                transaction.Rollback();
                await ExpectReleaseRejection(() => transaction.InstallAsync("2.0.0.alpha.6", "alpha"));
            }
            finally { Directory.Delete(path, recursive: true); }
        }
    }

    private static async ValueTask AutomaticUpdateDeltaFailureFallsBack()
    {
        foreach (string failure in new[] { "index-signature", "bundle-digest", "base-modified", "source-range", "traversal",
                     "missing-host", "stored-size", "duplicate", "target-digest", "unprofitable", "missing-index", "missing-bundle" })
        {
            string path = CreateTempDirectory();
            try
            {
                using var directory = new UpdateFixtureDirectory(path);
                var fixture = CreateDeltaFixture();
                SeedDeltaSource(directory, fixture);
                if (failure == "base-modified") File.WriteAllBytes(Path.Combine(path, fixture.Executable), [0]);
                else fixture.Source.Mutate(failure);
                string slot = await new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source)
                    .InstallAsync("2.0.0.alpha.6", "alpha");
                AssertDeltaOutput(directory, slot, fixture);
                AssertEqual(1, fixture.Source.FullRequests);
            }
            finally { Directory.Delete(path, recursive: true); }
        }
        // Invalid optional delta AND full bytes must never publish either payload.
        string rejectedPath = CreateTempDirectory();
        try
        {
            using var directory = new UpdateFixtureDirectory(rejectedPath);
            var fixture = CreateDeltaFixture();
            SeedDeltaSource(directory, fixture);
            fixture.Source.Mutate("bundle-digest"); fixture.Source.Full[^1] ^= 1;
            await ExpectReleaseRejection(() => new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source)
                .InstallAsync("2.0.0.alpha.6", "alpha"));
            using FileStream active = directory.OpenRead(AutomaticUpdateTransaction.ActivationName);
            AssertNull(UpdateTransactionJournal.Read(active));
            using FileStream water = directory.OpenRead("highest-accepted-version.journal");
            AssertNull(UpdateHighWaterJournal.Read(water).Version);
        }
        finally { Directory.Delete(rejectedPath, recursive: true); }
    }

    private static async ValueTask AutomaticUpdateDeltaOfflineRecoveryAndActiveSource()
    {
        foreach (string boundary in new[] { "received", "prepared", "high-water", "accepted", "activated" })
        {
            string path = CreateTempDirectory();
            try
            {
                using var directory = new UpdateFixtureDirectory(path);
                var fixture = CreateDeltaFixture();
                // Exercise an active slot rather than the initial installer layout.
                using (var baseSlot = directory.CreateDirectory("base-slot", publicRead: true)) SeedDeltaSource(baseSlot, fixture);
                using (FileStream active = directory.OpenState(AutomaticUpdateTransaction.ActivationName, publicRead: true, exclusive: false))
                    UpdateTransactionJournal.Append(active, "2.0.0.alpha.5", "base-slot", "2.0.0.alpha.4", "");
                var baseline = fixture.Identity with { Version = "2.0.0.alpha.4" };
                var transaction = new AutomaticUpdateTransaction(directory, baseline, fixture.Verifier, fixture.Source)
                { FaultBoundary = phase => { if (phase == boundary) throw new IOException("Exit after delta reception/preparation"); } };
                try { await transaction.InstallAsync("2.0.0.alpha.6", "alpha"); throw new InvalidOperationException("Missing fault"); }
                catch (IOException) { }
                fixture.Source.Offline = true;
                var recovered = new AutomaticUpdateTransaction(directory, baseline, fixture.Verifier, fixture.Source);
                string slot = await recovered.InstallAsync("2.0.0.alpha.6", "alpha");
                AssertDeltaOutput(directory, slot, fixture);
                AssertEqual(0, fixture.Source.FullRequests);
                AssertEqual(1, fixture.Source.DeltaRequests);
                recovered.Rollback();
                using FileStream state = directory.OpenRead(AutomaticUpdateTransaction.ActivationName);
                AssertEqual("base-slot", UpdateTransactionJournal.Read(state)![1]);
            }
            finally { Directory.Delete(path, recursive: true); }
        }
    }

    private static async ValueTask AutomaticUpdateDeltaCancellationDoesNotDownloadFull()
    {
        string path = CreateTempDirectory();
        try
        {
            using var directory = new UpdateFixtureDirectory(path);
            var fixture = CreateDeltaFixture();
            SeedDeltaSource(directory, fixture);
            using var stop = new CancellationTokenSource();
            fixture.Source.BeforeDelta = () => stop.Cancel();
            try
            {
                await new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source)
                    .InstallAsync("2.0.0.alpha.6", "alpha", stop.Token);
                throw new InvalidOperationException("Cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            AssertEqual(0, fixture.Source.FullRequests);
            using FileStream active = directory.OpenRead(AutomaticUpdateTransaction.ActivationName);
            AssertNull(UpdateTransactionJournal.Read(active));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private static async ValueTask AutomaticUpdateNativeDeltaTransaction()
    {
        if (Environment.GetEnvironmentVariable("NEXA_TEST_PROTECTED_TRANSACTION") != "1")
        { Console.WriteLine("SKIP: dedicated privileged native differential fixture."); return; }
        string root = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : OperatingSystem.IsMacOS() ? "/Library" : "/usr/lib";
        using IUpdateDirectory parent = ProtectedUpdateDirectory.Open(root);
        using IUpdateDirectory directory = parent.CreateDirectory(".nexa-delta-ci-" + Guid.NewGuid().ToString("N"), publicRead: true);
        var fixture = CreateDeltaFixture(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        SeedDeltaSource(directory, fixture);
        var transaction = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source)
        { FaultBoundary = phase => { if (phase == "high-water") throw new IOException("Exit after delta acceptance"); } };
        try { await transaction.InstallAsync("2.0.0.alpha.6", "alpha"); throw new InvalidOperationException("Missing fault"); }
        catch (IOException) { }
        fixture.Source.Offline = true;
        var resumed = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, fixture.Source);
        string slot = await resumed.InstallAsync("2.0.0.alpha.6", "alpha");
        AssertDeltaOutput(directory, slot, fixture);
        AssertEqual(0, fixture.Source.FullRequests);
        AssertEqual(1, fixture.Source.DeltaRequests);
        resumed.Rollback();
    }

    private sealed record DeltaFixture(UpdateBuildIdentity Identity, UpdateGpgVerifier Verifier, DeltaFixtureSource Source,
        string Executable, Dictionary<string, byte[]> BaseFiles, Dictionary<string, byte[]> TargetFiles);

    private static DeltaFixture CreateDeltaFixture(string rid = "win-x64")
    {
        string executable = rid.StartsWith("osx-", StringComparison.Ordinal) ? "Nexa.app/Contents/MacOS/Nexa.Desktop"
            : rid.StartsWith("win-", StringComparison.Ordinal) ? "Nexa.Desktop.exe" : "Nexa.Desktop";
        byte[] bytes = new byte[1024 * 1024]; new Random(42).NextBytes(bytes);
        var baseFiles = new Dictionary<string, byte[]> { [executable] = bytes, [executable.Replace("Nexa.Desktop", "Nexa.Jvm.Host", StringComparison.Ordinal)] = bytes };
        var targetFiles = baseFiles.ToDictionary(p => p.Key, p => p.Value.Concat(new byte[] { 4, 5, 6 }).ToArray());
        targetFiles[rid.StartsWith("osx-", StringComparison.Ordinal) ? "Nexa.app/Contents/Resources/new.txt" : "assets/new.txt"] = [7, 8, 9];
        using var full = new MemoryStream();
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            using var archive = new ZipArchive(full, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in targetFiles) { using Stream entry = archive.CreateEntry(file.Key).Open(); entry.Write(file.Value); }
        }
        else
        {
            using var gzip = new GZipStream(full, CompressionLevel.Fastest, leaveOpen: true);
            using var tar = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true);
            foreach (var file in targetFiles)
            {
                using var data = new MemoryStream(file.Value);
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile,
                    (rid.StartsWith("linux-", StringComparison.Ordinal) ? "Nexa/" : "") + file.Key)
                { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute };
                tar.WriteEntry(entry);
            }
        }
        byte[] fullBytes = full.ToArray(); string targetHash = DeltaHash(fullBytes);
        JsonObject release = JsonNode.Parse(ReleaseManifestFixture())!.AsObject();
        string format = rid.StartsWith("win-", StringComparison.Ordinal) ? "portable.zip" : "portable.tar.gz";
        foreach (JsonNode? item in release["assets"]!.AsArray())
            if (item!["rid"]!.GetValue<string>() == rid && item["format"]!.GetValue<string>() == format)
            { item["size"] = fullBytes.Length; item["sha256"] = targetHash; }
        var fileArray = new JsonArray(); using var literals = new MemoryStream();
        foreach (var file in targetFiles)
        {
            var chunks = new JsonArray();
            if (baseFiles.TryGetValue(file.Key, out byte[]? old)) chunks.Add((JsonNode)new JsonArray("copy", 0, old.Length));
            int offset = baseFiles.TryGetValue(file.Key, out old) ? old.Length : 0;
            chunks.Add((JsonNode)new JsonArray("data", literals.Position, file.Value.Length - offset));
            literals.Write(file.Value.AsSpan(offset));
            fileArray.Add((JsonNode)new JsonObject
            {
                ["path"] = file.Key,
                ["size"] = file.Value.Length,
                ["sha256"] = DeltaHash(file.Value),
                ["executable"] = !rid.StartsWith("win-", StringComparison.Ordinal),
                ["chunks"] = chunks
            });
        }
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["algorithm"] = "nexa-file-delta-v1",
            ["fromVersion"] = "2.0.0.alpha.5",
            ["version"] = "2.0.0.alpha.6",
            ["rid"] = rid,
            ["targetSha256"] = targetHash,
            ["files"] = fileArray
        };
        var key = GenerateSigningKey();
        var source = new DeltaFixtureSource(Encoding.UTF8.GetBytes(release.ToJsonString()), fullBytes, manifest, literals.ToArray(), key.Sign) { PublicKey = key.ArmoredKey };
        return new(new("2.0.0.alpha.5", rid, "nativeaot-self-contained", "Release"), new(key.ArmoredKey, key.Fingerprint), source,
            executable, baseFiles, targetFiles);
    }

    private static string DeltaHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void SeedDeltaSource(IUpdateDirectory directory, DeltaFixture fixture)
    {
        foreach (var file in fixture.BaseFiles)
        {
            var leases = new List<IUpdateDirectory>(); IUpdateDirectory parent = directory;
            string[] parts = file.Key.Split('/');
            try
            {
                foreach (string part in parts[..^1])
                {
                    IUpdateDirectory child;
                    try { child = parent.OpenDirectory(part); }
                    catch (IOException) { child = parent.CreateDirectory(part, publicRead: true); }
                    // Managed fixture OpenDirectory has no admission; create its test directory explicitly.
                    if (directory is UpdateFixtureDirectory) Directory.CreateDirectory(child.Path);
                    leases.Add(child); parent = child;
                }
                using FileStream output = parent.CreateFile(parts[^1], publicRead: true, executable: true);
                output.Write(file.Value); output.Flush(true);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
        using FileStream obsolete = directory.CreateFile("obsolete.txt", publicRead: true); obsolete.Write([0]); obsolete.Flush(true);
    }

    private static void AssertDeltaOutput(IUpdateDirectory directory, string slotName, DeltaFixture fixture)
    {
        using IUpdateDirectory slot = directory.OpenDirectory(slotName);
        foreach (var file in fixture.TargetFiles)
        {
            var leases = new List<IUpdateDirectory>(); IUpdateDirectory parent = slot;
            string[] parts = file.Key.Split('/');
            try
            {
                foreach (string part in parts[..^1]) { parent = parent.OpenDirectory(part); leases.Add(parent); }
                using FileStream output = parent.OpenRead(parts[^1]);
                AssertEqual(DeltaHash(file.Value), Convert.ToHexStringLower(SHA256.HashData(output)));
                AssertEqual((long)file.Value.Length, output.Length);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }

    private sealed class DeltaFixtureSource : IUpdateReleaseSource, IUpdateDeltaSource
    {
        private readonly byte[] _release, _releaseSignature, _data;
        private readonly Func<byte[], string> _sign;
        private readonly JsonObject _manifest;
        private JsonObject _index = null!;
        private byte[] _indexBytes = [], _signature = [];
        internal byte[] Full { get; }
        internal byte[] Bundle { get; private set; } = [];
        internal bool Offline { get; set; }
        internal int FullRequests { get; private set; }
        internal int DeltaRequests { get; private set; }
        internal Action? BeforeDelta { get; set; }
        private string? _failure;
        internal string PublicKey { get; init; } = "";
        internal AutomaticUpdateFixtureSource HttpFixture() => new(_release, _releaseSignature, Full) { PublicKey = PublicKey };
        internal Dictionary<string, byte[]> HttpAssets() => new()
        {
            ["Nexa-Delta.json"] = _indexBytes,
            ["Nexa-Delta.json.asc"] = _signature,
            [_index["patches"]![0]!["name"]!.GetValue<string>()] = Bundle,
        };
        internal DeltaFixtureSource(byte[] release, byte[] full, JsonObject manifest, byte[] data, Func<byte[], string> sign)
        {
            _release = release; Full = full; _manifest = manifest; _data = data; _sign = sign;
            _releaseSignature = Encoding.ASCII.GetBytes(sign(release)); Rebuild();
        }
        private void Rebuild()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (Stream stream = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open()) stream.Write(Encoding.UTF8.GetBytes(_manifest.ToJsonString()));
                using (Stream stream = zip.CreateEntry("data", CompressionLevel.NoCompression).Open()) stream.Write(_data);
            }
            Bundle = buffer.ToArray();
            if (_failure == "stored-size")
            {
                // Stored stream size comes from compressed length; forge the uncompressed declaration.
                for (int i = 0; i < Bundle.Length - 46; i++)
                    if (Bundle.AsSpan(i, 4).SequenceEqual(new byte[] { 0x50, 0x4b, 0x01, 0x02 }))
                    { System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(Bundle.AsSpan(i + 24, 4), 1); break; }
            }
            string rid = _manifest["rid"]!.GetValue<string>();
            _index = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["product"] = "nexacl",
                ["version"] = "2.0.0.alpha.6",
                ["runtimeVariant"] = "nativeaot-self-contained",
                ["configuration"] = "Release",
                ["patches"] = new JsonArray(new JsonObject
                {
                    ["fromVersion"] = "2.0.0.alpha.5",
                    ["rid"] = rid,
                    ["name"] = $"Nexa-2.0.0.alpha.6-{rid}.from-2.0.0.alpha.5.delta.zip",
                    ["size"] = Bundle.Length,
                    ["sha256"] = DeltaHash(Bundle),
                    ["targetSha256"] = DeltaHash(Full)
                })
            };
            SignIndex();
        }
        private void SignIndex() { _indexBytes = Encoding.UTF8.GetBytes(_index.ToJsonString()); _signature = Encoding.ASCII.GetBytes(_sign(_indexBytes)); }
        internal void Mutate(string failure)
        {
            _failure = failure;
            var files = _manifest["files"]!.AsArray();
            if (failure == "source-range") files[0]!["chunks"]![0]![1] = long.MaxValue;
            if (failure == "traversal") files[0]!["path"] = "../escape.exe";
            if (failure == "missing-host") files.RemoveAt(1);
            if (failure == "duplicate") files.Add(files[0]!.DeepClone());
            Rebuild();
            if (failure == "index-signature") _signature = Encoding.ASCII.GetBytes(GenerateSigningKey().Sign(_indexBytes));
            if (failure == "bundle-digest") Bundle[^1] ^= 1;
            if (failure == "target-digest") { _index["patches"]![0]!["targetSha256"] = new string('0', 64); SignIndex(); }
            if (failure == "unprofitable") { _index["patches"]![0]!["size"] = Full.Length; SignIndex(); }
        }
        public Task<(byte[] Manifest, byte[] Signature)> ReadReleaseAsync(string version, CancellationToken token)
            => Offline ? throw new IOException("Offline") : Task.FromResult((_release, _releaseSignature));
        public Task<(byte[] Index, byte[] Signature)> ReadDeltaIndexAsync(string version, CancellationToken token)
            => Offline || _failure == "missing-index" ? throw new FileNotFoundException("No index") : Task.FromResult((_indexBytes, _signature));
        public Task<Stream> OpenPackageAsync(string version, string name, CancellationToken token)
        {
            if (Offline) throw new IOException("Offline");
            if (name.EndsWith(".delta.zip", StringComparison.Ordinal))
            {
                DeltaRequests++; BeforeDelta?.Invoke(); token.ThrowIfCancellationRequested();
                if (_failure == "missing-bundle") throw new FileNotFoundException("No delta");
                return Task.FromResult<Stream>(new MemoryStream(Bundle, writable: false));
            }
            FullRequests++; return Task.FromResult<Stream>(new MemoryStream(Full, writable: false));
        }
    }
}
