using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.Sessions;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// Logout-all on a real SQL Server: the security version is the authority, and OpenIddict bulk revocation is
/// post-commit cleanup whose failure never reverses the committed decision (ADR-017 sections 6 to 8).
/// </summary>
public sealed class LogoutAllSessionsTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    [Fact]
    public async Task LogoutAll_RaisesSecurityVersion_AndRevokesEveryAuthorizationAndToken()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var otherUserId = await IdentityTestServices.CreateUserAsync(services);
        var sessions = await CreateSessionsAsync(services, userId, count: 2);
        var otherSession = (await CreateSessionsAsync(services, otherUserId, count: 1)).Single();

        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LogoutAllSessionsHandler>()
            .HandleAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(new LogoutAllResult(2, CleanupCompleted: true), result);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
        foreach (var session in sessions)
        {
            Assert.Equal(Statuses.Revoked, await AuthorizationStatusAsync(services, session.AuthorizationId));
            Assert.Equal(Statuses.Revoked, await TokenStatusAsync(services, session.TokenId));
        }

        // Another user's sessions are untouched.
        Assert.Equal(1, await IdentityTestServices.ReadSecurityVersionAsync(services, otherUserId));
        Assert.Equal(Statuses.Valid, await AuthorizationStatusAsync(services, otherSession.AuthorizationId));
        Assert.Equal(Statuses.Valid, await TokenStatusAsync(services, otherSession.TokenId));
    }

    [Fact]
    public async Task LogoutAll_WhenCleanupFails_StillCommitsTheSecurityVersion()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(
            database,
            configure: s => s.AddSingleton<ISessionRevocationCleanup, FailingCleanup>());
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var session = (await CreateSessionsAsync(services, userId, count: 1)).Single();

        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<LogoutAllSessionsHandler>()
            .HandleAsync(userId, TestContext.Current.CancellationToken);

        Assert.False(result.CleanupCompleted);
        Assert.Equal(2, result.SecurityVersion);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));

        // OpenIddict state is stale, but the raised security version is what blocks the session.
        Assert.Equal(Statuses.Valid, await AuthorizationStatusAsync(services, session.AuthorizationId));
    }

    [Fact]
    public async Task LogoutAll_WithoutSecurityVersionRecord_FailsClosed()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);

        await using var scope = services.CreateAsyncScope();
        await Assert.ThrowsAsync<SecurityStateUnavailableException>(() =>
            scope.ServiceProvider.GetRequiredService<LogoutAllSessionsHandler>()
                .HandleAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cleanup_WhenTheDatabaseFails_ReportsFailureInsteadOfThrowing()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(
            database,
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:IdentityDatabase"] = "Server=tcp:127.0.0.1,1;Database=unreachable;Connect Timeout=2;TrustServerCertificate=true;Integrated Security=true",
            });
        using var metrics = new MetricCollector(IdentitySecurityMetrics.MeterName);

        var outcome = await services.GetRequiredService<ISessionRevocationCleanup>()
            .RevokeAllForSubjectAsync(Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        Assert.Equal(CleanupOutcome.Failed, outcome);
        Assert.True(metrics.Sum("identity.security.cleanup.failures", "revoke-subject") >= 1);
    }

    private static async Task<List<(string AuthorizationId, string TokenId)>> CreateSessionsAsync(IServiceProvider services, string subject, int count)
    {
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var ct = TestContext.Current.CancellationToken;

        var application = await applications.CreateAsync(
            new OpenIddictApplicationDescriptor { ClientId = $"test-{Guid.NewGuid():N}", ClientType = ClientTypes.Public },
            ct);
        var applicationId = await applications.GetIdAsync(application, ct);

        var sessions = new List<(string, string)>();
        for (var i = 0; i < count; i++)
        {
            var authorization = await authorizations.CreateAsync(
                new OpenIddictAuthorizationDescriptor
                {
                    ApplicationId = applicationId,
                    Subject = subject,
                    Status = Statuses.Valid,
                    Type = AuthorizationTypes.AdHoc,
                    CreationDate = DateTimeOffset.UtcNow,
                },
                ct);
            var authorizationId = (await authorizations.GetIdAsync(authorization, ct))!;

            var token = await tokens.CreateAsync(
                new OpenIddictTokenDescriptor
                {
                    ApplicationId = applicationId,
                    AuthorizationId = authorizationId,
                    Subject = subject,
                    Status = Statuses.Valid,
                    Type = TokenTypeHints.RefreshToken,
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = DateTimeOffset.UtcNow.AddHours(1),
                },
                ct);
            sessions.Add((authorizationId, (await tokens.GetIdAsync(token, ct))!));
        }

        return sessions;
    }

    private static async Task<string?> AuthorizationStatusAsync(IServiceProvider services, string authorizationId)
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await manager.FindByIdAsync(authorizationId, TestContext.Current.CancellationToken);
        return await manager.GetStatusAsync(authorization!, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> TokenStatusAsync(IServiceProvider services, string tokenId)
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var token = await manager.FindByIdAsync(tokenId, TestContext.Current.CancellationToken);
        return await manager.GetStatusAsync(token!, TestContext.Current.CancellationToken);
    }

    private sealed class FailingCleanup : ISessionRevocationCleanup
    {
        public Task<CleanupOutcome> RevokeAllForSubjectAsync(string subject, CancellationToken cancellationToken) =>
            Task.FromResult(CleanupOutcome.Failed);

        public Task<CleanupOutcome> RevokeAuthorizationTokensAsync(string authorizationId, CancellationToken cancellationToken) =>
            Task.FromResult(CleanupOutcome.Failed);
    }
}
