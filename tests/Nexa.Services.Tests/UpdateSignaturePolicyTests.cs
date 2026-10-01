using System.Text;
using Nexa.Services.Updates;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask GpgPolicyRejectsWeakHashesAndTextSignatures()
    {
        byte[] payload = "publisher bytes\r\nsecond line\n"u8.ToArray();
        foreach (var hash in new[] { HashAlgorithmTag.Sha256, HashAlgorithmTag.Sha384, HashAlgorithmTag.Sha512 })
        {
            var key = GenerateSigningKey(hashAlgorithm: hash);
            await VerifyPolicySignature(key, payload, expectedFailure: false);
        }
        await VerifyPolicySignature(GenerateSigningKey(hashAlgorithm: HashAlgorithmTag.Sha1), payload, expectedFailure: true, beforeContent: true);
        await VerifyPolicySignature(GenerateSigningKey(signatureType: PgpSignature.CanonicalTextDocument), payload, expectedFailure: true, beforeContent: true);
    }

    private static async ValueTask GpgPolicyUsesTrustedKeyAndAuthenticatedExpiry()
    {
        byte[] payload = "historical publisher bytes"u8.ToArray();
        DateTime past = DateTime.UtcNow.AddDays(-2);
        await VerifyPolicySignature(GenerateSigningKey(keyCreation: past, signatureCreation: past), payload, expectedFailure: false);
        await VerifyPolicySignature(GenerateSigningKey(keyCreation: past, keyExpirationSeconds: 1), payload, expectedFailure: true, beforeContent: true);
        await VerifyPolicySignature(GenerateSigningKey(revoked: true), payload, expectedFailure: true, beforeContent: true);
        await VerifyPolicySignature(GenerateSigningKey(signatureCreation: past, signatureExpirationSeconds: 1), payload, expectedFailure: true);
        // An attacker cannot resurrect an expired signature with unhashed creation time.
        await VerifyPolicySignature(GenerateSigningKey(signatureCreation: past, signatureExpirationSeconds: 1,
            unhashedCreation: DateTime.UtcNow.AddDays(2)), payload, expectedFailure: true);
        // Unauthenticated expiry cannot revoke genuine non-expiring signed bytes either.
        await VerifyPolicySignature(GenerateSigningKey(signatureCreation: past, unhashedExpirationSeconds: 1), payload, expectedFailure: false);
    }

    private static async ValueTask GpgPolicyBoundsEnvelopesAndKeepsCancellation()
    {
        byte[] payload = "bounded publisher bytes"u8.ToArray();
        var key = GenerateSigningKey();
        var verifier = new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint);
        async Task Verify(byte[] signature, bool reject)
        {
            using var content = new MemoryStream(payload);
            using var detached = new MemoryStream(signature);
            bool failed = false;
            try { await verifier.VerifyAsync(content, detached); }
            catch (InvalidDataException) { failed = true; AssertEqual(0L, content.Position); }
            AssertEqual(reject, failed);
        }
        await Verify(new byte[1024 * 1024 + 1], true);
        await Verify(CompressPolicySignature(key.Sign(payload), 0), false);
        await Verify(CompressPolicySignature(key.Sign(payload), 1024 * 1024), true);
        await Verify([0xC2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], true);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var canceledContent = new MemoryStream(payload);
        using var canceledSignature = new MemoryStream(Encoding.ASCII.GetBytes(key.Sign(payload)));
        try { await verifier.VerifyAsync(canceledContent, canceledSignature, stop.Token); throw new InvalidOperationException("Signature cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertEqual(0L, canceledContent.Position);
    }

    private static async Task VerifyPolicySignature(
        (string ArmoredKey, string Fingerprint, Func<byte[], string> Sign) key,
        byte[] payload, bool expectedFailure, bool beforeContent = false)
    {
        using var content = new MemoryStream(payload);
        using var detached = new MemoryStream(Encoding.ASCII.GetBytes(key.Sign(payload)));
        bool failed = false;
        try { await new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint).VerifyAsync(content, detached); }
        catch (InvalidDataException) { failed = true; }
        AssertEqual(expectedFailure, failed);
        if (beforeContent) AssertEqual(0L, content.Position);
    }

    private static byte[] CompressPolicySignature(string armored, int padding)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(armored));
        using var decoded = PgpUtilities.GetDecoderStream(input);
        var signatures = (PgpSignatureList)new PgpObjectFactory(decoded).NextPgpObject();
        using var output = new MemoryStream();
        using (var armor = new ArmoredOutputStream(output))
        {
            var compression = new PgpCompressedDataGenerator(CompressionAlgorithmTag.Zip);
            using Stream data = compression.Open(armor);
            signatures[0].Encode(data);
            data.Write(new byte[padding]);
        }
        return output.ToArray();
    }
}
