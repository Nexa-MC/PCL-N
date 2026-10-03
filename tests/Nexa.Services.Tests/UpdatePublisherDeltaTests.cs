using System.Security.Cryptography;
using System.Text;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async Task<int> RunPublisherDeltaFixture(string fixturePath)
    {
        string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var key = GenerateSigningKey();
        var fixture = new PublisherDeltaSource(Path.Combine(fixturePath, "artifacts"), key.Sign);
        string path = CreateTempDirectory();
        try
        {
            using var directory = new UpdateFixtureDirectory(path);
            string source = Path.Combine(fixturePath, "source", rid), target = Path.Combine(fixturePath, "target", rid);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(path, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            var identity = new UpdateBuildIdentity("2.0.0.alpha.5", rid, "nativeaot-self-contained", "Release");
            string slot = await new AutomaticUpdateTransaction(directory, identity, new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint), fixture)
                .InstallAsync("2.0.0.alpha.6", "alpha");
            foreach (string expected in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                string actual = Path.Combine(path, slot, Path.GetRelativePath(target, expected));
                using FileStream first = File.OpenRead(expected), second = File.OpenRead(actual);
                AssertEqual(Convert.ToHexStringLower(SHA256.HashData(first)), Convert.ToHexStringLower(SHA256.HashData(second)));
            }
            AssertEqual(0, fixture.FullTransfers);
            AssertEqual(1, fixture.DeltaTransfers);
            string runtime = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "managed" : "NativeAOT";
            Console.WriteLine($"PASS publisher-generated differential bundle consumed by {runtime} updater: {rid}, full transfers=0");
            return 0;
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private sealed class PublisherDeltaSource(string directory, Func<byte[], string> sign) : IUpdateReleaseSource, IUpdateDeltaSource
    {
        internal int FullTransfers { get; private set; }
        internal int DeltaTransfers { get; private set; }
        public Task<(byte[] Manifest, byte[] Signature)> ReadReleaseAsync(string version, CancellationToken token)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, "Nexa-Release.json"));
            return Task.FromResult((bytes, Encoding.ASCII.GetBytes(sign(bytes))));
        }
        public Task<(byte[] Index, byte[] Signature)> ReadDeltaIndexAsync(string version, CancellationToken token)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, "Nexa-Delta.json"));
            return Task.FromResult((bytes, Encoding.ASCII.GetBytes(sign(bytes))));
        }
        public Task<Stream> OpenPackageAsync(string version, string name, CancellationToken token)
        {
            if (name.EndsWith(".delta.zip", StringComparison.Ordinal)) DeltaTransfers++; else FullTransfers++;
            return Task.FromResult<Stream>(File.OpenRead(Path.Combine(directory, name)));
        }
    }
}
