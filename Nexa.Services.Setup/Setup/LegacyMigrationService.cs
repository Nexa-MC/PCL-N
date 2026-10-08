using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Settings;

namespace Nexa.Services.Setup;

/// <summary>Read-only source conversion; never imports credentials or overwrites live stores.</summary>
public sealed class LegacyMigrationService
{
    private sealed record Conversion(LegacyMigrationPreview Preview, JsonObject Settings, JsonObject Profiles);
    private readonly Func<bool> _isIdle;
    private static readonly string[] SecretFragments = ["password", "token", "secret", "authorization", "apikey"];
    public LegacyMigrationService(Func<bool>? isIdle = null) => _isIdle = isIdle ?? (() => true);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Preview and apply share one injected migration port lifetime; previews intentionally remain read-only while work is active.")]
    public async Task<LegacyMigrationPreview> PreviewAsync(string source, string destination, CancellationToken token = default) =>
        (await ConvertAsync(source, destination, token).ConfigureAwait(false)).Preview;

    public async Task ApplyAsync(LegacyMigrationPreview preview, CancellationToken token = default)
    {
        if (!_isIdle()) throw new IOException("请等待活动任务结束后迁移。");
        var conversion = await ConvertAsync(preview.SourceDirectory, preview.DestinationDirectory, token).ConfigureAwait(false);
        if (conversion.Preview.Revision != preview.Revision) throw new IOException("迁移来源在预览后发生变化。");
        string destination = conversion.Preview.DestinationDirectory;
        string stage = destination + ".nexa-import-" + Guid.NewGuid().ToString("N"); ContentBackupService.CheckLinks(stage); Directory.CreateDirectory(stage);
        try
        {
            await WriteAsync(Path.Combine(stage, "settings", "settings.json"), conversion.Settings, token).ConfigureAwait(false);
            await WriteAsync(Path.Combine(stage, "profiles", "profiles.json"), conversion.Profiles, token).ConfigureAwait(false);
            if (!_isIdle()) throw new IOException("任务状态改变，迁移未提交。");
            token.ThrowIfCancellationRequested(); ContentBackupService.CheckLinks(destination); Directory.Move(stage, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static async Task<Conversion> ConvertAsync(string sourceInput, string destinationInput, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(sourceInput) || !Path.IsPathFullyQualified(destinationInput)) throw new IOException("迁移目录必须为绝对路径。");
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceInput)), destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationInput));
        string prefix = source + Path.DirectorySeparatorChar;
        if (Nexa.Core.PathIdentity.Comparer.Equals(source, destination) || destination.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("迁移来源与目标不能相互包含。");
        ContentBackupService.CheckLinks(source); ContentBackupService.CheckLinks(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("迁移目标必须为尚未存在的目录。");
        var settingsSource = await ReadFirstAsync(source, ["settings.json", "settings/settings.json", "PCL/Setup.json"], token).ConfigureAwait(false);
        var profilesSource = await ReadFirstAsync(source, ["launch-profiles.json", "profiles.json", "profiles/profiles.json", "launcher_accounts.json"], token).ConfigureAwait(false);
        if (settingsSource is null && profilesSource is null) throw new IOException("未找到可迁移的设置或账户文件。");
        JsonObject boolean = [], integer = [], text = []; List<string> warnings = []; int settings = 0, offline = 0, reauth = 0;
        var outputSettings = new JsonObject { ["schemaVersion"] = 1, ["booleanOptions"] = boolean, ["integerOptions"] = integer, ["textOptions"] = text };
        if (settingsSource is not null)
        {
            var root = Parse(settingsSource.Value.Bytes);
            if (root["schemaVersion"] is { } schema && schema.GetValue<int>() != 1) throw new IOException("设置源版本不支持。");
            void CopyOptions<T>(string group, IReadOnlyDictionary<string, T> defaults, JsonObject target, JsonValueKind expected)
            {
                JsonObject options = root[group] as JsonObject ?? root;
                foreach (var key in defaults.Keys)
                {
                    if (options[key] is not JsonValue value || IsSecret(key)) continue;
                    using JsonDocument doc = JsonDocument.Parse(value.ToJsonString());
                    if (doc.RootElement.ValueKind != expected && !(expected == JsonValueKind.True && doc.RootElement.ValueKind == JsonValueKind.False)) { warnings.Add("跳过类型不匹配的设置：" + key); continue; }
                    if (value.ToJsonString().Length > 8192) { warnings.Add("跳过过长设置：" + key); continue; }
                    target[key] = value.DeepClone(); settings++;
                }
            }
            CopyOptions("booleanOptions", LauncherDefaults.BooleanDefaults, boolean, JsonValueKind.True);
            CopyOptions("integerOptions", LauncherDefaults.IntegerDefaults, integer, JsonValueKind.Number);
            CopyOptions("textOptions", LauncherDefaults.TextDefaults, text, JsonValueKind.String);
            foreach (string field in new[] { "colorMode", "lightColor", "darkColor", "downloadSource", "automaticallyRepairGameIssues" })
                if (root[field] is JsonValue value && value.ToJsonString().Length <= 128) outputSettings[field] = value.DeepClone();
            warnings.Add("未识别的设置和凭据不会导入；外部 Minecraft/Java 目录未移动。");
        }
        JsonArray profiles = [];
        if (profilesSource is not null)
        {
            var root = Parse(profilesSource.Value.Bytes);
            if (root["schemaVersion"] is { } schema && schema.GetValue<int>() != 1) throw new IOException("账户源版本不支持。");
            IEnumerable<JsonNode?> rows = root["profiles"] is JsonArray list ? list : root["accounts"] is JsonObject accounts ? accounts.Select(x => x.Value) : [];
            int count = 0;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested(); if (++count > 128) throw new IOException("账户数量超过迁移预算。");
                if (row is not JsonObject value) continue;
                string username = ReadText(value, "username") ?? ReadText(value, "name") ?? "";
                string kind = ReadText(value, "kind") ?? (ReadText(value, "type")?.Equals("offline", StringComparison.OrdinalIgnoreCase) == true ? "Offline" : "Microsoft");
                string uuid = ReadText(value, "uuid") ?? "";
                if (username.Length is < 1 or > 64 || username.Any(char.IsControl)) { warnings.Add("跳过无效账户名称。"); continue; }
                if (kind != "Offline") { reauth++; continue; }
                if (uuid.Length != 32 || uuid.Any(c => !char.IsAsciiHexDigit(c)))
                { warnings.Add("离线账户缺少合法 UUID，需要重新添加：" + username); continue; }
                profiles.Add((JsonNode)new JsonObject { ["username"] = username, ["kind"] = "Offline", ["uuid"] = uuid }); offline++;
            }
            if (reauth > 0) warnings.Add($"{reauth} 个在线账户需要重新登录，令牌不会复制到新目录。");
        }
        var outputProfiles = new JsonObject { ["schemaVersion"] = 1, ["profiles"] = profiles };
        string revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + destination + "\0"
            + (settingsSource is { } s ? Convert.ToHexString(SHA256.HashData(s.Bytes)) : "-") + "\0"
            + (profilesSource is { } p ? Convert.ToHexString(SHA256.HashData(p.Bytes)) : "-"))));
        if (settingsSource is { } settingsBytes) CryptographicOperations.ZeroMemory(settingsBytes.Bytes);
        if (profilesSource is { } profileBytes) CryptographicOperations.ZeroMemory(profileBytes.Bytes);
        return new(new(source, destination, revision, settings, offline, reauth, Array.AsReadOnly(warnings.Distinct().Take(64).ToArray())), outputSettings, outputProfiles);
    }

    private static JsonObject Parse(byte[] bytes) => JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new IOException("迁移源不是 JSON 对象。");
    private static string? ReadText(JsonObject source, string key) => source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool IsSecret(string key) => SecretFragments.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
    private static async Task<(string Path, byte[] Bytes)?> ReadFirstAsync(string source, string[] candidates, CancellationToken token)
    {
        foreach (string candidate in candidates)
        {
            string path = Path.Combine(source, candidate); ContentBackupService.CheckLinks(path); if (!File.Exists(path)) continue;
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (input.Length > 4 * 1024 * 1024) throw new IOException("迁移源超过读取预算。");
            byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (input.ReadByte() != -1) throw new IOException("迁移源读取期间发生变化。"); return (path, bytes);
        }
        return null;
    }
    private static async Task WriteAsync(string path, JsonObject json, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(json, StorageJsonContext.Default.JsonObject), token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
    }
}
