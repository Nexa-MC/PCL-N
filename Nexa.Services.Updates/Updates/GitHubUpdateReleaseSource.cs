using System.Net;

namespace Nexa.Services.Updates;

/// <summary>Independent publisher fetch: no caller URLs, credentials or tool paths.</summary>
public sealed class GitHubUpdateReleaseSource(HttpClient client) : IUpdateReleaseSource, IUpdateDeltaSource
{
    private const string Repository = "https://github.com/PCL-N-Edition/PCL-N/releases/download/";
    async Task<(byte[] Index, byte[] Signature)> IUpdateDeltaSource.ReadDeltaIndexAsync(string version, CancellationToken token)
    {
        UpdateHighWaterJournal.ParseVersion(version);
        return (await ReadBoundedAsync(version, "Nexa-Delta.json", token).ConfigureAwait(false),
            await ReadBoundedAsync(version, "Nexa-Delta.json.asc", token).ConfigureAwait(false));
    }
    public async Task<(byte[] Manifest, byte[] Signature)> ReadReleaseAsync(string version, CancellationToken token)
    {
        UpdateHighWaterJournal.ParseVersion(version);
        byte[] manifest = await ReadBoundedAsync(version, "Nexa-Release.json", token).ConfigureAwait(false);
        byte[] signature = await ReadBoundedAsync(version, "Nexa-Release.json.asc", token).ConfigureAwait(false);
        return (manifest, signature);
    }
    public Task<Stream> OpenPackageAsync(string version, string name, CancellationToken token)
    {
        UpdateHighWaterJournal.ParseVersion(version);
        if (!name.StartsWith("Nexa-" + version + "-", StringComparison.Ordinal) || name.Any(c => c < 32 || "/\\:?&#".Contains(c)))
            throw new InvalidDataException("发布包名称无效。");
        return OpenAsync(version, name, token);
    }
    private async Task<Stream> OpenAsync(string version, string name, CancellationToken token)
    {
        foreach (string tag in new[] { version, "v" + version })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Repository + tag + "/" + name);
            request.Headers.UserAgent.ParseAdd("NexaCL-Update/1.0");
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); continue; }
            try { response.EnsureSuccessStatusCode(); return new ResponseStream(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), response); }
            catch { response.Dispose(); throw; }
        }
        throw new FileNotFoundException("GitHub 发布缺少更新文件。");
    }
    private async Task<byte[]> ReadBoundedAsync(string version, string name, CancellationToken token)
    {
        using Stream input = await OpenAsync(version, name, token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (count > 1024 * 1024 - output.Length) throw new InvalidDataException("发布封套超过限制。"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private sealed class ResponseStream(Stream stream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => stream.CanRead; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { stream.Dispose(); response.Dispose(); } base.Dispose(disposing); }
    }
}
