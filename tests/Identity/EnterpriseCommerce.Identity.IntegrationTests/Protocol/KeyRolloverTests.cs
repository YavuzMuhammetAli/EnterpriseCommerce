using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseCommerce.Identity.Api.Protocol.Keys;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Identity.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Serilog.Core;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Identity with persisted certificate key material: two signing and two encryption certificates in a temporary
/// directory, and restarts on the same database with a changed key ring. Logs are captured at the most verbose level.
/// </summary>
public sealed class KeyRolloverFixture : IdentityServerFixture, IAsyncLifetime, IAsyncDisposable
{
    public TestKeyMaterial Keys { get; } = new();

    public (string Path, string KeyId) SigningA { get; }

    public (string Path, string KeyId) SigningB { get; }

    public (string Path, string KeyId) EncryptionA { get; }

    public (string Path, string KeyId) EncryptionB { get; }

    public LogCapturingIdentityServerFixture.CapturingSink Sink { get; } = new();

    public KeyRolloverFixture()
    {
        SigningA = Keys.Create("signing-a");
        SigningB = Keys.Create("signing-b");
        EncryptionA = Keys.Create("encryption-a");
        EncryptionB = Keys.Create("encryption-b");
    }

    protected override string MinimumLogLevel => "Verbose";

    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(Sink));

    /// <summary>Restarts Identity with exactly this key ring.</summary>
    internal Task UseRingAsync(
        (string Path, KeyStatus Status, DateTimeOffset? RetiredAt)[] signing,
        (string Path, KeyStatus Status, DateTimeOffset? RetiredAt)[] encryption) =>
        RestartAsync(builder =>
        {
            Configure(builder, "Signing", signing);
            Configure(builder, "Encryption", encryption);
        });

    /// <summary>Re-implements the interface so that the temporary key files are deleted after the host stopped.</summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Keys.Dispose();
    }

    private static void Configure(IWebHostBuilder builder, string use, (string Path, KeyStatus Status, DateTimeOffset? RetiredAt)[] entries)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var prefix = $"Identity:Keys:{use}:{index}:";
            builder.UseSetting(prefix + "CertificatePath", entries[index].Path);
            builder.UseSetting(prefix + "CertificatePassword", TestKeyMaterial.Password);
            builder.UseSetting(prefix + "Status", entries[index].Status.ToString());
            if (entries[index].RetiredAt is { } retiredAt)
            {
                builder.UseSetting(prefix + "RetiredAt", retiredAt.ToString("O"));
            }
        }
    }
}

