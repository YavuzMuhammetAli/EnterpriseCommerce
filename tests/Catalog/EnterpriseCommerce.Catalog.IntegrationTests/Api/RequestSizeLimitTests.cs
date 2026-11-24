using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// The 256 KiB request body limit (ADR-024 section 14), on real Kestrel: the in-memory test server does not enforce
/// body size limits. Both a declared oversized body and an oversized chunked body are refused with 413 before any
/// authorization or store work.
/// </summary>
/// <remarks>
/// A chunked body's size is known only once more than the limit has arrived, while the JSON binding may already parse
/// what has arrived. An oversized body that is also malformed (for example zero bytes) is therefore refused either as
/// too large (413) or as malformed (400), depending on which happens first: both are correct refusals, and the outcome
/// varies with timing under load (docs/testing TS-4, resolved). The 413 contract is tested with a body that is valid
/// JSON up to the limit, so the size is the only reason to refuse it; the malformed case is tested separately.
/// </remarks>
public sealed class RequestSizeLimitTests : IAsyncLifetime
{
    private const int Limit = 256 * 1024;

    private readonly CatalogApiFixture _fixture = new();
    private WebApplicationFactory<Program>? _factory;

    public ValueTask InitializeAsync()
    {
        _factory = _fixture.CreateFactory();
        _factory.UseKestrel(0);
        _factory.StartServer();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task AnOversizedBody_Is413()
    {
        var tenantId = _fixture.NewTenant(out var owner, out _);
        using var client = CatalogApiFixture.CreateClient(_factory!, _fixture.UserToken(owner));
        var calls = _fixture.TenantMerchant.VerificationCalls;

        using var declared = new ByteArrayContent(new byte[Limit + 1]);
        declared.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var declaredResponse = await client.PostAsync($"/api/v1/catalog/tenants/{tenantId}/products", declared, TestContext.Current.CancellationToken);

        // Valid JSON for its first 256 KiB: only its size can make the server refuse it.
        using var chunkedResponse = await PostChunkedAsync(client, tenantId, OversizedJson());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declaredResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        Assert.Equal(calls, _fixture.TenantMerchant.VerificationCalls);
    }

    [Fact]
    public async Task AnOversizedMalformedChunkedBody_IsRefusedAsTooLargeOrMalformed_AndNeverProcessed()
    {
        var tenantId = _fixture.NewTenant(out var owner, out _);
        using var client = CatalogApiFixture.CreateClient(_factory!, _fixture.UserToken(owner));
        var calls = _fixture.TenantMerchant.VerificationCalls;

        using var response = await PostChunkedAsync(client, tenantId, new byte[Limit + 1]);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.BadRequest });
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(calls, _fixture.TenantMerchant.VerificationCalls);
    }

    private static byte[] OversizedJson() =>
        Encoding.UTF8.GetBytes("{\"slug\":\"oversized\",\"name\":\"" + new string('a', Limit) + "\"}");

    private static Task<HttpResponseMessage> PostChunkedAsync(HttpClient client, Guid tenantId, byte[] body)
    {
        var chunked = new StreamContent(new MemoryStream(body));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
