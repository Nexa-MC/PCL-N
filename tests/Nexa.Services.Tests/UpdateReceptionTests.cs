using System.Text;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static readonly byte[] ReceptionPackage = "publisher fixture package\n"u8.ToArray();

    private static async Task<VerifiedReleasePackage> ReceptionReleaseAsync()
    {
        byte[] manifest = ReleaseManifestFixture();
        var key = GenerateSigningKey();
        return await VerifiedReleasePackage.VerifyAsync(manifest, Encoding.ASCII.GetBytes(key.Sign(manifest)),
            ReleaseInstalled, "alpha", ReleaseInstalled.Version, "portable.tar.gz",
            new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint));
    }

    private static async ValueTask UpdateReceptionCopiesVerifiedBytesWithoutReopening()
    {
        VerifiedReleasePackage release = await ReceptionReleaseAsync();
        byte[] mutable = ReceptionPackage.ToArray();
        using var source = new ReceptionSource(mutable);
        string directory = CreateTempDirectory();
        try
        {
            using var destination = new FileStream(Path.Combine(directory, "received"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            await release.CopyVerifiedPackageAsync(source, destination);
            AssertEqual((long)ReceptionPackage.Length, destination.Length);
            AssertEqual(0L, destination.Position);
            AssertEqual(ReceptionPackage.Length, source.ActualRead);
            AssertTrue(source.CanRead);
            Array.Fill(mutable, (byte)0);
            byte[] actual = new byte[ReceptionPackage.Length];
            await destination.ReadExactlyAsync(actual);
            AssertTrue(actual.SequenceEqual(ReceptionPackage));
            destination.Position = 0;
            await release.VerifyPackageAsync(destination);
        }
        finally { Directory.Delete(directory, recursive: true); }

        // A prior verify-only pass is not permission to consume subsequently changed bytes.
        mutable = ReceptionPackage.ToArray();
        using var formerlyValid = new MemoryStream(mutable);
        await release.VerifyPackageAsync(formerlyValid);
        formerlyValid.Position = 0;
        mutable[0] ^= 1;
        using var rejected = new MemoryStream();
        await ExpectReleaseRejection(() => release.CopyVerifiedPackageAsync(formerlyValid, rejected));
        AssertEqual(0L, rejected.Length);
    }

    private static async ValueTask UpdateReceptionRejectsActualLengthAndDigest()
    {
        VerifiedReleasePackage release = await ReceptionReleaseAsync();
        byte[] modified = ReceptionPackage.ToArray();
        modified[0] ^= 1;
        foreach (byte[] data in new[] { ReceptionPackage[..^1], ReceptionPackage.Concat(new byte[] { 0 }).ToArray(), modified })
        {
            using var input = new ReceptionSource(data);
            using var output = new ReceptionDestination();
            await ExpectReleaseRejection(() => release.CopyVerifiedPackageAsync(input, output));
            AssertEqual(0L, output.Length);
            AssertEqual(0L, output.Position);
            AssertTrue(output.TotalWritten <= release.Size);
            AssertEqual(0, output.FlushCalls);
        }

        using var unread = new ReceptionSource(ReceptionPackage);
        using var nonempty = new MemoryStream();
        nonempty.WriteByte(42);
        nonempty.Position = 0;
        try { await release.CopyVerifiedPackageAsync(unread, nonempty); throw new InvalidOperationException("Nonempty destination accepted."); }
        catch (ArgumentException) { }
        AssertEqual(0, unread.ActualRead);
        AssertEqual(1L, nonempty.Length);
        AssertEqual(42, nonempty.ReadByte());
        try { await release.CopyVerifiedPackageAsync(nonempty, nonempty); throw new InvalidOperationException("Aliased stream accepted."); }
        catch (ArgumentException) { }
        using var readonlyDestination = new MemoryStream([], writable: false);
        try { await release.CopyVerifiedPackageAsync(unread, readonlyDestination); throw new InvalidOperationException("Read-only destination accepted."); }
        catch (ArgumentException) { }
        AssertEqual(0, unread.ActualRead);
    }

    private static async ValueTask UpdateReceptionInvalidatesCancellationAndIoFailure()
    {
        VerifiedReleasePackage release = await ReceptionReleaseAsync();
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        using var unread = new ReceptionSource(ReceptionPackage);
        using var untouched = new MemoryStream();
        try { await release.CopyVerifiedPackageAsync(unread, untouched, stopped.Token); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertEqual(0, unread.ActualRead);
        AssertEqual(0L, untouched.Length);

        using var duringCopy = new CancellationTokenSource();
        using var canceledSource = new ReceptionSource(ReceptionPackage, beforeRead: read =>
        {
            if (read > 0) duringCopy.Cancel();
        });
        using var canceledOutput = new ReceptionDestination();
        try { await release.CopyVerifiedPackageAsync(canceledSource, canceledOutput, duringCopy.Token); throw new InvalidOperationException("Mid-copy cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertTrue(canceledOutput.TotalWritten > 0);
        AssertEqual(0L, canceledOutput.Length);

        using var brokenSource = new ReceptionSource(ReceptionPackage, beforeRead: read =>
        {
            if (read > 0) throw new IOException("Source failed.");
        });
        using var brokenSourceOutput = new ReceptionDestination();
        try { await release.CopyVerifiedPackageAsync(brokenSource, brokenSourceOutput); throw new InvalidOperationException("Read failure ignored."); }
        catch (IOException) { }
        AssertTrue(brokenSourceOutput.TotalWritten > 0);
        AssertEqual(0L, brokenSourceOutput.Length);

        foreach (bool failFlush in new[] { false, true })
        {
            using var input = new ReceptionSource(ReceptionPackage);
            using var output = new ReceptionDestination(failWrite: !failFlush, failFlush: failFlush);
            try { await release.CopyVerifiedPackageAsync(input, output); throw new InvalidOperationException("Destination failure ignored."); }
            catch (IOException) { }
            AssertTrue(output.TotalWritten > 0);
            AssertEqual(0L, output.Length);
        }

        using var cleanupSource = new ReceptionSource(ReceptionPackage);
        using var cleanupOutput = new ReceptionDestination(failWrite: true, failCleanup: true);
        try { await release.CopyVerifiedPackageAsync(cleanupSource, cleanupOutput); throw new InvalidOperationException("Cleanup failure ignored."); }
        catch (AggregateException failure)
        {
            AssertEqual(2, failure.InnerExceptions.Count);
            AssertTrue(failure.InnerExceptions.All(static error => error is IOException));
        }
        // Failed output is never admitted, even when the filesystem prevents cleanup.
        AssertTrue(cleanupOutput.Length > 0);
    }

    private sealed class ReceptionSource(byte[] data, Action<int>? beforeRead = null) : Stream
    {
        public int ActualRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke(ActualRead);
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, 7), data.Length - ActualRead);
            data.AsMemory(ActualRead, count).CopyTo(buffer);
            ActualRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ReceptionDestination(bool failWrite = false, bool failFlush = false, bool failCleanup = false) : MemoryStream
    {
        public long TotalWritten { get; private set; }
        public int FlushCalls { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            TotalWritten += buffer.Length;
            if (failWrite) throw new IOException("Destination failed after partial write.");
        }
        public override Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCalls++;
            if (failFlush) throw new IOException("Destination flush failed.");
            return base.FlushAsync(cancellationToken);
        }
        public override void SetLength(long value)
        {
            if (failCleanup) throw new IOException("Destination cleanup failed.");
            base.SetLength(value);
        }
    }
}
