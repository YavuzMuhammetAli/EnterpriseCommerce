using System.Diagnostics;
using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Bff.Sessions;
using StackExchange.Redis;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>Server-side sessions in the real session Redis (ADR-016 section 3).</summary>
public sealed class SessionTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task TheSessionRecord_IsInRedis_UnderTheDeploymentPrefix_WithProtectedTokens()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());

        var cookieKey = browser.Cookie(bff.Origin, bff.SessionCookieName)!;
        var recordId = SessionIds.RecordId(cookieKey);
        var tokens = (await bff.TokensOfAsync(browser))!.Tokens;

        var redis = BffTestEnvironment.Redis(out var connection);
        await using var _ = connection;
        var key = $"{bff.KeyPrefix}:s:{recordId}";
        var fields = (await redis.HashGetAllAsync(key)).ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString());

        Assert.Equal(environment.Storefront.ClientId, fields["client"]);
        Assert.Equal("usable", fields["state"]);
        Assert.All(new[] { tokens.AccessToken, tokens.RefreshToken!, tokens.IdToken! }, secret =>
            Assert.DoesNotContain(fields.Values, value => value.Contains(secret, StringComparison.Ordinal)));

        // Neither the cookie key nor the record exposes the other: Redis stores only the hash of the key.
        Assert.DoesNotContain(cookieKey, key, StringComparison.Ordinal);
        var ttl = await redis.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(1), TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task APlantedSessionId_IsNeverPromoted_AndEveryLoginRotatesTheId()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        var planted = SessionIds.NewKey();
        browser.SetCookie(bff.Origin, bff.SessionCookieName, planted);

        await environment.SignInAsync(browser, bff, email);
        var first = browser.Cookie(bff.Origin, bff.SessionCookieName);
        Assert.NotEqual(planted, first);

        await environment.SignInAsync(browser, bff, email);
        var second = browser.Cookie(bff.Origin, bff.SessionCookieName);
        Assert.NotEqual(first, second);

        // The previous session is deleted, not kept alongside the new one.
        var redis = BffTestEnvironment.Redis(out var connection);
        await using var _ = connection;
        Assert.False(await redis.KeyExistsAsync($"{bff.KeyPrefix}:s:{SessionIds.RecordId(first!)}"));
        Assert.False(await redis.KeyExistsAsync($"{bff.KeyPrefix}:s:{SessionIds.RecordId(planted)}"));
        Assert.True(await redis.KeyExistsAsync($"{bff.KeyPrefix}:s:{SessionIds.RecordId(second!)}"));
    }

    [Fact]
    public async Task TheThreeFrontEnds_HaveIsolatedSessions()
    {
        environment.RequireAvailable();
        var storefront = environment.CreateBff(environment.Storefront);
        var merchant = environment.CreateBff(environment.Merchant);
        var admin = environment.CreateBff(environment.Admin);
        using var browser = environment.CreateBrowser(storefront, merchant, admin);
        var email = await environment.CreateUserAsync();

        foreach (var bff in new[] { storefront, merchant, admin })
        {
            await environment.SignInAsync(browser, bff, email);
            var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);
            Assert.Equal(HttpStatusCode.OK, user.Status);
        }

        var keys = new[] { storefront, merchant, admin }.Select(bff => browser.Cookie(bff.Origin, bff.SessionCookieName)).ToList();
        Assert.Equal(3, keys.Distinct().Count());

        // A session ID of one front end is not a session at another, even when presented under that one's cookie name.
        using var stolen = environment.CreateBrowser(merchant, admin);
        stolen.SetCookie(merchant.Origin, merchant.SessionCookieName, keys[0]!);
        stolen.SetCookie(admin.Origin, admin.SessionCookieName, keys[1]!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(stolen, merchant)).User.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(stolen, admin)).User.Status);
    }

    [Fact]
    public async Task ARecordOfAnotherClient_IsRejected_EvenUnderASharedPrefix()
    {
        environment.RequireAvailable();
        var storefront = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(storefront);
        await environment.SignInAsync(browser, storefront, await environment.CreateUserAsync());

        // A misconfigured merchant deployment pointed at the storefront's key namespace.
        var misconfigured = environment.CreateBff(environment.Merchant, options => options.KeyPrefix = storefront.KeyPrefix);
        using var other = environment.CreateBrowser(misconfigured);
        other.SetCookie(misconfigured.Origin, misconfigured.SessionCookieName, browser.Cookie(storefront.Origin, storefront.SessionCookieName)!);

        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(other, misconfigured)).User.Status);
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, storefront)).User.Status);
    }

    [Fact]
    public async Task AnIdleSession_Expires()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront, options => options.IdleTimeout = TimeSpan.FromSeconds(2));
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);

        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);
        Assert.Equal(HttpStatusCode.Unauthorized, user.Status);
        Assert.Contains("BffSession", user.Header("WWW-Authenticate"), StringComparison.Ordinal);
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
    }

    [Fact]
    public async Task ActivityNeverExtendsASessionPastItsAbsoluteLifetime()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront, options =>
        {
            options.AbsoluteLifetime = TimeSpan.FromSeconds(5);
            options.IdleTimeout = TimeSpan.FromSeconds(3);
        });
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());

        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(4))
        {
            Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
    }

    [Fact]
    public async Task KeepMeSignedIn_IsAPersistentCookieBoundedByTheAbsoluteLifetime_OnlyWhereAllowed()
    {
        environment.RequireAvailable();
        var storefront = environment.CreateBff(environment.Storefront, options => options.AllowPersistent = true);
        var admin = environment.CreateBff(environment.Admin);
        using var browser = environment.CreateBrowser(storefront, admin);
        var email = await environment.CreateUserAsync();

        await environment.SignInAsync(browser, storefront, email, extraQuery: "&persistent=1");
        var persistent = browser.Transcript.Last(r => r.Url.AbsolutePath == "/bff/callback").SetCookie(storefront.SessionCookieName)!;
        Assert.Contains("expires=", persistent.ToLowerInvariant(), StringComparison.Ordinal);

        await environment.SignInAsync(browser, admin, email, extraQuery: "&persistent=1");
        var sessionOnly = browser.Transcript.Last(r => r.Url.AbsolutePath == "/bff/callback").SetCookie(admin.SessionCookieName)!;
        Assert.DoesNotContain("expires=", sessionOnly.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableSessionStore_FailsClosedWith503_AndTheSessionSurvivesTheOutage()
    {
        environment.RequireAvailable();
        var (host, port) = TestInfrastructure.RedisEndpoint();
        await using var forwarder = new TcpForwarder(host, port);
        var bff = environment.CreateBff(environment.Storefront, options => options.RedisConnection = TestInfrastructure.RedisConnection(forwarder.Port));
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());
        var cookie = browser.Cookie(bff.Origin, bff.SessionCookieName);

        forwarder.Cut();
        var (user, _) = await BffTestEnvironment.UserAsync(browser, bff);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, user.Status);
        Assert.NotNull(user.Header("Retry-After"));
        Assert.Null(user.SetCookie(bff.SessionCookieName));
        Assert.Equal(cookie, browser.Cookie(bff.Origin, bff.SessionCookieName));

        // A login attempt cannot create a session either.
        using var other = environment.CreateBrowser(bff);
        var login = await other.GetAsync(bff.Url("/bff/login"));
        Assert.Null(other.Cookie(bff.Origin, bff.SessionCookieName));
        Assert.True(TestBrowser.IsRedirect(login.Status) || login.Status == HttpStatusCode.ServiceUnavailable);

        forwarder.Restore();
        var deadline = Stopwatch.StartNew();
        HttpStatusCode status;
        do
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            status = (await BffTestEnvironment.UserAsync(browser, bff)).User.Status;
        }
        while (status != HttpStatusCode.OK && deadline.Elapsed < TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>
    /// A real Redis process restart: needs control of the container, so it runs only when
    /// <c>EC_TEST_REDIS_RESTART_CONTAINER</c> names the session Redis container (for example
    /// <c>enterprisecommerce-redis-sessions-1</c>). Sessions survive through append-only persistence.
    /// </summary>
    [Fact]
    public async Task Sessions_SurviveASessionRedisRestart()
    {
        environment.RequireAvailable();
        var container = Environment.GetEnvironmentVariable("EC_TEST_REDIS_RESTART_CONTAINER");
        if (string.IsNullOrWhiteSpace(container))
        {
            Assert.Skip("Set EC_TEST_REDIS_RESTART_CONTAINER to the session Redis container name to run the restart test.");
        }

        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());

        // appendfsync everysec: let the write reach the append-only file before the restart.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        using (var restart = Process.Start(new ProcessStartInfo("docker", ["restart", container]) { RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await restart.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, restart.ExitCode);
        }

        var deadline = Stopwatch.StartNew();
        HttpStatusCode status;
        do
        {
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            status = (await BffTestEnvironment.UserAsync(browser, bff)).User.Status;
        }
        while (status != HttpStatusCode.OK && deadline.Elapsed < TimeSpan.FromSeconds(60));

        Assert.Equal(HttpStatusCode.OK, status);
    }
}

