using System.Security.Cryptography;
using Nexa.Services.Downloads;
using Nexa.Services.Tasks;

namespace Nexa.Services.Resources;

public sealed class ResourceDownloadService(IResourceCatalogSource catalog, DownloadService downloads, TaskCenterService tasks, HttpClient http)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "CurseForge publishes SHA1 identities; HTTPS protects metadata. Prefer SHA512 when provided.")]
    public async Task DownloadAsync(ResourceDownloadCommand command, CancellationToken token)
    {
        using var task = tasks.Begin(new("resource." + Guid.NewGuid().ToString("N"), "下载资源", ["读取版本", "下载", "校验"]));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, task.CancellationToken);
        token = linked.Token;
        string? stage = null;
        try
        {
            ResourceFile? file;
            if (catalog is IResourceFileSource files) file = await files.ReadFileAsync(command, token).ConfigureAwait(false);
            else
            {
                var detail = await catalog.DetailAsync(new(command.ProjectId) { Sources = [new(command.Provider, command.ProjectId)], MirrorFirst = command.MirrorFirst }, token).ConfigureAwait(false);
                file = detail.Versions.FirstOrDefault(v => v.Id == command.VersionId && v.Provider == command.Provider && v.ProjectId == command.ProjectId)?.File;
            }
            if (file is null) throw new IOException("作者未开放此文件的直接下载，请前往项目主页下载。");
            Validate(file);
            string directory = Path.GetFullPath(command.DestinationDirectory);
            CheckDirectory(directory);
            string destination = Path.Combine(directory, file.Name);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标文件已存在，请选择其他目录。");
            stage = Path.Combine(directory, ".nexa-resource-" + Guid.NewGuid().ToString("N"));
            var result = await downloads.DownloadAsync(new DownloadRequest
            {
                DestinationPath = stage,
                AllowResume = false,
                Sources = Sources(file.Url, command.MirrorFirst),
                ConnectionFactory = url => new Connection(http, url, file.Size, Hash(file.Sha512, 128) ? file.Sha512! : file.Sha1!)
            }, progress => task.Report("下载", file.Name, (double)progress.DownloadedBytes / file.Size, 0, 1, progress.BytesPerSecond), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.Success) throw new IOException("资源下载失败，请重试。");
            task.Report("校验", file.Name, 1, 0, 1, 0);
            await using (var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != file.Size) throw new InvalidDataException("下载文件长度不匹配。");
                string expected = Hash(file.Sha512, 128) ? file.Sha512! : file.Sha1!;
                byte[] actual = expected.Length == 128 ? await SHA512.HashDataAsync(input, token).ConfigureAwait(false) : await SHA1.HashDataAsync(input, token).ConfigureAwait(false);
                if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载文件校验失败。");
            }
            token.ThrowIfCancellationRequested(); CheckDirectory(directory);
            File.Move(stage, destination, overwrite: false);
            task.Complete("已保存：" + file.Name);
        }
        catch (OperationCanceledException) { task.Canceled(); throw; }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException) { task.Fail(e.Message); throw; }
        finally
        {
            if (stage is not null) foreach (string path in new[] { stage, stage + ".PCLDownloading" })
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static void CheckDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("请选择实际存在的下载目录，不支持链接目录。");
    }
    private static bool Hash(string? text, int length) => text?.Length == length && text.All(char.IsAsciiHexDigit);
    private static void Validate(ResourceFile file)
    {
        if (file.Size is <= 0 or > 2L * 1024 * 1024 * 1024 || !Allowed(file.Url) || file.Name.Length is 0 or > 240
            || file.Name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || file.Name.EndsWith('.') || file.Name.EndsWith(' ')
            || !new[] { ".jar", ".zip", ".mrpack" }.Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase)
            || (!Hash(file.Sha1, 40) && !Hash(file.Sha512, 128))) throw new InvalidDataException("资源文件元数据无效，无法安全下载。");
    }
    private static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && uri.Host is "cdn.modrinth.com" or "edge.forgecdn.net" or "mediafilez.forgecdn.net" or "media.forgecdn.net" or "mod.mcimirror.top" or "mcim-files.pysio.online";
    private static string[] Sources(string url, bool mirrorFirst)
    {
        var uri = new Uri(url);
        string mirror = ResourceProviderHttp.Mirror + uri.PathAndQuery;
        return mirrorFirst ? [mirror, url] : [url, mirror];
    }
    private sealed class Connection(HttpClient client, string url, long expected, string expectedHash) : IDownloadConnection
    {
        private HttpResponseMessage? _response;
        private Stream? _stream;
        private long _read;
        private IncrementalHash? _hash;
        public async ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken cancellationToken = default)
        {
            if (beginOffset != 0) throw new InvalidDataException("此资源不支持续传。");
            string current = url;
            for (int i = 0; i < 5; i++)
            {
                if (!Allowed(current)) throw new InvalidDataException("下载地址不受支持。");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                _response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)_response.StatusCode is >= 300 and < 400 && _response.Headers.Location is { } next)
                { current = new Uri(new Uri(current), next).AbsoluteUri; _response.Dispose(); _response = null; continue; }
                _response.EnsureSuccessStatusCode();
                if (_response.Content.Headers.ContentLength is { } length && length != expected) throw new InvalidDataException("资源长度不匹配。");
                _stream = await _response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); _read = 0;
                _hash = IncrementalHash.CreateHash(expectedHash.Length == 128 ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA1);
                return new(expected, 0, expected - 1, false);
            }
            throw new IOException("下载重定向次数过多。");
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await _stream!.ReadAsync(buffer[..(int)Math.Min(buffer.Length, expected - _read + 1)], cancellationToken).ConfigureAwait(false);
            _read += count;
            if (count > 0) _hash!.AppendData(buffer.Span[..count]);
            if (_read > expected || count == 0 && _read != expected) throw new InvalidDataException("资源实际长度不匹配。");
            if (_read == expected && count > 0)
            {
                if (await _stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0) throw new InvalidDataException("资源超出声明长度。");
                if (!Convert.ToHexString(_hash!.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("资源哈希不匹配。");
            }
            return count;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { _hash?.Dispose(); _hash = null; _stream?.Dispose(); _response?.Dispose(); _stream = null; _response = null; return ValueTask.CompletedTask; }
    }
}
