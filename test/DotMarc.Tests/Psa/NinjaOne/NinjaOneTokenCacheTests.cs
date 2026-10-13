using System.Net;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

public sealed class NinjaOneTokenCacheTests
{
    private const string TokenJson = """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1","token_type":"Bearer"}""";

    private static NinjaOneSettings Settings() => new() { Instance = "eu.ninjarmm.com", ClientId = "client-1", ClientSecretConfigured = true, RefreshTokenConfigured = true };

    private static FakeSecretStore Secrets(string refreshToken = "refresh-1") =>
        new() { Secrets = { [NinjaOneSettings.ClientSecretKey] = "secret", [NinjaOneSettings.RefreshTokenKey] = refreshToken } };

    [Fact]
    public async Task Refreshes_WithTheRefreshTokenGrant_AtTheInstancesTokenEndpoint()
    {
        var handler = new FakeHttpMessageHandler { ResponseBody = TokenJson };
        var cache = new NinjaOneTokenCache(Secrets(), TimeProvider.System);

        var token = await cache.GetTokenAsync(new HttpClient(handler), Settings(), forceRefresh: false, CancellationToken.None);

        Assert.Equal("access-1", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://eu.ninjarmm.com/ws/oauth/token", request.RequestUri!.ToString());
        var form = handler.RequestBodies.Single();
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("client_id=client-1", form);
        Assert.Contains("client_secret=secret", form);
        Assert.Contains("refresh_token=refresh-1", form);
    }

    [Fact]
    public async Task ReusesTheAccessToken_UntilAMinuteBeforeItExpires_UnlessForced()
    {
        var handler = new FakeHttpMessageHandler { ResponseBody = TokenJson };
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var cache = new NinjaOneTokenCache(Secrets(), clock);
        var httpClient = new HttpClient(handler);

        await cache.GetTokenAsync(httpClient, Settings(), false, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(58));
        await cache.GetTokenAsync(httpClient, Settings(), false, CancellationToken.None);
        Assert.Single(handler.Requests);

        await cache.GetTokenAsync(httpClient, Settings(), forceRefresh: true, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(59.5));
        await cache.GetTokenAsync(httpClient, Settings(), false, CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ARotatedRefreshToken_IsSaved()
    {
        var handler = new FakeHttpMessageHandler { ResponseBody = TokenJson.Replace("refresh-1", "refresh-2") };
        var secrets = Secrets();

        await new NinjaOneTokenCache(secrets, TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None);

        Assert.Equal("refresh-2", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", "invalid_grant")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"Bad client"}""", "invalid_client - Bad client")]
    public async Task ARefusal_MeansTheConnectionIsLost_AndSaysWhy(HttpStatusCode status, string body, string reason)
    {
        var handler = new FakeHttpMessageHandler { StatusCode = status, ResponseBody = body };

        var exception = await Assert.ThrowsAsync<NinjaOneConnectionLostException>(() =>
            new NinjaOneTokenCache(Secrets(), TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None));

        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    public async Task AServerError_IsTransient_NotALostConnection()
    {
        var handler = new FakeHttpMessageHandler { StatusCode = HttpStatusCode.BadGateway, ResponseBody = "" };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new NinjaOneTokenCache(Secrets(), TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None));
    }

    [Theory]
    [InlineData(NinjaOneSettings.ClearedRefreshToken)]
    [InlineData("")]
    public async Task WithNoSavedSignIn_TheConnectionIsLost_WithoutCallingNinjaOne(string refreshToken)
    {
        var handler = new FakeHttpMessageHandler();

        await Assert.ThrowsAsync<NinjaOneConnectionLostException>(() =>
            new NinjaOneTokenCache(Secrets(refreshToken), TimeProvider.System).GetTokenAsync(new HttpClient(handler), Settings(), false, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheAuthorizeUrl_AsksForTheCodeAndOfflineAccess()
    {
        var url = NinjaOneOAuth.AuthorizeUrl(Settings(), "https://dotmarc.example/psa/ninjaone/callback", "state-1");

        Assert.Equal("https://eu.ninjarmm.com/ws/oauth/authorize?response_type=code&client_id=client-1"
            + "&redirect_uri=https%3A%2F%2Fdotmarc.example%2Fpsa%2Fninjaone%2Fcallback&scope=monitoring%20management%20offline_access&state=state-1", url);
    }
}
