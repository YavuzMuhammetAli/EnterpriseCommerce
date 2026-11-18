using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>Captures every log event of the Identity host, at the most verbose application level.</summary>
public sealed class LogCapturingIdentityServerFixture : IdentityServerFixture
{
    public CapturingSink Sink { get; } = new();

    // Verbose for everything the shipped configuration does not explicitly cap (ADR-017 section 11 caps
    // Microsoft.AspNetCore, Yarp, EF Core and OpenIddict.Server). A leak at any uncapped level fails the test.
    protected override string MinimumLogLevel => "Verbose";

    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Sink));

    /// <summary>Serilog picks up sinks registered in DI (<c>ReadFrom.Services</c> in Program.cs).</summary>
    public sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _events = new();

        public IReadOnlyCollection<string> Events => _events;

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            var properties = string.Join(" ", logEvent.Properties.Select(property => $"{property.Key}={property.Value}"));
            _events.Enqueue($"{logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)} {properties} {logEvent.Exception}");
        }
    }
}

/// <summary>
/// No authorization code, token, PKCE verifier, client assertion, password or sensitive query string reaches the
/// logs during real flows, including failures and reuse detection (ADR-017 section 11, PoC-10).
/// </summary>
public sealed partial class SensitiveLogRedactionTests(LogCapturingIdentityServerFixture identity) : IClassFixture<LogCapturingIdentityServerFixture>
{
    [Fact]
    public async Task FullFlows_NeverLogSecrets()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        const string wrongPassword = "Never-Log-This-Password-42";
        var secrets = new List<string> { IdentityServerFixture.Password, wrongPassword };

        using (var browser = identity.CreateBrowser())
        {
            using var failed = await identity.SubmitLoginAsync(browser, email, wrongPassword);
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

            var (code, verifier) = await identity.AuthorizeAsync(browser, identity.Storefront, email);
            secrets.Add(code);
            secrets.Add(verifier);

            var tokens = await identity.ExchangeCodeAsync(identity.Storefront, code, verifier);
            Assert.Equal(HttpStatusCode.OK, tokens.Status);
            secrets.Add(tokens.AccessToken!);
            secrets.Add(tokens.RefreshToken!);

            // A normal refresh, a cross-client replay, refresh-token reuse and a code replay all log security events.
            var rotated = await identity.RefreshAsync(identity.Storefront, tokens.RefreshToken!);
            Assert.Equal(HttpStatusCode.OK, rotated.Status);
            secrets.Add(rotated.RefreshToken!);
            Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Merchant, rotated.RefreshToken!)).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, tokens.RefreshToken!)).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await identity.ExchangeCodeAsync(identity.Storefront, code, verifier)).Status);
        }

        var events = identity.Sink.Events.ToList();
        Assert.NotEmpty(events);

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(events, entry => entry.Contains(secret, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(events, entry => JwtLike().IsMatch(entry));
        Assert.DoesNotContain(events, entry => SensitiveParameter().IsMatch(entry));
    }

    /// <summary>Compact JWS/JWE serializations: access, ID and refresh tokens, codes and client assertions.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex JwtLike();

    [GeneratedRegex(@"(code_verifier|client_assertion|refresh_token|id_token_hint|code)=[^&\s]{8,}")]
    private static partial Regex SensitiveParameter();
}
