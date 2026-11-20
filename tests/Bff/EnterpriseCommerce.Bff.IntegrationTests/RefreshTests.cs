using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Bff.Protocol;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>
/// Server-side refresh with Identity's rotation and redemption ledger (ADR-016 section 4, ADR-017 section 5). Identity
/// issues 10-minute access tokens; moving the BFF clock 9 minutes puts a session inside the refresh threshold.
/// </summary>
public sealed class RefreshTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    private static readonly TimeSpan IntoThreshold = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan PastExpiry = TimeSpan.FromMinutes(11);

    [Fact]
    public async Task NearExpiry_TheBffRefreshesOnce_AndRotatesTheRefreshToken()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);
        var before = (await bff.TokensOfAsync(browser))!;

        // Not due yet: no refresh on ordinary requests.
        await BffTestEnvironment.UserAsync(browser, bff);
        Assert.Equal(0, bff.Channel.RefreshRequests);

        bff.Time.Advance(IntoThreshold);
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);

        var after = (await bff.TokensOfAsync(browser))!;
        Assert.Equal(1, bff.Channel.RefreshRequests);
        Assert.NotEqual(before.Tokens.RefreshToken, after.Tokens.RefreshToken);
        Assert.NotEqual(before.Tokens.AccessToken, after.Tokens.AccessToken);
        Assert.True(after.Version > before.Version);
        Assert.Equal(RefreshState.Usable, after.State);
        Assert.Equal(1, await environment.RedemptionsAsync(await environment.UserIdAsync(email)));
    }

    [Fact]
    public async Task ConcurrentRequests_RedeemTheRefreshTokenExactlyOnce()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);
        bff.Channel.RefreshDelay = TimeSpan.FromMilliseconds(400);
        bff.Time.Advance(IntoThreshold);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => BffTestEnvironment.UserAsync(browser, bff)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.User.Status));
        Assert.Equal(1, bff.Channel.RefreshRequests);
        Assert.Equal(1, await environment.RedemptionsAsync(await environment.UserIdAsync(email)));
        Assert.Equal(RefreshState.Usable, (await bff.TokensOfAsync(browser))!.State);
    }

    [Fact]
    public async Task ConcurrentRequestsAcrossTwoInstances_RedeemTheRefreshTokenExactlyOnce()
    {
        environment.RequireAvailable();
        var first = environment.CreateBff(environment.Storefront);
        var second = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(first);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, first, email);
        var cookie = $"{first.SessionCookieName}={browser.Cookie(first.Origin, first.SessionCookieName)}";

        foreach (var instance in new[] { first, second })
        {
            instance.Channel.RefreshDelay = TimeSpan.FromMilliseconds(400);
            instance.Time.Advance(IntoThreshold);
        }

        using var a = new HttpMessageInvoker(first.CreateHandler());
        using var b = new HttpMessageInvoker(second.CreateHandler());
        var statuses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, first.Url("/bff/user"));
            request.Headers.Add("Cookie", cookie);
            using var response = await (i % 2 == 0 ? a : b).SendAsync(request, TestContext.Current.CancellationToken);
            return response.StatusCode;
        }));

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(1, first.Channel.RefreshRequests + second.Channel.RefreshRequests);
        Assert.Equal(1, await environment.RedemptionsAsync(await environment.UserIdAsync(email)));
    }

    [Theory]
    [InlineData(RefreshFault.LoseResponse)]
    [InlineData(RefreshFault.ServiceUnavailable)]
    public async Task AnUnknownRefreshOutcome_IsNeverRetried_AndTheSessionEndsWithItsAccessToken(RefreshFault fault)
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);
        var original = (await bff.TokensOfAsync(browser))!.Tokens;
        var recordId = bff.RecordIdOf(browser)!;

        bff.Channel.Fault = fault;
        bff.Time.Advance(IntoThreshold);

        // The access token is still valid: the user keeps working, but the refresh token is never presented again.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
        }

        var snapshot = (await bff.TokensOfAsync(browser))!;
        Assert.Equal(RefreshState.Uncertain, snapshot.State);
        Assert.Equal(original.AccessToken, snapshot.Tokens.AccessToken);
        Assert.Equal(1, bff.Channel.RefreshRequests);

        // Once the access token has expired, the session ends instead of risking a replay.
        bff.Channel.Fault = RefreshFault.None;
        bff.Time.Advance(PastExpiry - IntoThreshold);
        var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);
        Assert.Equal(HttpStatusCode.Unauthorized, user.Status);
        Assert.Equal(1, bff.Channel.RefreshRequests);
        Assert.Null(await bff.Store.GetTokensAsync(recordId, TestContext.Current.CancellationToken));
        Assert.True(await environment.RedemptionsAsync(await environment.UserIdAsync(email)) <= 1);
    }

    [Fact]
    public async Task ARefreshThatWasProvablyNotSent_IsRetriedLater_WithTheSameToken()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);

        bff.Channel.Fault = RefreshFault.RefuseConnection;
        bff.Time.Advance(IntoThreshold);
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
        Assert.Equal(RefreshState.Usable, (await bff.TokensOfAsync(browser))!.State);

        bff.Channel.Fault = RefreshFault.None;
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
        Assert.Equal(2, bff.Channel.RefreshRequests);
        Assert.Equal(1, await environment.RedemptionsAsync(await environment.UserIdAsync(email)));
    }

    [Fact]
    public async Task ARevokedAuthorization_EndsTheSessionAtTheNextRefresh()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());
        var recordId = bff.RecordIdOf(browser)!;
        var tokens = (await bff.TokensOfAsync(browser))!.Tokens;

        // Another party revokes the authorization (for example an administrator, or the reuse policy).
        var identity = bff.Services.GetRequiredService<IdentityProtocolClient>();
        Assert.Equal(CallOutcome.Succeeded, await identity.RevokeAsync(tokens.RefreshToken!, TestContext.Current.CancellationToken));

        bff.Time.Advance(IntoThreshold);
        var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);

        Assert.Equal(HttpStatusCode.Unauthorized, user.Status);
        Assert.Contains("login=\"/bff/login\"", user.Header("WWW-Authenticate"), StringComparison.Ordinal);
        Assert.Null(await bff.Store.GetTokensAsync(recordId, TestContext.Current.CancellationToken));
    }
}
