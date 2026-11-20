extern alias IdentityApi;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseCommerce.Bff.Protocol;
using EnterpriseCommerce.Bff.Sessions;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Serilog.Core;
using StackExchange.Redis;
using Yarp.ReverseProxy.Forwarder;
using IdentityProgram = IdentityApi::Program;

namespace EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

/// <summary>A front end registered at Identity: its client, key pair and public origin.</summary>
public sealed class FrontEnd(string deployment) : IDisposable
{
    public string Deployment { get; } = deployment;

    public string ClientId => $"bff-{Deployment}";

    public string Audience => $"ec-{Deployment}";

    public Uri Origin => new($"https://{Deployment}.example.test");

    public string KeyId { get; } = Guid.NewGuid().ToString("N");

    public RSA Key { get; } = RSA.Create(3072);

    public string PrivateKeyPem => Key.ExportPkcs8PrivateKeyPem();

    public string JsonWebKeySet
    {
        get
        {
            var parameters = Key.ExportParameters(includePrivateParameters: false);
            return JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } },
            });
        }
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>Options for one BFF host in a test.</summary>
public sealed class BffHostOptions
{
    public string? KeyPrefix { get; set; }

    public string? RedisConnection { get; set; }

    public string? PrivateKeyPem { get; set; }

    public string? PublicOrigin { get; set; }

    public string? Authority { get; set; }

    public HttpMessageHandler? AuthorityHandler { get; set; }

    public string? ClientId { get; set; }

    public TimeSpan? AbsoluteLifetime { get; set; }

    public TimeSpan? IdleTimeout { get; set; }

    public bool AllowPersistent { get; set; }

    public ILogEventSink? LogSink { get; set; }

    public string? DownstreamApiAddress { get; set; }

    public HttpMessageHandler? DownstreamHandler { get; set; }

    public Dictionary<string, string?> Settings { get; } = [];
}

/// <summary>One running BFF deployment instance.</summary>
public sealed class BffHost : IAsyncDisposable
{
    internal BffHost(WebApplicationFactory<Program> factory, FrontEnd frontEnd, Uri origin, string keyPrefix, MutableTimeProvider time, IdentityChannelControls channel)
    {
        Factory = factory;
        FrontEnd = frontEnd;
        Origin = origin;
        KeyPrefix = keyPrefix;
        Time = time;
        Channel = channel;
    }

    internal WebApplicationFactory<Program> Factory { get; }

    public FrontEnd FrontEnd { get; }

    public Uri Origin { get; }

    public string KeyPrefix { get; }

    public MutableTimeProvider Time { get; }

    public IdentityChannelControls Channel { get; }

    public IServiceProvider Services => Factory.Services;

    internal RedisSessionStore Store => Services.GetRequiredService<RedisSessionStore>();

    public string SessionCookieName => FrontEnd.Deployment switch { _ => $"__Host-Http-ec-{FrontEnd.Deployment}" };

    public string RequestTokenCookieName => $"__Host-ec-{FrontEnd.Deployment}-xsrf";

    public Uri Url(string pathAndQuery) => new(Origin, pathAndQuery);

    public HttpMessageHandler CreateHandler() => Factory.Server.CreateHandler();

    /// <summary>The session record ID of the browser's current session cookie.</summary>
    public string? RecordIdOf(TestBrowser browser) =>
        browser.Cookie(Origin, SessionCookieName) is { } key ? SessionIds.RecordId(key) : null;

    internal async Task<SessionTokenSnapshot?> TokensOfAsync(TestBrowser browser) =>
        RecordIdOf(browser) is { } recordId ? await Store.GetTokensAsync(recordId, CancellationToken.None) : null;

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>
/// The real Identity host (throwaway SQL Server database) with the three BFF clients registered, plus factories for
/// BFF hosts of each front end against the real session Redis. Every Redis key of a run lives under one prefix and is
/// deleted at the end; the database is dropped.
/// </summary>
public partial class BffTestEnvironment : IAsyncLifetime
{
    public const string Password = "Correct-Horse-Battery-9";
    public const string NewPassword = "Another-Correct-Horse-7";
    public static readonly Uri Issuer = new("https://id.example.test/");

