using System.Globalization;
using Nexa.Services.Logging;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Accounts;

internal sealed record AccountWardrobeCapture(AccountWardrobeIdentity Identity, LaunchProfile Profile,
    LaunchProfileView View);

/// <summary>Provider orchestration for the selected wardrobe. Credentials stay inside Accounts.</summary>
public sealed class AccountWardrobeService(AccountService accounts, HttpClient http, AccountSkinService skins,
    IMicrosoftMinecraftAuthService? microsoft = null, string? microsoftClientId = null,
    ILittleSkinOAuthService? littleSkin = null, LittleSkinOAuthConfiguration? littleSkinConfiguration = null,
    LogService? log = null)
{
    private readonly MinecraftSkinService _skin = new(http);
    private readonly MinecraftCapeService _cape = new(http);

    internal static XsrError StaleIdentity() => new(XsrErrorKind.Rejected,
        XsrSemanticId.Parse("accounts.wardrobe.stale_identity"), "当前账户已变化，请刷新更衣橱后重试。");
    private static XsrError Failed(string message) => new(XsrErrorKind.Rejected,
        XsrSemanticId.Parse("accounts.wardrobe.rejected"), message);
    private static string? Unavailable(LaunchProfileKind kind) => kind switch
    {
        LaunchProfileKind.Microsoft => null,
        LaunchProfileKind.LittleSkin => null,
        LaunchProfileKind.Offline => "离线账户不支持在线更换皮肤或披风。",
        _ => "此认证提供方未提供可用的皮肤和披风管理接口。"
    };

    public async ValueTask<XsrResult<AccountWardrobeSnapshot>> ReadAsync(AccountWardrobeQuery query,
        CancellationToken cancellationToken = default)
    {
        var capture = accounts.CaptureActiveWardrobe();
        if (!capture.IsSuccess) return XsrResult.Failure<AccountWardrobeSnapshot>(capture.Error!);
        if (Unavailable(capture.Value.Profile.Kind) is { } reason)
            return XsrResult.Success(Snapshot(capture.Value, [], reason));
        try
        {
            var current = await Authenticate(capture.Value, cancellationToken).ConfigureAwait(false);
            using var operation = new Operation(accounts, current, cancellationToken);
            var capes = await ReadCapes(current.Profile, operation.Token).ConfigureAwait(false);
            operation.Admit();
            return XsrResult.Success(Snapshot(current, capes));
        }
        catch (Exception error) when (Recoverable(error))
        { return Reject<AccountWardrobeSnapshot>(error); }
    }

    public async ValueTask<XsrResult<AccountWardrobeSkinPreview>> ValidateSkinAsync(AccountWardrobeSkinQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capture = Require(query.Identity);
            DemandProvider(capture.Profile);
            using var operation = new Operation(accounts, capture, cancellationToken);
            string path = Path.GetFullPath(query.Path);
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || info.Length is <= 0 or > 1_048_576 || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择不超过 1 MiB 的普通 PNG 皮肤文件。");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is <= 0 or > 1_048_576) throw new InvalidDataException("皮肤文件超过大小限制。");
            byte[] bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, operation.Token).ConfigureAwait(false);
            if (file.ReadByte() != -1) throw new InvalidDataException("皮肤文件在读取期间发生变化。");
            var image = WardrobeSkinValidator.Validate(bytes, query.IsSlim);
            operation.Admit();
            return XsrResult.Success(new AccountWardrobeSkinPreview(image, query.IsSlim, Path.GetFileName(path)));
        }
        catch (Exception error) when (Recoverable(error))
        { return Reject<AccountWardrobeSkinPreview>(error); }
    }

    public async ValueTask<XsrResult> UploadSkinAsync(AccountWardrobeUploadSkinCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capture = Require(command.Identity);
            DemandProvider(capture.Profile);
            if (command.FileName is { Length: > 256 }) throw new InvalidDataException("皮肤文件名过长。");
            // Copy the command payload before validation; caller mutation cannot alter transmitted bytes.
            if (command.PngBytes is null || command.PngBytes.Length > 1_048_576)
                throw new InvalidDataException("皮肤文件超过大小限制。");
            var image = WardrobeSkinValidator.Validate(command.PngBytes.ToArray(), command.IsSlim);
            capture = await Authenticate(capture, cancellationToken).ConfigureAwait(false);
            string? address;
            using (var operation = new Operation(accounts, capture, cancellationToken))
            {
                if (capture.Profile.Kind == LaunchProfileKind.Microsoft)
                {
                    var result = await _skin.UploadAsync(capture.Profile.AccessToken, image.Bytes.ToArray(),
                        command.FileName, command.IsSlim, operation.Token).ConfigureAwait(false);
                    address = result.SkinAddress;
                }
                else
                {
                    await littleSkin!.UploadMinecraftTextureAsync(capture.Profile.AccessToken, capture.Profile.Uuid,
                        image.Bytes.ToArray(), command.FileName, command.IsSlim, operation.Token).ConfigureAwait(false);
                    address = "uuid:" + capture.Profile.Uuid;
                }
                operation.Admit();
            }
            var saved = accounts.CommitWardrobeSkin(capture.Identity, address, cancellationToken);
            if (!saved.IsSuccess) return saved;
            skins.Refresh(force: true);
            return XsrResult.Success();
        }
        catch (Exception error) when (Recoverable(error)) { return Reject(error); }
    }

    public async ValueTask<XsrResult> SetCapeAsync(AccountWardrobeSetCapeCommand command,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var capture = Require(command.Identity);
            DemandProvider(capture.Profile);
            capture = await Authenticate(capture, cancellationToken).ConfigureAwait(false);
            using var operation = new Operation(accounts, capture, cancellationToken);
            if (capture.Profile.Kind == LaunchProfileKind.Microsoft)
            {
                if (command.CapeId is null) await _cape.ClearActiveCapeAsync(capture.Profile.AccessToken, operation.Token).ConfigureAwait(false);
                else await _cape.SetActiveCapeAsync(capture.Profile.AccessToken, command.CapeId, operation.Token).ConfigureAwait(false);
            }
            else
            {
                var capes = await ReadCapes(capture.Profile, operation.Token).ConfigureAwait(false);
                operation.Admit();
                long id = 0;
                if (command.CapeId is not null && (!capes.Any(c => c.Id == command.CapeId)
                    || !long.TryParse(command.CapeId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0))
                    throw new InvalidDataException("所选披风不属于当前 LittleSkin 衣柜。");
                var player = await ReadPlayer(capture.Profile, operation.Token).ConfigureAwait(false);
                operation.Admit();
                await littleSkin!.ApplyTextureAsync(capture.Profile.ProviderAccessToken, player.PlayerId, id,
                    LittleSkinTextureKind.Cape, operation.Token).ConfigureAwait(false);
            }
            operation.Admit();
            return XsrResult.Success();
        }
        catch (Exception error) when (Recoverable(error)) { return Reject(error); }
    }

    private AccountWardrobeCapture Require(AccountWardrobeIdentity identity)
    {
        var captured = accounts.CaptureActiveWardrobe(identity);
        if (!captured.IsSuccess) throw new StaleWardrobeException();
        return captured.Value;
    }
    private void DemandProvider(LaunchProfile profile)
    {
        if (Unavailable(profile.Kind) is { } reason) throw new InvalidDataException(reason);
        if (profile.Kind == LaunchProfileKind.LittleSkin && littleSkin is null)
            throw new InvalidDataException("LittleSkin 外观接口不可用，请检查账户配置。");
    }
    private async Task<AccountWardrobeCapture> Authenticate(AccountWardrobeCapture capture, CancellationToken token)
    {
        DemandProvider(capture.Profile);
        using var operation = new Operation(accounts, capture, token);
        var profile = capture.Profile;
        LaunchProfile? updated = null;
        if (profile.Kind == LaunchProfileKind.Microsoft && microsoft is not null
            && !string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(profile.RefreshToken))
        {
            var result = await microsoft.RefreshAsync(microsoftClientId, profile.RefreshToken, operation.Token).ConfigureAwait(false);
            operation.Admit();
            if (!result.OwnsMinecraft || string.IsNullOrWhiteSpace(result.AccessToken)
                || !string.Equals(result.Uuid.Replace("-", ""), profile.Uuid.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("此账户的 Minecraft 身份无法验证，请重新登录。");
            updated = profile with
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                Username = string.IsNullOrWhiteSpace(result.Username) ? profile.Username : result.Username
            };
        }
        if (profile.Kind == LaunchProfileKind.LittleSkin && profile.ProviderTokenExpiresAtUnix <= DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds())
        {
            if (littleSkinConfiguration is null || string.IsNullOrWhiteSpace(profile.RefreshToken))
                throw new InvalidDataException("LittleSkin 外观授权已过期，请重新登录。");
            var result = await littleSkin!.RefreshOAuthTokenAsync(littleSkinConfiguration, profile.RefreshToken, operation.Token).ConfigureAwait(false);
            operation.Admit();
            updated = profile with
            {
                ProviderAccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                ProviderTokenExpiresAtUnix = result.ExpiresAt.ToUnixTimeSeconds()
            };
        }
        if (profile.Kind == LaunchProfileKind.LittleSkin)
        {
            var provider = updated ?? profile;
            var session = await littleSkin!.CreateMinecraftSessionAsync(provider.ProviderAccessToken,
                profile.Uuid, operation.Token).ConfigureAwait(false);
            operation.Admit();
            if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.Username)
                || !string.Equals(session.Uuid.Replace("-", ""), profile.Uuid.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("LittleSkin 返回了其他角色或无效会话，请重新登录。");
            updated = provider with { Username = session.Username, AccessToken = session.AccessToken, ClientToken = session.ClientToken };
        }
        operation.Admit();
        if (updated is not null)
        {
            operation.Dispose(); // Our durable credential refresh creates the next admitted generation.
            var saved = accounts.CommitWardrobeProfile(capture.Identity, updated, token);
            if (!saved.IsSuccess) throw new StaleWardrobeException();
            capture = saved.Value;
        }
        if (string.IsNullOrWhiteSpace(capture.Profile.AccessToken)
            || capture.Profile.Kind == LaunchProfileKind.LittleSkin && string.IsNullOrWhiteSpace(capture.Profile.ProviderAccessToken))
            throw new InvalidDataException("账户缺少有效授权，请重新登录。");
        return capture;
    }
    private async Task<IReadOnlyList<AccountWardrobeCape>> ReadCapes(LaunchProfile profile, CancellationToken token)
    {
        if (profile.Kind == LaunchProfileKind.Microsoft)
            return Array.AsReadOnly((await _cape.GetOwnedCapesAsync(profile.AccessToken, token).ConfigureAwait(false))
                .Select(c => new AccountWardrobeCape(c.Id, c.Alias, c.TextureAddress, c.IsActive)).ToArray());
        var player = await ReadPlayer(profile, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var closet = await littleSkin!.GetClosetItemsAsync(profile.ProviderAccessToken, LittleSkinTextureKind.Cape, token).ConfigureAwait(false);
        if (closet.Count > 256) throw new InvalidDataException("账户披风数量超过读取限制。");
        return Array.AsReadOnly(closet.Where(c => c.TextureId > 0).Select(c => new AccountWardrobeCape(
            c.TextureId.ToString(CultureInfo.InvariantCulture), c.Name, c.TextureAddress, c.TextureId == player.CapeTextureId)).ToArray());
    }
    private async Task<LittleSkinPlayer> ReadPlayer(LaunchProfile profile, CancellationToken token)
    {
        var players = await littleSkin!.GetPlayersAsync(profile.ProviderAccessToken, token).ConfigureAwait(false);
        var matching = players.Where(p => string.Equals(p.Username, profile.Username, StringComparison.Ordinal)).Take(2).ToArray();
        if (matching.Length != 1) throw new InvalidDataException("无法唯一识别当前 LittleSkin 角色，请重新登录。");
        return matching[0];
    }
    private static AccountWardrobeSnapshot Snapshot(AccountWardrobeCapture capture, IReadOnlyList<AccountWardrobeCape> capes, string? reason = null)
        => new(capture.Identity, capture.View, reason is null, reason is null, reason, capes);
    private static bool Recoverable(Exception error) => error is IOException or InvalidDataException or HttpRequestException or InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or OperationCanceledException;
    private XsrResult<T> Reject<T>(Exception error) => XsrResult.Failure<T>(Error(error));
    private XsrResult Reject(Exception error) => XsrResult.Failure(Error(error));
    private XsrError Error(Exception error)
    {
        log?.Warn("AccountWardrobe", "Wardrobe operation rejected: " + error.GetType().Name);
        if (error is StaleWardrobeException) return StaleIdentity();
        if (error is OperationCanceledException) return new(XsrErrorKind.Cancelled,
            XsrSemanticId.Parse("accounts.wardrobe.cancelled"), "更衣橱操作已取消。");
        return Failed(error is InvalidDataException ? error.Message : "无法完成外观操作，请检查网络和账户授权后重试。");
    }
    private sealed class StaleWardrobeException : InvalidOperationException { }
    private sealed class Operation : IDisposable
    {
        private readonly AccountService _accounts;
        private readonly AccountWardrobeCapture _capture;
        private readonly CancellationTokenSource _stop;
        private bool _disposed;
        internal Operation(AccountService accounts, AccountWardrobeCapture capture, CancellationToken token)
        {
            _accounts = accounts; _capture = capture; _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _stop.CancelAfter(TimeSpan.FromSeconds(30));
            accounts.StateStore.Changed += OnChanged;
            try { Admit(); }
            catch { Dispose(); throw; }
        }
        internal CancellationToken Token => _stop.Token;
        internal void Admit()
        {
            Token.ThrowIfCancellationRequested();
            if (!_accounts.CaptureActiveWardrobe(_capture.Identity).IsSuccess) throw new StaleWardrobeException();
        }
        private void OnChanged(XsrStateChange change)
        {
            if (_disposed) return;
            if (change.SemanticId == AccountStateContract.ProfilesKey || change.SemanticId == AccountStateContract.SelectedKey)
                if (!_accounts.CaptureActiveWardrobe(_capture.Identity).IsSuccess)
                    try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _accounts.StateStore.Changed -= OnChanged; _stop.Dispose();
        }
    }
}
