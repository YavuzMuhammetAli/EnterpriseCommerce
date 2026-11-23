using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

/// <summary>
/// A stand-in for Identity's token endpoint and Tenant &amp; Merchant's internal membership verification endpoint
/// (TENANT_MERCHANT_API_CONTRACT.md section 3.3), for the Catalog tests. It checks that Catalog authenticates as
/// <c>svc-catalog</c> with a <c>private_key_jwt</c> assertion signed by its key, for the verification scope, and that
/// every verification carries an issued token. The real endpoint is tested in Tenant &amp; Merchant's own suite.
/// </summary>
public sealed class StubTenantMerchant(RSA clientKey) : HttpMessageHandler
{
    public static readonly Uri BaseAddress = new("https://tenant-merchant.test/");
    public static readonly Uri TokenEndpoint = new("https://identity.test/connect/token");

    private readonly ConcurrentDictionary<(Guid TenantId, string Subject), string> _members = new();
    private readonly ConcurrentQueue<string> _issued = new();
    private int _verifications;
    private int _tokens;

    /// <summary>When set, the verification endpoint answers this status without a verdict.</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>When set, the verification endpoint answers this body with 200 instead of a verdict.</summary>
    public string? RawAnswer { get; set; }

    /// <summary>When set, the verification endpoint answers 401 this many times (a revoked or expired token).</summary>
    public int RejectTokens { get; set; }

    /// <summary>When set, the token endpoint answers 400 (for example an unregistered client).</summary>
    public bool RefuseTokens { get; set; }

    /// <summary>When set, the verification endpoint waits this long (a hanging dependency).</summary>
    public TimeSpan? Delay { get; set; }

    public int VerificationCalls => _verifications;

    public int TokenRequests => _tokens;

    /// <summary>The correlation ID of the last verification request.</summary>
    public string? LastCorrelationId { get; private set; }

    /// <summary>Makes <paramref name="subject"/> an active member of the tenant with the role.</summary>
    public void SetMember(Guid tenantId, string subject, string role) => _members[(tenantId, subject)] = role;

    /// <summary>Removes the membership: the next verification is negative.</summary>
    public void RemoveMember(Guid tenantId, string subject) => _members.TryRemove((tenantId, subject), out _);

    /// <summary>Resets every failure mode.</summary>
    public void Heal()
    {
        FailWith = null;
        RawAnswer = null;
        RejectTokens = 0;
        RefuseTokens = false;
        Delay = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == TokenEndpoint)
        {
            return await TokenAsync(request, cancellationToken);
        }

        var path = request.RequestUri!.AbsolutePath;
        const string prefix = "/internal/v1/tenants/";
        const string suffix = "/membership-verifications";
        if (request.RequestUri.Host != BaseAddress.Host
            || request.Method != HttpMethod.Post
            || !path.StartsWith(prefix, StringComparison.Ordinal)
            || !path.EndsWith(suffix, StringComparison.Ordinal)
            || !Guid.TryParse(path[prefix.Length..^suffix.Length], out var tenantId))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        Interlocked.Increment(ref _verifications);
        LastCorrelationId = request.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.Single() : null;
        if (Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

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

        if (RawAnswer is { } raw)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json") };
        }

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var subject = body.RootElement.GetProperty("subjectId").GetString()!;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = _members.TryGetValue((tenantId, subject), out var role)
                ? JsonContent.Create(new { active = true, role })
                : JsonContent.Create(new { active = false, role = (string?)null }),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tokens);
        if (RefuseTokens)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);

        var signature = await new JsonWebTokenHandler().ValidateTokenAsync(form["client_assertion"], new TokenValidationParameters
        {
            ValidIssuer = "svc-catalog",
            ValidAudience = TestTokenIssuer.Issuer,
            ValidTypes = ["client-authentication+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = new RsaSecurityKey(clientKey.ExportParameters(includePrivateParameters: false)),
        });
        var valid = signature.IsValid
            && form["grant_type"] == "client_credentials"
            && form["scope"] == "svc.tenant-merchant.membership-verification"
            && form["client_id"] == "svc-catalog"
            && form["client_assertion_type"] == "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
            && ((JsonWebToken)signature.SecurityToken).Subject == "svc-catalog";
        if (!valid)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var issued = "stub-service-token-" + Guid.NewGuid().ToString("N");
        _issued.Enqueue(issued);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = issued, token_type = "Bearer", expires_in = 300 }),
        };
    }
}