    private readonly string _database = $"ec_bff_test_{Guid.NewGuid():N}";
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly List<BffHost> _hosts = [];
    private readonly string _dataProtectionRoot = Path.Combine(Path.GetTempPath(), "ec-bff-tests", Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<IdentityProgram>? _identity;

    public FrontEnd Storefront { get; } = new("storefront");

    public FrontEnd Merchant { get; } = new("merchant");

    public FrontEnd Admin { get; } = new("admin");

    public IEnumerable<FrontEnd> FrontEnds => [Storefront, Merchant, Admin];

    public ILogEventSink? IdentityLogSink { get; set; }

    internal WebApplicationFactory<IdentityProgram> Identity => _identity ?? throw new InvalidOperationException("The infrastructure is not configured.");

    public string ConnectionString => TestInfrastructure.SqlServerBuilder(_database).ConnectionString;

    public string RunPrefix => $"bfftest:{_runId}";

    public void RequireAvailable() => TestInfrastructure.RequireAvailable();

    public async ValueTask InitializeAsync()
    {
        if (!TestInfrastructure.IsConfigured)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<IdentityServiceDbContext>().UseSqlServer(ConnectionString).UseOpenIddict().Options;
        await using (var context = new IdentityServiceDbContext(options))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        _identity = new IdentityFactory(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:IdentityDatabase", ConnectionString);
            builder.UseSetting("Identity:Issuer", Issuer.AbsoluteUri);
            builder.UseSetting("Identity:AdminConsoleClientId", Admin.ClientId);
            builder.UseSetting("Identity:RateLimits:CredentialRequestsPerMinute", "100000");
            builder.UseSetting("Serilog:MinimumLevel:Default", IdentityLogSink is null ? "Warning" : "Verbose");
            var index = 0;
            foreach (var frontEnd in FrontEnds)
            {
                var prefix = $"Identity:Clients:{index++}:";
                builder.UseSetting(prefix + "ClientId", frontEnd.ClientId);
                builder.UseSetting(prefix + "Kind", "Browser");
                builder.UseSetting(prefix + "Audience", frontEnd.Audience);
                builder.UseSetting(prefix + "RedirectUris:0", new Uri(frontEnd.Origin, "/bff/callback").AbsoluteUri);
                builder.UseSetting(prefix + "PostLogoutRedirectUris:0", new Uri(frontEnd.Origin, "/bff/signed-out").AbsoluteUri);
                builder.UseSetting(prefix + "JsonWebKeySet", frontEnd.JsonWebKeySet);
            }

            if (IdentityLogSink is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton(IdentityLogSink));
            }
        });

        _ = _identity.Server;
    }

