using System.Net;
using Nexa.Services.Accounts;
using Nexa.Services.Minecraft.Launch;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ThirdPartyLaunchPreservesIdentityAndRejectsStaleSessions()
    {
        foreach (string behavior in new[] { "valid", "refresh", "wrong-character", "replace", "cancel", "persist-failure" })
        {
            var port = new ThrowingProfilePort();
            var accounts = CreateAccountService(port);
            var profile = SampleProfile("Alice", "old-access") with
            {
                Kind = LaunchProfileKind.ThirdParty,
                AuthServer = "https://auth.example/api/yggdrasil",
                Uuid = "0123456789abcdef0123456789abcdef",
                ClientToken = "client"
            };
            AssertTrue(accounts.AddProfile(profile).IsSuccess);
            using var stop = new CancellationTokenSource();
            var handler = new ThirdPartyLaunchHandler(behavior, profile.Uuid, () =>
            {
                if (behavior == "replace") AssertTrue(accounts.ReplaceProfile(0, profile with { AccessToken = "newer" }).IsSuccess);
                if (behavior == "cancel") stop.Cancel();
                if (behavior == "persist-failure") port.SaveShouldThrow = true;
            });
            using var http = new HttpClient(handler);
            var resolver = new AccountLaunchIdentityResolver(accounts, yggdrasil: new YggdrasilAuthService(http));
            try
            {
                var result = await resolver.ResolveAsync(0, profile, stop.Token);
                AssertEqual(behavior is "valid" or "refresh", result.IsSuccess);
                if (result.IsSuccess)
                {
                    AssertEqual(MinecraftLaunchIdentityMode.ThirdParty, result.Value!.Mode);
                    AssertEqual(profile.AuthServer, result.Value.AuthServer);
                    AssertEqual(behavior == "valid" ? "old-access" : "rotated-access", accounts.GetProfile(0).Value!.AccessToken);
                }
                else AssertEqual(behavior == "replace" ? "newer" : "old-access", accounts.GetProfile(0).Value!.AccessToken);
            }
            catch (OperationCanceledException) when (behavior == "cancel") { AssertEqual("old-access", accounts.GetProfile(0).Value!.AccessToken); }
            AssertTrue(handler.Paths.All(path => path.StartsWith("https://auth.example/api/yggdrasil/authserver/", StringComparison.Ordinal)));
        }
    }

    private sealed class ThirdPartyLaunchHandler(string behavior, string uuid, Action beforeRefresh) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsolutePath.EndsWith("validate", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(behavior == "valid" ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
            beforeRefresh();
            string selected = behavior == "wrong-character" ? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" : uuid;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accessToken\":\"rotated-access\",\"clientToken\":\"rotated-client\",\"selectedProfile\":{\"id\":\"" + selected + "\",\"name\":\"AliceRenamed\"}}")
            });
        }
    }
}
