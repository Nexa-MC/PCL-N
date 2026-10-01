using System.Security.Cryptography;

namespace Nexa.Sidecar.Transport;

/// <summary>One-use bootstrap proof travels over inherited stdin, never command arguments.</summary>
public static class SidecarBootstrap
{
    public const int ChallengeSize = 32;

    public static async ValueTask<Stream> ConnectAsync(string endpoint, Stream bootstrapInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrapInput);
        byte[] challenge = new byte[ChallengeSize];
        Stream? stream = null;
        try
        {
            await bootstrapInput.ReadExactlyAsync(challenge, cancellationToken).ConfigureAwait(false);
            stream = await SidecarIpcConnector.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(challenge, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch { stream?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }

    public static async ValueTask AuthenticateAsync(Stream stream, ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (expected.Length != ChallengeSize) throw new ArgumentException("Invalid bootstrap challenge length.", nameof(expected));
        byte[] actual = new byte[ChallengeSize];
        try
        {
            await stream.ReadExactlyAsync(actual, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected.Span))
                throw new InvalidDataException("Sidecar bootstrap authentication failed.");
        }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
}
