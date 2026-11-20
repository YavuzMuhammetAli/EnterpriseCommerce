using System.Net;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>Retention never weakens replay protection of the real token endpoint (Phase 2.3 S6).</summary>
public sealed class RetentionProtocolTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    [Fact]
    public async Task TheHostWindow_CoversTheLongestConfiguredSessionLifetime()
    {
        identity.RequireAvailable();

        // The fixture's browser clients use the default 12-hour absolute session; the default margin is 1 day.
        var options = identity.Services.GetRequiredService<IOptions<IdentityRetentionOptions>>().Value;
        Assert.Equal(TimeSpan.FromHours(36), options.ReplayProtectionWindow);
    }

    [Fact]
    public async Task AfterACleanupRun_ARedeemedRefreshTokenIsStillDetectedAsReuse()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var session = await identity.SignInAsync(identity.Storefront, email);
        var rotated = await identity.RefreshAsync(identity.Storefront, session.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);

        var run = await identity.Services.GetRequiredService<IdentityRetentionRunner>().RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RetentionRunOutcome.Completed, run.Outcome);

        // The replayed token is refused, and the reuse policy still revokes the session (ADR-017 section 5).
        var replay = await identity.RefreshAsync(identity.Storefront, session.RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, replay.Status);
        Assert.Equal("invalid_grant", replay.Error);
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, rotated.RefreshToken!)).Status);
        Assert.All(await identity.AuthorizationStatusesAsync(userId), s => Assert.Equal(OpenIddictConstants.Statuses.Revoked, s));
    }
}