/// <summary>
/// Signing and encryption key rollover (Phase 2.3 S6.3, PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.5, findings I31
/// and I34): explicit activation, pre-publication, old tokens accepted while the retired key is kept, rejection once it
/// is removed, unknown key IDs and tampered signatures, persisted keys across restarts, and no key material in logs.
/// </summary>
public sealed class KeyRolloverTests(KeyRolloverFixture identity) : IClassFixture<KeyRolloverFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset LongAgo = DateTimeOffset.UtcNow.AddDays(-10);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Every test leaves the fixture on its first ring, so tests do not depend on each other's order.
        if (!string.IsNullOrEmpty(identity.ConnectionString))
        {
            await UseFirstRingAsync();
        }
    }

    [Fact]
    public async Task TheActiveKeySigns_APendingKeyIsPublishedButNeverSigns_AndPersistedKeysSurviveARestart()
    {
        identity.RequireAvailable();
        await UseFirstRingAsync();
        var email = await identity.CreateUserAsync();

        var session = await identity.SignInAsync(identity.Storefront, email);

        Assert.Equal(HttpStatusCode.OK, session.Status);
        Assert.Equal(identity.SigningA.KeyId, new JsonWebToken(session.AccessToken).Kid);
        Assert.Equal(identity.SigningA.KeyId, new JsonWebToken(session.IdToken).Kid);
        Assert.Equal(new[] { identity.SigningA.KeyId, identity.SigningB.KeyId }.Order(StringComparer.Ordinal), (await JwksKeyIdsAsync()).Order(StringComparer.Ordinal));

        // A restart with the same persisted keys keeps every issued token valid (ephemeral keys would not).
        await UseFirstRingAsync();
        var refreshed = await identity.RefreshAsync(identity.Storefront, session.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(identity.SigningA.KeyId, new JsonWebToken(refreshed.AccessToken).Kid);
    }

    [Fact]
    public async Task Rollover_KeepsEarlierTokensValid_UntilTheRetiredKeyIsRemoved()
    {
        identity.RequireAvailable();

        // 1. Key A signs, key B is pre-published. Two users with sessions; a service caches the JWKS (A and B).
        await UseFirstRingAsync();
        var first = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());
        var second = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());
        await using var cachingService = await ResourceServer.StartAsync(identity.Factory.Server.CreateHandler(), "ec-storefront");
        Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(cachingService, first.AccessToken!));

        // 2. Activate B, retire A (signing and encryption). Restart.
        await identity.UseRingAsync(
            [(identity.SigningB.Path, KeyStatus.Active, null), (identity.SigningA.Path, KeyStatus.Retired, DateTimeOffset.UtcNow)],
            [(identity.EncryptionB.Path, KeyStatus.Active, null), (identity.EncryptionA.Path, KeyStatus.Retired, DateTimeOffset.UtcNow)]);

        var third = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());
        Assert.Equal(identity.SigningB.KeyId, new JsonWebToken(third.AccessToken).Kid);

        // The service validates B tokens from its cache: B was published before it signed (it cannot reach the old host).
        Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(cachingService, third.AccessToken!));

        // A fresh service accepts tokens of both keys during the rollover window.
        await using (var freshService = await ResourceServer.StartAsync(identity.Factory.Server.CreateHandler(), "ec-storefront"))
        {
            Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(freshService, first.AccessToken!));
            Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(freshService, third.AccessToken!));
        }

        // A refresh token from before the rollover (signed with A, encrypted with encryption key A) still redeems, and
        // its successor is signed with B.
        var refreshed = await identity.RefreshAsync(identity.Storefront, first.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(identity.SigningB.KeyId, new JsonWebToken(refreshed.AccessToken).Kid);

        // Identity's own bearer validation accepts the A-signed access token too.
        Assert.Equal(HttpStatusCode.NoContent, await identity.LogoutAllAsync(first.AccessToken));

        // 3. Remove signing key A (retired beyond its window), keep encryption key A. Restart.
        await identity.UseRingAsync(
            [(identity.SigningB.Path, KeyStatus.Active, null), (identity.SigningA.Path, KeyStatus.Retired, LongAgo)],
            [(identity.EncryptionB.Path, KeyStatus.Active, null), (identity.EncryptionA.Path, KeyStatus.Retired, DateTimeOffset.UtcNow)]);

        Assert.Equal([identity.SigningB.KeyId], await JwksKeyIdsAsync());
        await using (var laterService = await ResourceServer.StartAsync(identity.Factory.Server.CreateHandler(), "ec-storefront"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await ResourceServer.CallAsync(laterService, second.AccessToken!));
            Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(laterService, third.AccessToken!));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await identity.LogoutAllAsync(second.AccessToken));

        // Refresh tokens are signed with the server signing key as well (finding I31): although encryption key A is
        // still loaded, a refresh token signed with A no longer redeems once signing key A is gone.
        var orphaned = await identity.RefreshAsync(identity.Storefront, second.RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, orphaned.Status);
        Assert.Null(orphaned.AccessToken);

        Assert.Contains(identity.Sink.Events, entry => entry.Contains("past its retention window and was not loaded", StringComparison.Ordinal)
            && entry.Contains(identity.SigningA.KeyId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownKeyIdsAndTamperedSignatures_AreRejectedEverywhere()
    {
        identity.RequireAvailable();
        await UseFirstRingAsync();
        var genuine = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());
        var claims = new JsonWebToken(genuine.AccessToken);

        using var foreignKey = RSA.Create(3072);
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            TokenType = "at+jwt",
            Issuer = IdentityServerFixture.Issuer.AbsoluteUri,
            Audience = "ec-storefront",
            Subject = new ClaimsIdentity([new Claim("sub", claims.Subject), new Claim("ec_ptype", "user"), new Claim("client_id", "bff-storefront")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(foreignKey) { KeyId = Guid.NewGuid().ToString("N") }, SecurityAlgorithms.RsaSha256),
        });
        var parts = genuine.AccessToken!.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{(parts[2][0] == 'A' ? 'B' : 'A')}{parts[2][1..]}";

        await using var service = await ResourceServer.StartAsync(identity.Factory.Server.CreateHandler(), "ec-storefront");
        Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(service, genuine.AccessToken!));
        foreach (var token in new[] { forged, tampered })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await ResourceServer.CallAsync(service, token));
            Assert.Equal(HttpStatusCode.Unauthorized, await identity.LogoutAllAsync(token));
        }
    }

    [Fact]
    public async Task KeyMaterial_NeverReachesTheLogs()
    {
        identity.RequireAvailable();
        await UseFirstRingAsync();
        await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());

        // The key ring was reported (key IDs are public), so the capture did see the startup logs.
        Assert.Contains(identity.Sink.Events, entry => entry.Contains("Key ring:", StringComparison.Ordinal) && entry.Contains(identity.SigningA.KeyId, StringComparison.Ordinal));
        Assert.DoesNotContain(identity.Sink.Events, entry => entry.Contains(TestKeyMaterial.Password, StringComparison.Ordinal));
        Assert.DoesNotContain(identity.Sink.Events, entry => entry.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(identity.Sink.Events, entry => entry.Contains("\"d\":", StringComparison.Ordinal));
    }

    private Task UseFirstRingAsync() =>
        identity.UseRingAsync(
            [(identity.SigningA.Path, KeyStatus.Active, null), (identity.SigningB.Path, KeyStatus.Pending, null)],
            [(identity.EncryptionA.Path, KeyStatus.Active, null)]);

    private async Task<List<string>> JwksKeyIdsAsync()
    {
        using var client = identity.CreateBackChannel();
        using var jwks = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/jwks", UriKind.Relative), TestContext.Current.CancellationToken));
        var keys = jwks.RootElement.GetProperty("keys").EnumerateArray().ToList();

        // Only public RSA parameters are ever published.
        Assert.All(keys, key => Assert.False(key.TryGetProperty("d", out _)));
        return [.. keys.Select(key => key.GetProperty("kid").GetString()!)];
    }
}
