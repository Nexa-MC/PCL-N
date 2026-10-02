using System.Security.Cryptography;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace Nexa.Sidecar.Tests;

internal static partial class Program
{
    private static async ValueTask BootstrapRejectsWrongChallenge()
    {
        byte[] expected = RandomNumberGenerator.GetBytes(SidecarBootstrap.ChallengeSize);
        using var correct = new MemoryStream(expected);
        await SidecarBootstrap.AuthenticateAsync(correct, expected);
        byte[] wrong = expected.ToArray(); wrong[0] ^= 1;
        using var invalid = new MemoryStream(wrong);
        bool rejected = false;
        try { await SidecarBootstrap.AuthenticateAsync(invalid, expected); }
        catch (InvalidDataException) { rejected = true; }
        AssertTrue(rejected);
        using var shortInput = new MemoryStream(expected[..12]);
        rejected = false;
        try { await SidecarBootstrap.AuthenticateAsync(shortInput, expected); }
        catch (EndOfStreamException) { rejected = true; }
        AssertTrue(rejected);
    }

    private static void ExtensionsRequireTargetAndContent()
    {
        foreach (var kind in Enum.GetValues<SidecarRegistrationKind>().Where(SidecarRegistration.IsExtension))
        {
            byte[] payload = [1, 2, 3];
            var item = new SidecarRegistrationItem(kind, "plugin.extension", 0, 0,
                payload, SHA256.HashData(payload), TargetSemanticId: "host.target");
            var decoded = SidecarRegistration.DecodeItem(SidecarRegistration.EncodeItem(item));
            AssertEqual(kind, decoded.Kind);
            AssertEqual("host.target", decoded.TargetSemanticId);
            AssertTrue(payload.SequenceEqual(decoded.Payload!));
            bool rejected = false;
            try { SidecarRegistration.DecodeItem(SidecarRegistration.EncodeItem(item with { TargetSemanticId = null })); }
            catch (SidecarProtocolException) { rejected = true; }
            AssertTrue(rejected);
        }
    }

    private static async ValueTask AcceptedStreamOutlivesListener()
    {
        using var listener = SidecarIpcListener.Bind("ownership-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<Stream> accepted = listener.AcceptAsync(deadline.Token).AsTask();
        using Stream client = await SidecarIpcConnector.ConnectAsync(listener.Endpoint, deadline.Token);
        using Stream server = await accepted;
        listener.Dispose();
        byte[] received = new byte[1];
        // Unbuffered pipe writes may wait for the reader even for one byte.
        Task serverReading = server.ReadExactlyAsync(received, deadline.Token).AsTask();
        await client.WriteAsync(new byte[] { 42 }, deadline.Token);
        await serverReading;
        AssertEqual((byte)42, received[0]);
        Task clientReading = client.ReadExactlyAsync(received, deadline.Token).AsTask();
        await server.WriteAsync(new byte[] { 43 }, deadline.Token);
        await clientReading;
        AssertEqual((byte)43, received[0]);
    }
}