/// <summary>
/// The startup guard against an evicting session store. Runs alone (it changes the server's policy for a moment), and
/// restores <c>noeviction</c> afterwards.
/// </summary>
[Collection(nameof(SessionStorePolicyCollection))]
public sealed class SessionStorePolicyTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task TheBff_RefusesToStart_UnlessTheSessionRedisUsesNoEviction()
    {
        environment.RequireAvailable();
        await using var connection = await ConnectionMultiplexer.ConnectAsync(TestInfrastructure.RedisConnection() + ",allowAdmin=true");
        var server = connection.GetServers()[0];
        Assert.Equal("noeviction", (await server.ConfigGetAsync("maxmemory-policy")).Single().Value);

        await server.ConfigSetAsync("maxmemory-policy", "allkeys-lru");
        try
        {
            var failure = Assert.ThrowsAny<Exception>(() => environment.CreateBff(environment.Storefront));
            Assert.Contains("noeviction", failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await server.ConfigSetAsync("maxmemory-policy", "noeviction");
        }

        Assert.NotNull(environment.CreateBff(environment.Storefront));
    }

    [Fact]
    public void TheBff_RefusesToStart_WithoutAReachableSessionRedis()
    {
        environment.RequireAvailable();

        var failure = Assert.ThrowsAny<Exception>(() => environment.CreateBff(environment.Storefront, options =>
            options.RedisConnection = "127.0.0.1:1,connectTimeout=500,syncTimeout=500,asyncTimeout=500,abortConnect=false"));

        Assert.Contains("session Redis", failure.ToString(), StringComparison.Ordinal);
    }
}

[CollectionDefinition(nameof(SessionStorePolicyCollection), DisableParallelization = true)]
public sealed class SessionStorePolicyCollection;
