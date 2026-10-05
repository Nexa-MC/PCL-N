using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static void SidecarRegistrationTablesOwnAndIndexDeclarations()
    {
        SidecarRegistrationEntry command = new(SidecarRegistrationKind.Command, XsrSemanticId.Parse("api.command"), 1, 0, 0)
        { ResultCodecId = SidecarValueCodecs.Bytes };
        SidecarRegistrationEntry query = new(SidecarRegistrationKind.Query, XsrSemanticId.Parse("api.query"), 1, 0, SidecarValueCodecs.I32);
        SidecarRegistrationEntry second = new(SidecarRegistrationKind.Command, XsrSemanticId.Parse("api.second"), 2, 0, 0);
        List<SidecarRegistrationEntry> source = [command, query, second];
        SidecarRegistrationSet registration = new(source);
        source[0] = command with { ContractId = 99, ResultCodecId = 0 };
        source.Clear();
        AssertEqual(3, registration.Entries.Count);
        AssertEqual(command, registration.TryResolve(SidecarRegistrationKind.Command, command.SemanticId));
        AssertEqual(command, registration.TryResolveId(SidecarRegistrationKind.Command, 1));
        AssertEqual(query, registration.TryResolveId(SidecarRegistrationKind.Query, 1));
        AssertEqual(second, registration.TryResolveId(SidecarRegistrationKind.Command, 2));
        AssertEqual(SidecarValueCodecs.Bytes, registration.TryResolveId(SidecarRegistrationKind.Command, 1)!.ResultCodecId);
        AssertTrue(registration.TryResolveId(SidecarRegistrationKind.Command, 0) is null);
        AssertTrue(registration.TryResolveId(SidecarRegistrationKind.Command, uint.MaxValue) is null);
        AssertTrue(registration.TryResolveId(SidecarRegistrationKind.State, 1) is null);
        AssertTrue(registration.TryResolveId((SidecarRegistrationKind)uint.MaxValue, 1) is null);
        AssertThrows<NotSupportedException>(() => ((IList<SidecarRegistrationEntry>)registration.Entries)[0] = second);
        AssertEqual(0, new SidecarRegistrationSet([]).Entries.Count);

        // Numeric receive dispatch remains allocation-free after the cold semantic resolution.
        uint checksum = 0;
        for (int index = 0; index < 10_000; index++)
            checksum ^= registration.TryResolveId(SidecarRegistrationKind.Command, 1)!.ContractId;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100_000; index++)
            checksum ^= registration.TryResolveId(SidecarRegistrationKind.Command, 1)!.ContractId;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        AssertEqual(0L, allocated);
    }

    private static void SidecarRegistrationTablesRejectMalformedDeclarations()
    {
        SidecarRegistrationEntry valid = new(SidecarRegistrationKind.Command, XsrSemanticId.Parse("api.valid"), 1, 0, 0);
        SidecarRegistrationEntry[] malformed =
        [
            valid with { ContractId = 0 }, valid with { ContractId = uint.MaxValue }, valid with { ContractId = 2 },
            valid with { Kind = (SidecarRegistrationKind)0 }, valid with { Kind = (SidecarRegistrationKind)14 },
            valid with { Kind = (SidecarRegistrationKind)uint.MaxValue }, valid with { SemanticId = default },
            valid with { SemanticId = XsrSemanticId.Parse(new string('a', 257)) },
            valid with { CodecId = 7 }, valid with { ResultCodecId = uint.MaxValue },
            valid with { Kind = SidecarRegistrationKind.Resource, CodecId = 1 },
            valid with { Kind = SidecarRegistrationKind.State, ResultCodecId = 1 },
        ];
        foreach (SidecarRegistrationEntry entry in malformed)
            AssertThrows<SidecarProtocolException>(() => _ = new SidecarRegistrationSet([entry]));
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarRegistrationSet(
            [valid, valid with { SemanticId = XsrSemanticId.Parse("api.duplicate.numeric") }]));
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarRegistrationSet(
            [valid, valid with { Kind = SidecarRegistrationKind.Query }]));
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarRegistrationSet([null!]));
        SidecarRegistrationEntry[] excessive = Enumerable.Range(1, 4097).Select(index =>
            valid with { SemanticId = XsrSemanticId.Parse("api.limit." + index), ContractId = (uint)index }).ToArray();
        AssertThrows<SidecarProtocolException>(() => _ = new SidecarRegistrationSet(excessive));
    }

    private static void SidecarRuntimeCodecsRejectMalformedValuesAndOwnBytes()
    {
        AssertEqual(typeof(string), SidecarValueCodecs.Get(SidecarValueCodecs.Utf8String).ValueType);
        AssertEqual(typeof(bool), SidecarValueCodecs.Get(SidecarValueCodecs.Bool).ValueType);
        AssertEqual(typeof(int), SidecarValueCodecs.Get(SidecarValueCodecs.I32).ValueType);
        AssertEqual(typeof(long), SidecarValueCodecs.Get(SidecarValueCodecs.I64).ValueType);
        AssertEqual(typeof(double), SidecarValueCodecs.Get(SidecarValueCodecs.F64).ValueType);
        AssertThrows<SidecarProtocolException>(() => _ = SidecarValueCodecs.Get(7));
        foreach (byte[] malformed in new byte[][] { [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf0, 0x9f], [0xff] })
        {
            AssertThrows<SidecarProtocolException>(() => SidecarValueCodecs.Validate(SidecarValueCodecs.Utf8String, malformed));
            AssertThrows<SidecarProtocolException>(() => SidecarValueCodecs.Decode(SidecarValueCodecs.Utf8String, malformed));
        }
        AssertThrows<SidecarProtocolException>(() => SidecarValueCodecs.Get(SidecarValueCodecs.Utf8String).Encode("\ud800"));
        foreach (byte[] malformed in new byte[][] { [], [2], [255], [0, 1] })
            AssertThrows<SidecarProtocolException>(() => SidecarValueCodecs.Decode(SidecarValueCodecs.Bool, malformed));
        foreach ((uint codec, byte[] malformed) in new (uint, byte[])[]
            { (SidecarValueCodecs.I32, [1, 2, 3]), (SidecarValueCodecs.I64, new byte[9]), (SidecarValueCodecs.F64, []) })
            AssertThrows<SidecarProtocolException>(() => SidecarValueCodecs.Validate(codec, malformed));
        AssertEqual("接口 ✓", SidecarValueCodecs.Decode(SidecarValueCodecs.Utf8String,
            SidecarValueCodecs.Get(SidecarValueCodecs.Utf8String).Encode("接口 ✓")));
        AssertEqual(false, SidecarValueCodecs.Decode(SidecarValueCodecs.Bool, [0]));
        AssertEqual(true, SidecarValueCodecs.Decode(SidecarValueCodecs.Bool, [1]));
        foreach (uint id in new[] { SidecarValueCodecs.Bytes, SidecarValueCodecs.GeneratedDto })
        {
            AssertEqual(typeof(byte[]), SidecarValueCodecs.Get(id).ValueType);
            byte[] source = [1, 2, 3];
            byte[] encoded = SidecarValueCodecs.Get(id).Encode(source);
            source[0] = 99;
            AssertEqual((byte)1, encoded[0]);
            byte[] decoded = (byte[])SidecarValueCodecs.Decode(id, encoded);
            decoded[0] = 88;
            AssertEqual((byte)1, encoded[0]);
            byte[] independentlyDecoded = (byte[])SidecarValueCodecs.Decode(id, encoded);
            AssertEqual((byte)1, independentlyDecoded[0]);
        }
    }

    private static void SidecarVerifiedCacheOwnsAllPublicArrays()
    {
        SidecarHostCache cache = new();
        XsrSemanticId first = XsrSemanticId.Parse("cache.resource.first");
        XsrSemanticId second = XsrSemanticId.Parse("cache.resource.second");
        XsrSemanticId module = XsrSemanticId.Parse("cache.module");
        byte[] bytes = [1, 2, 3];
        byte[] expected = bytes.ToArray();
        byte[] hash = SHA256.HashData(bytes);
        byte[] expectedHash = hash.ToArray();
        cache.AddResource(first, bytes, hash);
        bytes[0] = 99;
        hash[0] ^= 255;
        cache.AddResource(second, expected, expectedHash);
        AssertEqual(1, cache.ResourceCount);
        AssertTrue(cache.TryGetResource(first, out byte[]? content));
        AssertTrue(expected.AsSpan().SequenceEqual(content));
        content![0] = 77;
        AssertTrue(cache.TryGetResource(expectedHash, out byte[]? legacyContent));
        AssertTrue(expected.AsSpan().SequenceEqual(legacyContent));
        legacyContent![0] = 66;
        AssertTrue(cache.TryGetResource(second, out byte[]? aliasContent));
        AssertTrue(expected.AsSpan().SequenceEqual(aliasContent));
        AssertTrue(cache.TryGetResourceHash(first, out byte[]? admittedHash));
        admittedHash![0] ^= 255;
        AssertTrue(cache.TryGetResourceHash(first, out byte[]? secondHash));
        AssertTrue(expectedHash.AsSpan().SequenceEqual(secondHash));
        AssertThrows<SidecarProtocolException>(() => cache.AddResource(first, [9], expectedHash));
        AssertTrue(cache.TryGetResource(first, out byte[]? preserved));
        AssertTrue(expected.AsSpan().SequenceEqual(preserved));

        XsrSemanticId missing = XsrSemanticId.Parse("cache.resource.missing");
        AssertThrows<SidecarProtocolException>(() => cache.AddUiModule(module, expected, expectedHash, [missing]));
        AssertFalse(cache.TryOpenUiModule(module, out _));
        AssertFalse(cache.TryGetRequiredUiResources(module, out _));
        XsrSemanticId[] dependencies = [first, second];
        byte[] moduleInput = expected.ToArray();
        cache.AddUiModule(module, moduleInput, expectedHash, dependencies);
        moduleInput[0] = 55;
        dependencies[0] = missing;
        AssertTrue(cache.TryOpenUiModule(module, out byte[]? moduleOutput));
        AssertTrue(expected.AsSpan().SequenceEqual(moduleOutput));
        moduleOutput![0] = 44;
        AssertTrue(cache.TryOpenUiModule(module, out byte[]? moduleAgain));
        AssertTrue(expected.AsSpan().SequenceEqual(moduleAgain));
        AssertTrue(cache.TryGetRequiredUiResources(module, out IReadOnlyList<XsrSemanticId>? admittedReferences));
        AssertEqual(first, admittedReferences![0]);
        AssertEqual(second, admittedReferences[1]);
        AssertThrows<NotSupportedException>(() => ((IList<XsrSemanticId>)admittedReferences)[0] = missing);
        AssertFalse(cache.TryGetResource(missing, out _));
        AssertFalse(cache.TryGetResourceHash(missing, out _));
        AssertFalse(cache.TryGetResource(new byte[33], out _));
        AssertFalse(cache.TryGetResource(Array.Empty<byte>(), out _));
    }

    private static async ValueTask SidecarRegisteredContentAndExtensionsReadLocallyWithoutAliases()
    {
        // A module may precede its dependencies on the registration stream; admission resolves
        // the complete candidate before publishing either the cache or its dependency table.
        byte[] moduleBytes = [1, 2, 3];
        byte[] resourceBytes = [7, 8, 9];
        XsrSemanticId page = XsrSemanticId.Parse("local.page");
        XsrSemanticId resource = XsrSemanticId.Parse("local.resource");
        CountingSidecarTraffic? traffic = null;
        var (session, peer) = await PatchSession(new(CaptionPoint),
        [
            new(SidecarRegistrationKind.UiModule, page.Value, 0, 0, moduleBytes, SHA256.HashData(moduleBytes), resource.Value),
            new(SidecarRegistrationKind.Resource, resource.Value, 0, 0, resourceBytes, SHA256.HashData(resourceBytes)),
            CaptionItem("local.caption", "Owned"),
        ], wrap: inner => traffic = new(inner));
        using (session) using (peer)
        {
            int baseline = traffic!.Writes;
            int baselineReads = traffic.Reads;
            for (int index = 0; index < 3; index++)
            {
                AssertTrue(session.Cache.TryOpenUiModule(page, out byte[]? pageOutput));
                AssertTrue(moduleBytes.AsSpan().SequenceEqual(pageOutput));
                pageOutput![0] = 99;
                AssertTrue(session.Cache.TryGetResource(resource, out byte[]? resourceOutput));
                AssertTrue(resourceBytes.AsSpan().SequenceEqual(resourceOutput));
                resourceOutput![0] = 88;
                AssertTrue(session.Cache.TryGetRequiredUiResources(page, out IReadOnlyList<XsrSemanticId>? references));
                AssertEqual(resource, references![0]);
                SidecarExtensionRegistration declaration = session.Extensions.Entries.Single();
                AssertEqual("Owned", SidecarUiCaptionPatch.Decode(declaration.Payload.Span));
                AssertTrue(MemoryMarshal.TryGetArray(declaration.Payload, out ArraySegment<byte> exposed));
                exposed.Array![exposed.Offset] = 255;
                AssertEqual("Owned", SidecarUiCaptionPatch.Decode(session.Extensions.Entries.Single().Payload.Span));
            }
            AssertEqual(baseline, traffic.Writes);
            AssertEqual(baselineReads, traffic.Reads);
        }
    }

    private sealed class CountingSidecarTraffic(Stream inner) : Stream
    {
        internal int Writes { get; private set; }
        internal int Reads { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return inner.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            inner.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return inner.WriteAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
