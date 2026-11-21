using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>An Identity account as the stub knows it.</summary>
public sealed record StubAccount(string Email, bool EmailConfirmed = true, bool Active = true);

/// <summary>
/// A stand-in for Identity's token endpoint and internal email verification endpoint (IDENTITY_API_CONTRACT.md
/// section 2.13), for the Tenant &amp; Merchant tests. It checks that the client authenticates with a signed
/// <c>private_key_jwt</c> assertion for the verification scope, and that every verification call carries the issued
/// token. The real Identity endpoint is tested in Identity's own suite and end to end through the BFF.
/// </summary>
public sealed class StubIdentity : HttpMessageHandler
{
    public static readonly Uri BaseAddress = new("https://identity.test/");

    private readonly ConcurrentDictionary<string, StubAccount> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _issued = new();
    private int _verifications;
    private int _tokens;

    /// <summary>When set, the verification endpoint answers this status without a verdict.</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>When set, the verification endpoint answers 401 this many times (a revoked or expired token).</summary>
    public int RejectTokens { get; set; }

    public int VerificationCalls => _verifications;

    public int TokenRequests => _tokens;

    /// <summary>Registers (or replaces) the account of a subject.</summary>
    public void SetAccount(string subject, StubAccount account) => _accounts[subject] = account;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/connect/token")
        {
            return await TokenAsync(request, cancellationToken);
        }

        const string prefix = "/internal/v1/accounts/";
        const string suffix = "/email-verification";
        if (request.Method != HttpMethod.Post || !path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        Interlocked.Increment(ref _verifications);
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token } || !_issued.Contains(token))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (RejectTokens > 0)
        {
            RejectTokens--;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (FailWith is { } failure)
        {
            return new HttpResponseMessage(failure);
        }

        var subject = Uri.UnescapeDataString(path[prefix.Length..^suffix.Length]);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var email = body.RootElement.GetProperty("email").GetString()!;
        if (!_accounts.TryGetValue(subject, out var account))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var result = !account.Active ? "Inactive"
            : !account.EmailConfirmed ? "Unverified"
            : string.Equals(account.Email.ToUpperInvariant(), email.Trim().ToUpperInvariant(), StringComparison.Ordinal) ? "Match"
            : "Mismatch";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { accountId = subject, result }) };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tokens);
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);

        var assertion = new JsonWebToken(form["client_assertion"]);
        var valid = form["grant_type"] == "client_credentials"
            && form["scope"] == "svc.identity.account-verification"
            && form["client_id"] == "svc-tenant-merchant"
            && form["client_assertion_type"] == "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
            && assertion.Typ == "client-authentication+jwt"
            && assertion.Alg == "RS256"
            && assertion.Issuer == "svc-tenant-merchant"
            && assertion.Subject == "svc-tenant-merchant"
            && assertion.Audiences.Contains(TestTokenIssuer.Issuer);
        if (!valid)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var token = "stub-service-token-" + Guid.NewGuid().ToString("N");
        _issued.Enqueue(token);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = token, token_type = "Bearer", expires_in = 300 }),
        };
    }
}