    /// <summary>Starts a BFF host for a front end. Hosts of the same front end share the key prefix and key ring.</summary>
    public BffHost CreateBff(FrontEnd frontEnd, Action<BffHostOptions>? configure = null)
    {
        var options = new BffHostOptions();
        configure?.Invoke(options);

        var time = new MutableTimeProvider();
        var channel = new IdentityChannelControls();
        var origin = new Uri(options.PublicOrigin ?? frontEnd.Origin.AbsoluteUri.TrimEnd('/'));
        var keyPrefix = options.KeyPrefix ?? $"{RunPrefix}:{frontEnd.Deployment}";
        var authorityHandler = options.AuthorityHandler;

        var factory = new BffFactory(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:BffSessions", options.RedisConnection ?? TestInfrastructure.RedisConnection());
            builder.UseSetting("Bff:Deployment", frontEnd.Deployment);
            builder.UseSetting("Bff:ClientId", options.ClientId ?? frontEnd.ClientId);
            builder.UseSetting("Bff:Audience", frontEnd.Audience);
            builder.UseSetting("Bff:Authority", options.Authority ?? Issuer.AbsoluteUri);
            builder.UseSetting("Bff:PublicOrigin", origin.AbsoluteUri.TrimEnd('/'));
            builder.UseSetting("Bff:ClientKey:PrivateKeyPem", options.PrivateKeyPem ?? frontEnd.PrivateKeyPem);
            builder.UseSetting("Bff:ClientKey:KeyId", frontEnd.KeyId);
            builder.UseSetting("Bff:RedisKeyPrefix", keyPrefix);
            builder.UseSetting("Bff:DataProtectionKeyDirectory", Path.Combine(_dataProtectionRoot, frontEnd.Deployment));
            builder.UseSetting("Bff:Session:AllowPersistent", options.AllowPersistent ? "true" : "false");
            if (options.AbsoluteLifetime is { } absolute)
            {
                builder.UseSetting("Bff:Session:AbsoluteLifetime", absolute.ToString("c"));
            }

            if (options.IdleTimeout is { } idle)
            {
                builder.UseSetting("Bff:Session:IdleTimeout", idle.ToString("c"));
            }

            if (options.DownstreamApiAddress is not null)
            {
                builder.UseSetting("Bff:DownstreamApiAddress", options.DownstreamApiAddress);
            }

            builder.UseSetting("Serilog:MinimumLevel:Default", options.LogSink is null ? "Warning" : "Verbose");
            foreach (var (key, value) in options.Settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddHttpClient(IdentityProtocolClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => authorityHandler is null ? Identity.Server.CreateHandler() : new NonDisposingHandler(authorityHandler))
                    .AddHttpMessageHandler(() => new IdentityChannelControls.Handler(channel));
                if (options.LogSink is not null)
                {
                    services.AddSingleton(options.LogSink);
                }

                if (options.DownstreamHandler is not null)
                {
                    services.AddSingleton<IForwarderHttpClientFactory>(new TestForwarderFactory(options.DownstreamHandler));
                }
            });
        });

        var host = new BffHost(factory, frontEnd, origin, keyPrefix, time, channel);
        _ = factory.Server;
        _hosts.Add(host);
        return host;
    }

    /// <summary>A browser that can reach Identity and the given BFF hosts.</summary>
    public TestBrowser CreateBrowser(params BffHost[] hosts)
    {
        var browser = new TestBrowser().Map(Issuer, Identity.Server.CreateHandler());
        foreach (var host in hosts)
        {
            browser.Map(host.Origin, host.CreateHandler());
        }

        return browser;
    }

    public async Task<string> CreateUserAsync()
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"user-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));

        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        context.SecurityVersions.Add(new SecurityVersionRecord { UserId = user.Id, Version = SecurityVersionPolicy.InitialVersion });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return email;
    }

    public async Task<string> UserIdAsync(string email)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
    }

    /// <summary>
    /// Signs in through the BFF like a browser: /bff/login, Identity's login page if there is no SSO session, the
    /// callback, and the landing page. Returns the last response.
    /// </summary>
    public async Task<BrowserResponse> SignInAsync(TestBrowser browser, BffHost bff, string email, string returnUrl = "/home", string extraQuery = "", Func<Uri, bool>? stopBefore = null)
    {
        var response = await browser.NavigateAsync(bff.Url($"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}{extraQuery}"), stopBefore);
        if (response.Url.AbsolutePath == "/account/login" && response.Status == HttpStatusCode.OK)
        {
            response = await SubmitLoginAsync(browser, response, email);
            if (TestBrowser.IsRedirect(response.Status) && response.Location is { } next && stopBefore?.Invoke(next) != true)
            {
                response = await browser.NavigateAsync(next, stopBefore);
            }
        }

        return response;
    }

    public static async Task<BrowserResponse> SubmitLoginAsync(TestBrowser browser, BrowserResponse loginPage, string email, string password = Password)
    {
        var token = WebUtility.HtmlDecode(AntiforgeryField().Match(loginPage.Body).Groups[1].Value);
        var returnUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(loginPage.Url.Query).TryGetValue("ReturnUrl", out var value) ? value.ToString() : string.Empty;
        var request = new HttpRequestMessage(HttpMethod.Post, loginPage.Url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = password,
                ["ReturnUrl"] = returnUrl,
                ["__RequestVerificationToken"] = token,
            }),
        };
        request.Headers.Add("Origin", Issuer.GetLeftPart(UriPartial.Authority));
        return await browser.SendAsync(request);
    }

    /// <summary>Changes the password on Identity's page with the browser's SSO session (ADR-017 section 7).</summary>
    public static async Task<BrowserResponse> ChangePasswordAsync(TestBrowser browser)
    {
        var page = await browser.GetAsync(new Uri(Issuer, "/account/change-password"));
        Assert.Equal(HttpStatusCode.OK, page.Status);
        var request = new HttpRequestMessage(HttpMethod.Post, page.Url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.CurrentPassword"] = Password,
                ["Input.NewPassword"] = NewPassword,
                ["Input.ConfirmPassword"] = NewPassword,
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(AntiforgeryField().Match(page.Body).Groups[1].Value),
            }),
        };
        request.Headers.Add("Origin", Issuer.GetLeftPart(UriPartial.Authority));
        return await browser.SendAsync(request);
    }

    /// <summary>Loads /bff/user (which issues the antiforgery token) and returns the readable request token.</summary>
    public static async Task<(BrowserResponse User, string? Xsrf)> UserAsync(TestBrowser browser, BffHost bff)
    {
        var user = await browser.FetchAsync(HttpMethod.Get, bff.Url("/bff/user"), xsrfToken: null);
        return (user, browser.Cookie(bff.Origin, bff.RequestTokenCookieName));
    }

    public static async Task<BrowserResponse> PostAsync(TestBrowser browser, BffHost bff, string path)
    {
        var (_, xsrf) = await UserAsync(browser, bff);
        return await browser.FetchAsync(HttpMethod.Post, bff.Url(path), xsrf);
    }

    /// <summary>The OpenIddict authorization statuses of a user.</summary>
    public async Task<List<string?>> AuthorizationStatusesAsync(string userId)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var statuses = new List<string?>();
        await foreach (var authorization in manager.FindBySubjectAsync(userId, TestContext.Current.CancellationToken))
        {
            statuses.Add(await manager.GetStatusAsync(authorization, TestContext.Current.CancellationToken));
        }

        return statuses;
    }

    public async Task<long> SecurityVersionAsync(string userId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand("SELECT [Version] FROM [SecurityVersions] WHERE [UserId] = @id", connection);
        command.Parameters.AddWithValue("@id", userId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Rows in Identity's refresh redemption ledger for the user's authorizations.</summary>
    public async Task<int> RedemptionsAsync(string userId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*) FROM [RefreshTokenRedemptions] r
            JOIN [OpenIddictAuthorizations] a ON a.[Id] = r.[AuthorizationId]
            WHERE a.[Subject] = @id
            """, connection);
        command.Parameters.AddWithValue("@id", userId);
        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static IDatabase Redis(out IConnectionMultiplexer connection)
    {
        connection = ConnectionMultiplexer.Connect(TestInfrastructure.RedisConnection());
        return connection.GetDatabase();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        if (_identity is not null)
        {
            await _identity.DisposeAsync();
            await DeleteRedisKeysAsync();
            await DropDatabaseAsync();
        }

        foreach (var frontEnd in FrontEnds)
        {
            frontEnd.Dispose();
        }

        try
        {
            Directory.Delete(_dataProtectionRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task DeleteRedisKeysAsync()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(TestInfrastructure.RedisConnection());
        var server = connection.GetServers()[0];
        var database = connection.GetDatabase();
        await foreach (var key in server.KeysAsync(pattern: RunPrefix + ":*", pageSize: 500))
        {
            await database.KeyDeleteAsync(key);
        }
    }

    private async Task DropDatabaseAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(TestInfrastructure.SqlServerBuilder("master").ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @name sysname = @databaseName;
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) =
                    N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
                EXEC (@sql);
            END
            """;
        command.Parameters.AddWithValue("@databaseName", _database);
        await command.ExecuteNonQueryAsync();
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    private sealed class IdentityFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<IdentityProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
    }

    private sealed class BffFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
    }

    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class TestForwarderFactory(HttpMessageHandler handler) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(handler, disposeHandler: false);
    }
}
