using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

/// <summary>What the stub answers for one dependency: a failure, a raw body, or a delay instead of a verdict.</summary>
public sealed class StubBehaviour
{
    /// <summary>When set, the endpoint answers this status without a verdict.</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>When set, the endpoint answers this body with 200 instead of a verdict.</summary>
    public string? RawAnswer { get; set; }

    /// <summary>When set, the endpoint answers 401 this many times (a revoked or expired token).</summary>
    public int RejectTokens { get; set; }

    /// <summary>When set, the endpoint waits this long (a hanging dependency).</summary>
    public TimeSpan? Delay { get; set; }

    internal int Calls;

    public int CallCount => Calls;

    public string? LastCorrelationId { get; internal set; }

    public void Heal()
    {
        FailWith = null;
        RawAnswer = null;
        RejectTokens = 0;
        Delay = null;
    }
}

/// <summary>
/// A stand-in for Identity's token endpoint, Tenant &amp; Merchant's internal membership verification
/// (TENANT_MERCHANT_API_CONTRACT.md section 3.3) and Catalog's internal variant verification (CATALOG_API_CONTRACT.md
/// section 6a), for the Inventory tests. It checks that Inventory authenticates as <c>svc-inventory</c> with a
/// <c>private_key_jwt</c> assertion signed by its key, for the scope of the API it calls, and that every verification
/// carries a token issued for that scope. The real endpoints are tested in their services' own suites.
/// </summary>
public sealed class StubDependencies(RSA clientKey) : HttpMessageHandler
{
    public const string MembershipScope = "svc.tenant-merchant.membership-verification";
    public const string CatalogScope = "svc.catalog.variant-verification";

    public static readonly Uri TenantMerchantAddress = new("https://tenant-merchant.test/");
    public static readonly Uri CatalogAddress = new("https://catalog.test/");
    public static readonly Uri TokenEndpoint = new("https://identity.test/connect/token");

    private readonly ConcurrentDictionary<(Guid TenantId, string Subject), string> _members = new();
    private readonly ConcurrentDictionary<(Guid TenantId, Guid ProductId, Guid VariantId), (string Status, bool Active, string Sku)> _variants = new();
    private readonly ConcurrentDictionary<string, string> _issued = new(StringComparer.Ordinal);
    private int _tokens;

    public StubBehaviour TenantMerchant { get; } = new();

    public StubBehaviour Catalog { get; } = new();

    /// <summary>When set, the token endpoint answers 400 (for example an unregistered client).</summary>
    public bool RefuseTokens { get; set; }

    public int TokenRequests => _tokens;

    /// <summary>Makes <paramref name="subject"/> an active member of the tenant with the role.</summary>
    public void SetMember(Guid tenantId, string subject, string role) => _members[(tenantId, subject)] = role;

    /// <summary>Removes the membership: the next verification is negative.</summary>
    public void RemoveMember(Guid tenantId, string subject) => _members.TryRemove((tenantId, subject), out _);

    /// <summary>Makes a variant known to the stub Catalog as a variant of the tenant's product.</summary>
    public void SetVariant(Guid tenantId, Guid productId, Guid variantId, string status = "Published", string sku = "SKU-1", bool active = true) =>
        _variants[(tenantId, productId, variantId)] = (status, active, sku);

    public void Heal()
    {
        TenantMerchant.Heal();
        Catalog.Heal();
        RefuseTokens = false;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri == TokenEndpoint)
        {
            return await TokenAsync(request, cancellationToken);
        }

        var uri = request.RequestUri!;
        if (uri.Host == TenantMerchantAddress.Host && TryTenant(uri.AbsolutePath, "/internal/v1/tenants/", "/membership-verifications", out var tenantId))
        {
            return await AnswerAsync(request, TenantMerchant, MembershipScope, body =>
            {
                var subject = body.RootElement.GetProperty("subjectId").GetString()!;
                return _members.TryGetValue((tenantId, subject), out var role)
                    ? JsonContent.Create(new { active = true, role })
                    : JsonContent.Create(new { active = false, role = (string?)null });
            }, cancellationToken);
        }

        if (uri.Host == CatalogAddress.Host && TryTenant(uri.AbsolutePath, "/internal/v1/catalog/tenants/", "/variant-verifications", out var catalogTenant))
        {
            return await AnswerAsync(request, Catalog, CatalogScope, body =>
            {
                var productId = body.RootElement.GetProperty("productId").GetGuid();
                var variantId = body.RootElement.GetProperty("variantId").GetGuid();
                return _variants.TryGetValue((catalogTenant, productId, variantId), out var variant)
                    ? JsonContent.Create(new { found = true, productStatus = variant.Status, variantActive = variant.Active, sku = variant.Sku })
                    : JsonContent.Create(new { found = false, productStatus = (string?)null, variantActive = (bool?)null, sku = (string?)null });
            }, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static bool TryTenant(string path, string prefix, string suffix, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal)
            && path.Length > prefix.Length + suffix.Length && Guid.TryParse(path[prefix.Length..^suffix.Length], out tenantId);
    }

    private async Task<HttpResponseMessage> AnswerAsync(
        HttpRequestMessage request,
        StubBehaviour behaviour,
        string scope,
        Func<JsonDocument, HttpContent> verdict,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        Interlocked.Increment(ref behaviour.Calls);
        behaviour.LastCorrelationId = request.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.Single() : null;
        if (behaviour.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token } || !_issued.TryGetValue(token, out var tokenScope) || tokenScope != scope)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (behaviour.RejectTokens > 0)
        {
            behaviour.RejectTokens--;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (behaviour.FailWith is { } failure)
        {
            return new HttpResponseMessage(failure);
        }

        if (behaviour.RawAnswer is { } raw)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/json") };
        }

        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = verdict(body) };
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
            ValidIssuer = "svc-inventory",
            ValidAudience = TestTokenIssuer.Issuer,
            ValidTypes = ["client-authentication+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKey = new RsaSecurityKey(clientKey.ExportParameters(includePrivateParameters: false)),
        });
        var scope = form.GetValueOrDefault("scope");
        var valid = signature.IsValid
            && form["grant_type"] == "client_credentials"
            && scope is MembershipScope or CatalogScope
            && form["client_id"] == "svc-inventory"
            && form["client_assertion_type"] == "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
            && ((JsonWebToken)signature.SecurityToken).Subject == "svc-inventory";
        if (!valid)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        var issued = "stub-service-token-" + Guid.NewGuid().ToString("N");
        _issued[issued] = scope!;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = issued, token_type = "Bearer", expires_in = 300 }),
        };
    }
}
