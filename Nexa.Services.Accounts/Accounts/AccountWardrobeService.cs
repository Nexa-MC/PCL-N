using System.Globalization;
using Nexa.Services.Logging;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Accounts;

internal sealed record AccountWardrobeCapture(AccountWardrobeIdentity Identity, LaunchProfile Profile,
    LaunchProfileView View);

/// <summary>Provider orchestration for the selected wardrobe. Credentials stay inside Accounts.</summary>
public sealed partial class AccountWardrobeService(AccountService accounts, HttpClient http, AccountSkinService skins,
    IMicrosoftMinecraftAuthService? microsoft = null, string? microsoftClientId = null,
    ILittleSkinOAuthService? littleSkin = null, LittleSkinOAuthConfiguration? littleSkinConfiguration = null,
    LogService? log = null)
{
    private readonly MinecraftSkinService _skin = new(http);
    private readonly MinecraftCapeService _cape = new(http);
    private readonly WardrobeTextureResolver _textures = new(http);
    private readonly WardrobeHistoryStore _history = new();
    private readonly WardrobeCatalogService _catalog = new(http);
    private readonly INCloudWardrobePort? _cloud;
    private readonly object _cardsGate = new();
    private AccountWardrobeIdentity? _projectedIdentity;
    private AccountWardrobeResolvedTextures? _projectedCurrent;
    private IReadOnlyDictionary<string, AccountWardrobeCard> _projectedCards = new Dictionary<string, AccountWardrobeCard>();

    public AccountWardrobeService(AccountService accounts, HttpClient http, AccountSkinService skins,
        WardrobeHistoryStore history, INCloudWardrobePort? cloud = null,
        IMicrosoftMinecraftAuthService? microsoft = null, string? microsoftClientId = null,
        ILittleSkinOAuthService? littleSkin = null, LittleSkinOAuthConfiguration? littleSkinConfiguration = null,
        LogService? log = null, WardrobeCatalogService? catalog = null)
        : this(accounts, http, skins, microsoft, microsoftClientId, littleSkin, littleSkinConfiguration, log)
    {
        _history = history; _cloud = cloud; _catalog = catalog ?? new(http);
    }

    internal static XsrError StaleIdentity() => new(XsrErrorKind.Rejected,
        XsrSemanticId.Parse("accounts.wardrobe.stale_identity"), "当前账户已变化，请刷新更衣橱后重试。");
    private static XsrError Failed(string message) => new(XsrErrorKind.Rejected,
        XsrSemanticId.Parse("accounts.wardrobe.rejected"), message);
    private string? Unavailable(AccountWardrobeCapture capture) => capture.Profile.Kind switch
    {
        LaunchProfileKind.Microsoft => null,
        LaunchProfileKind.LittleSkin => littleSkin is null ? "LittleSkin 外观接口不可用，请检查账户配置。" : null,
        LaunchProfileKind.Offline => "离线账户不支持在线更换皮肤或披风。",
        LaunchProfileKind.NCloud => _cloud?.CanManage(capture.View) == true ? null : "N Cloud 外观服务尚未连接，请先登录并启用提供方。",
        LaunchProfileKind.ThirdParty => "第三方皮肤站需要独立授权，请前往对应皮肤站管理角色材质。",
        _ => "此认证提供方未提供可用的皮肤和披风管理接口。"
    };

    public async ValueTask<XsrResult<AccountWardrobeSnapshot>> ReadAsync(AccountWardrobeQuery query,
        CancellationToken cancellationToken = default)
    {
        var capture = accounts.CaptureActiveWardrobe();
        if (!capture.IsSuccess) return XsrResult.Failure<AccountWardrobeSnapshot>(capture.Error!);
        try
        {
            var current = capture.Value;
            LittleSkinRetry littleSkinRetry = new();
            string? providerStatus = null;
            var capeState = Unavailable(current) is null && current.Profile.Kind is LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin
                ? AccountWardrobeCapeState.Loaded : AccountWardrobeCapeState.Unsupported;
            if (Unavailable(current) is null && current.Profile.Kind != LaunchProfileKind.NCloud)
            {
                try { current = await Authenticate(current, cancellationToken, littleSkinRetry).ConfigureAwait(false); }
                catch (Exception error) when (Recoverable(error) && error is not StaleWardrobeException
                    && (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
                {
                    // A failed credential or ownership lookup must retain the safe current preview and history.
                    current = Require(littleSkinRetry.Capture?.Identity ?? current.Identity);
                    providerStatus = "无法读取账户外观，请检查网络和账户授权后刷新。";
                    if (current.Profile.Kind != LaunchProfileKind.Microsoft)
                        capeState = AccountWardrobeCapeState.LoadFailed;
                }
            }
            IReadOnlyList<AccountWardrobeCape> capes = [];
            IReadOnlyList<LittleSkinClosetItem> closetSkins = [];
            if (capeState == AccountWardrobeCapeState.Loaded && current.Profile.Kind == LaunchProfileKind.Microsoft)
            {
                var owned = await ReadMicrosoftOwnedCapesAsync(current, cancellationToken).ConfigureAwait(false);
                current = owned.Capture; capes = owned.Capes; capeState = owned.State;
                providerStatus = owned.Status ?? providerStatus;
            }
            if (capeState == AccountWardrobeCapeState.Loaded && current.Profile.Kind == LaunchProfileKind.LittleSkin)
            {
                try
                {
                    var inventory = await RetryLittleSkinAsync(current, littleSkinRetry, async (admitted, inventoryOperation) =>
                    {
                        var owned = await ReadCapes(admitted.Profile, inventoryOperation.Token).ConfigureAwait(false);
                        inventoryOperation.Admit();
                        var ownedSkins = await littleSkin!.GetClosetItemsAsync(admitted.Profile.ProviderAccessToken,
                            LittleSkinTextureKind.Skin, inventoryOperation.Token).ConfigureAwait(false);
                        return (Capes: owned, Skins: ownedSkins);
                    }, cancellationToken).ConfigureAwait(false);
                    current = inventory.Capture; capes = inventory.Value.Capes; closetSkins = inventory.Value.Skins;
                }
                catch (Exception error) when (Recoverable(error) && error is not OperationCanceledException and not StaleWardrobeException)
                {
                    current = Require(littleSkinRetry.Capture?.Identity ?? current.Identity);
                    capes = []; closetSkins = [];
                    capeState = AccountWardrobeCapeState.LoadFailed;
                    providerStatus = "无法读取账户衣柜，请检查网络和账户授权后刷新。";
                }
            }
            using var operation = new Operation(accounts, current, cancellationToken);
            var snapshot = await ProjectAsync(current, capes, closetSkins, capeState, providerStatus, operation).ConfigureAwait(false);
            operation.Admit();
            lock (_cardsGate)
            {
                _projectedIdentity = current.Identity;
                _projectedCurrent = snapshot.Current;
                _projectedCards = snapshot.Skins.Concat(snapshot.CapeCards).ToDictionary(card => card.Id, StringComparer.Ordinal);
            }
            return XsrResult.Success(snapshot);
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
            DemandProvider(capture);
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
            DemandProvider(capture);
            LittleSkinRetry littleSkinRetry = new();
            if (command.FileName is { Length: > 256 }) throw new InvalidDataException("皮肤文件名过长。");
            // Copy the command payload before validation; caller mutation cannot alter transmitted bytes.
            if (command.PngBytes is null || command.PngBytes.Length > 1_048_576)
                throw new InvalidDataException("皮肤文件超过大小限制。");
            var image = WardrobeSkinValidator.Validate(command.PngBytes.ToArray(), command.IsSlim);
            using (var before = new Operation(accounts, capture, cancellationToken))
            { await RememberAsync(capture, before.Token).ConfigureAwait(false); before.Admit(); }
            if (capture.Profile.Kind != LaunchProfileKind.NCloud)
                capture = await Authenticate(capture, cancellationToken, littleSkinRetry).ConfigureAwait(false);
            string? address;
            using (var operation = new Operation(accounts, capture, cancellationToken))
            {
                if (capture.Profile.Kind == LaunchProfileKind.Microsoft)
                {
                    var result = await _skin.UploadAsync(capture.Profile.AccessToken, image.Bytes.ToArray(),
                        command.FileName, command.IsSlim, operation.Token).ConfigureAwait(false);
                    address = result.SkinAddress;
                }
                else if (capture.Profile.Kind == LaunchProfileKind.LittleSkin)
                {
                    operation.Dispose();
                    var uploaded = await RetryLittleSkinAsync(capture, littleSkinRetry, async (admitted, uploadOperation) =>
                    {
                        await littleSkin!.UploadMinecraftTextureAsync(admitted.Profile.AccessToken, admitted.Profile.Uuid,
                            image.Bytes.ToArray(), command.FileName, command.IsSlim, uploadOperation.Token).ConfigureAwait(false);
                        return true;
                    }, cancellationToken, refreshGameSession: true).ConfigureAwait(false);
                    capture = uploaded.Capture;
                    address = "uuid:" + capture.Profile.Uuid;
                }
                else
                {
                    var result = await _cloud!.UploadSkinAsync(capture.View, image.Bytes.ToArray(), command.IsSlim,
                        operation.Token).ConfigureAwait(false);
                    address = CloudAddress(result);
                }
                if (capture.Profile.Kind != LaunchProfileKind.LittleSkin) operation.Admit();
            }
            var saved = accounts.CommitWardrobeSkin(capture.Identity, address, cancellationToken);
            if (!saved.IsSuccess) return saved;
            await RememberCommittedSkinAsync(capture, address, command.IsSlim).ConfigureAwait(false);
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
            DemandProvider(capture);
            LittleSkinRetry littleSkinRetry = new();
            if (capture.Profile.Kind is not (LaunchProfileKind.Microsoft or LaunchProfileKind.LittleSkin))
                throw new InvalidDataException("当前账户不支持在启动器内更换披风。");
            var oldAppearance = ProjectedAppearance(capture.Identity);
            string? nextCape = command.CapeId is null ? null : ProjectedCapeAddress(capture.Identity, command.CapeId);
            using (var before = new Operation(accounts, capture, cancellationToken))
            { await RememberAsync(capture, before.Token).ConfigureAwait(false); before.Admit(); }
            capture = await Authenticate(capture, cancellationToken, littleSkinRetry).ConfigureAwait(false);
            using var operation = new Operation(accounts, capture, cancellationToken);
            if (capture.Profile.Kind == LaunchProfileKind.Microsoft)
            {
                if (command.CapeId is null) await _cape.ClearActiveCapeAsync(capture.Profile.AccessToken, operation.Token).ConfigureAwait(false);
                else await _cape.SetActiveCapeAsync(capture.Profile.AccessToken, command.CapeId, operation.Token).ConfigureAwait(false);
            }
            else
            {
                operation.Dispose();
                var changed = await RetryLittleSkinAsync(capture, littleSkinRetry, async (admitted, capeOperation) =>
                {
                    var capes = await ReadCapes(admitted.Profile, capeOperation.Token).ConfigureAwait(false);
                    capeOperation.Admit();
                    long id = 0;
                    if (command.CapeId is not null && (!capes.Any(c => c.Id == command.CapeId)
                        || !long.TryParse(command.CapeId, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0))
                        throw new InvalidDataException("所选披风不属于当前 LittleSkin 衣柜。");
                    var player = await ReadPlayer(admitted.Profile, capeOperation.Token).ConfigureAwait(false);
                    capeOperation.Admit();
                    await littleSkin!.ApplyTextureAsync(admitted.Profile.ProviderAccessToken, player.PlayerId, id,
                        LittleSkinTextureKind.Cape, capeOperation.Token).ConfigureAwait(false);
                    capeOperation.Admit();
                    await VerifyLittleSkinAsync(admitted.Profile, player.PlayerId, id, LittleSkinTextureKind.Cape,
                        capeOperation).ConfigureAwait(false);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
                capture = changed.Capture;
            }
            if (capture.Profile.Kind != LaunchProfileKind.LittleSkin) operation.Admit();
            using var completion = new Operation(accounts, capture, cancellationToken);
            if (oldAppearance is not null)
                await RememberAppearanceAsync(capture.View, oldAppearance with { CapeAddress = nextCape, Cape = null },
                    completion.Token).ConfigureAwait(false);
            completion.Admit();
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
    private void DemandProvider(AccountWardrobeCapture capture)
    {
        var profile = capture.Profile;
        if (Unavailable(capture) is { } reason) throw new InvalidDataException(reason);
        if (profile.Kind == LaunchProfileKind.LittleSkin && littleSkin is null)
            throw new InvalidDataException("LittleSkin 外观接口不可用，请检查账户配置。");
    }
    private async Task<AccountWardrobeCapture> Authenticate(AccountWardrobeCapture capture, CancellationToken token,
        LittleSkinRetry? littleSkinRetry = null)
    {
        DemandProvider(capture);
        if (capture.Profile.Kind == LaunchProfileKind.LittleSkin)
            return await AuthenticateLittleSkinAsync(capture, littleSkinRetry ?? new(), token).ConfigureAwait(false);
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
    private sealed class LittleSkinRetry
    {
        internal bool Used { get; set; }
        internal AccountWardrobeCapture? Capture { get; set; }
    }
    private async Task<AccountWardrobeCapture> RefreshLittleSkinProviderAsync(AccountWardrobeCapture capture,
        LittleSkinRetry retry, CancellationToken token)
    {
        if (littleSkinConfiguration is null || string.IsNullOrWhiteSpace(capture.Profile.RefreshToken))
            throw new InvalidDataException("LittleSkin 外观授权已过期，请重新登录。");
        using var operation = new Operation(accounts, capture, token);
        var result = await littleSkin!.RefreshOAuthTokenAsync(littleSkinConfiguration, capture.Profile.RefreshToken,
            operation.Token).ConfigureAwait(false);
        operation.Admit();
        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.RefreshToken))
            throw new InvalidDataException("账户缺少有效授权，请重新登录。");
        operation.Dispose();
        var saved = accounts.CommitWardrobeProfile(capture.Identity, capture.Profile with
        {
            ProviderAccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            ProviderTokenExpiresAtUnix = result.ExpiresAt.ToUnixTimeSeconds()
        }, token);
        if (!saved.IsSuccess) throw new StaleWardrobeException();
        // Persist rotating provider credentials before a subsequent game-session or inventory request can fail.
        retry.Capture = saved.Value;
        return saved.Value;
    }
    private async Task<AccountWardrobeCapture> AuthenticateLittleSkinAsync(AccountWardrobeCapture capture,
        LittleSkinRetry retry, CancellationToken token)
    {
        retry.Capture = capture;
        if (string.IsNullOrWhiteSpace(capture.Profile.ProviderAccessToken)
            || capture.Profile.ProviderTokenExpiresAtUnix <= DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds())
            capture = await RefreshLittleSkinProviderAsync(capture, retry, token).ConfigureAwait(false);
        var authenticated = await RetryLittleSkinAsync(capture, retry, async (admitted, operation) =>
        {
            var session = await littleSkin!.CreateMinecraftSessionAsync(admitted.Profile.ProviderAccessToken,
                admitted.Profile.Uuid, operation.Token).ConfigureAwait(false);
            operation.Admit();
            if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.Username)
                || !string.Equals(session.Uuid.Replace("-", ""), admitted.Profile.Uuid.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("LittleSkin 返回了其他角色或无效会话，请重新登录。");
            return session;
        }, token).ConfigureAwait(false);
        capture = authenticated.Capture;
        var saved = accounts.CommitWardrobeProfile(capture.Identity, capture.Profile with
        {
            Username = authenticated.Value.Username,
            AccessToken = authenticated.Value.AccessToken,
            ClientToken = authenticated.Value.ClientToken
        }, token);
        if (!saved.IsSuccess) throw new StaleWardrobeException();
        retry.Capture = saved.Value;
        return saved.Value;
    }
    private async Task<(AccountWardrobeCapture Capture, T Value)> RetryLittleSkinAsync<T>(AccountWardrobeCapture capture,
        LittleSkinRetry retry, Func<AccountWardrobeCapture, Operation, Task<T>> request, CancellationToken token,
        bool refreshGameSession = false)
    {
        retry.Capture = capture;
        while (true)
        {
            try
            {
                using var operation = new Operation(accounts, capture, token);
                T value = await request(capture, operation).ConfigureAwait(false);
                operation.Admit();
                return (capture, value);
            }
            catch (HttpRequestException error) when (!retry.Used
                && error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                retry.Used = true;
                capture = await RefreshLittleSkinProviderAsync(capture, retry, token).ConfigureAwait(false);
                if (refreshGameSession)
                    capture = await AuthenticateLittleSkinAsync(capture, retry, token).ConfigureAwait(false);
            }
        }
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
        return Array.AsReadOnly(closet.Where(c => c.TextureId > 0).DistinctBy(c => c.TextureId).Select(c => new AccountWardrobeCape(
            c.TextureId.ToString(CultureInfo.InvariantCulture), c.Name, c.TextureAddress, c.TextureId == player.CapeTextureId)).ToArray());
    }
    private async Task<(AccountWardrobeCapture Capture, IReadOnlyList<AccountWardrobeCape> Capes,
        AccountWardrobeCapeState State, string? Status)> ReadMicrosoftOwnedCapesAsync(AccountWardrobeCapture capture,
        CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var operation = new Operation(accounts, capture, token);
                var capes = await ReadCapes(capture.Profile, operation.Token).ConfigureAwait(false);
                operation.Admit();
                return (capture, capes, AccountWardrobeCapeState.Loaded, null);
            }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && attempt == 0 && microsoft is not null && !string.IsNullOrWhiteSpace(microsoftClientId)
                && !string.IsNullOrWhiteSpace(capture.Profile.RefreshToken))
            {
                string previousToken = capture.Profile.AccessToken;
                try { capture = await Authenticate(capture, token).ConfigureAwait(false); }
                catch (Exception refreshError) when (Recoverable(refreshError) && refreshError is not StaleWardrobeException
                    && (refreshError is not OperationCanceledException || !token.IsCancellationRequested))
                { break; }
                if (string.Equals(previousToken, capture.Profile.AccessToken, StringComparison.Ordinal)) break;
            }
            catch (Exception error) when (Recoverable(error) && error is not StaleWardrobeException
                && (error is not OperationCanceledException || !token.IsCancellationRequested))
            { break; }
        }
        Require(capture.Identity);
        token.ThrowIfCancellationRequested();
        return (capture, [], AccountWardrobeCapeState.LoadFailed, "无法读取账户衣柜，请检查网络和账户授权后刷新。");
    }
    private async Task<LittleSkinPlayer> ReadPlayer(LaunchProfile profile, CancellationToken token)
    {
        var players = await littleSkin!.GetPlayersAsync(profile.ProviderAccessToken, token).ConfigureAwait(false);
        var matching = players.Where(p => string.Equals(p.Username, profile.Username, StringComparison.Ordinal)).Take(2).ToArray();
        if (matching.Length != 1) throw new InvalidDataException("无法唯一识别当前 LittleSkin 角色，请重新登录。");
        return matching[0];
    }
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
