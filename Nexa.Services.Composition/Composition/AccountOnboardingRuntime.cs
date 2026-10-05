using Nexa.Services.Accounts;
using Nexa.Services.Foundation;
using Nexa.Services.Logging;
using Nexa.Xsr.Runtime;

namespace Nexa.Services.Composition;

public sealed class AccountOnboardingRuntime(AccountOnboardingService service, XsrCommandRouter commands, HttpClient? ownedClient = null, AccountSkinService? skins = null, IAccountLaunchIdentityResolver? launchIdentityResolver = null, XsrQueryRouter? queries = null) : IDisposable
{
    public AccountOnboardingService Service { get; } = service;
    public XsrCommandRouter Commands { get; } = commands;
    public AccountSkinService? Skins { get; } = skins;
    public XsrQueryRouter? Queries { get; } = queries;

    /// <summary>
    /// The launch identity resolver wired to this runtime's own Microsoft auth capability, so
    /// the Minecraft runtime can refresh Microsoft sessions without recomposing them.
    /// </summary>
    public IAccountLaunchIdentityResolver? LaunchIdentityResolver { get; } = launchIdentityResolver;
    public void Dispose() { Service.Dispose(); Skins?.Dispose(); ownedClient?.Dispose(); }
}

public static class AccountOnboardingRuntimeComposer
{
    public static AccountOnboardingRuntime Compose(FoundationHost host, HttpClient? client = null,
        AccountOnboardingOptions? options = null, LegacyProfileImport? imports = null,
        IMicrosoftMinecraftAuthService? microsoft = null, ILittleSkinOAuthService? littleSkin = null,
        IXsrDispatchObserver? observer = null)
    {
        HttpClient http = client ?? host.CreateHttpClient(allowAutoRedirect: false);
        if (client is null) http.Timeout = TimeSpan.FromSeconds(30);
        // One instance, two consumers: the onboarding service and the launch resolver MUST
        // share this capability, or production silently loses Microsoft refresh.
        IMicrosoftMinecraftAuthService microsoftService =
            microsoft ?? new MicrosoftMinecraftAuthService(http, log: host.Logging);
        AccountOnboardingOptions resolvedOptions = options ?? AccountOnboardingOptions.FromEnvironment();
        ILittleSkinOAuthService littleSkinService = littleSkin ?? new LittleSkinOAuthService(http, host.Logging);
        YggdrasilAuthService yggdrasil = new(http, host.Logging);
        AccountOnboardingService service = new(host.Accounts, microsoftService,
            littleSkinService, yggdrasil,
            resolvedOptions, imports, host.Logging);
        XsrCommandRouterBuilder commands = new();
        AccountSkinService skins = new(host.Accounts, http, host.Logging);
        commands.Register<AccountRefreshSkinsCommand>(AccountSkinService.RefreshRoute, (_, _) => ValueTask.FromResult(skins.Refresh()));
        commands.Register<AccountLoginStartCommand>(AccountOnboardingRoutes.Start, (command, _) => ValueTask.FromResult(service.Start(command)));
        commands.Register<AccountLoginCancelCommand>(AccountOnboardingRoutes.Cancel, (command, _) => ValueTask.FromResult(service.Cancel(command.Generation)));
        commands.Register<AccountChooseCharacterCommand>(AccountOnboardingRoutes.ChooseCharacter, (command, _) => ValueTask.FromResult(service.ChooseCharacter(command.Generation, command.Uuid)));
        commands.Register<AccountImportCommand>(AccountOnboardingRoutes.Import, (command, _) => ValueTask.FromResult(service.Import(command)));
        commands.Register<AccountDiscoverImportsCommand>(AccountOnboardingRoutes.DiscoverImports,
            async (_, cancellation) => await Task.Run(service.DiscoverImports, cancellation).ConfigureAwait(false));
        AccountWardrobeService wardrobe = new(host.Accounts, http, skins, microsoftService,
            resolvedOptions.MicrosoftClientId, littleSkinService, resolvedOptions.LittleSkin, host.Logging);
        commands.Register<AccountWardrobeUploadSkinCommand>(AccountWardrobeContract.UploadSkin, wardrobe.UploadSkinAsync);
        commands.Register<AccountWardrobeSetCapeCommand>(AccountWardrobeContract.SetCape, wardrobe.SetCapeAsync);
        XsrQueryRouterBuilder queries = new();
        queries.Register<AccountWardrobeQuery, AccountWardrobeSnapshot>(AccountWardrobeContract.Read, wardrobe.ReadAsync);
        queries.Register<AccountWardrobeSkinQuery, AccountWardrobeSkinPreview>(AccountWardrobeContract.ValidateSkin, wardrobe.ValidateSkinAsync);
        IAccountLaunchIdentityResolver resolver = new AccountLaunchIdentityResolver(
            host.Accounts,
            microsoftService,
            resolvedOptions.MicrosoftClientId,
            host.Logging, littleSkinService, resolvedOptions.LittleSkin, yggdrasil);
        return new(service, commands.Build(observer ?? new Observer()), client is null ? http : null, skins, resolver,
            queries.Build(observer ?? new Observer()));
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
}
