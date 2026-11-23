using System.Net;
using System.Net.Http.Headers;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// The 256 KiB request body limit (ADR-024 section 14), on real Kestrel: the in-memory test server does not enforce
/// body size limits. Both a declared oversized body and an oversized chunked body are refused with 413 before any
/// authorization or store work.
/// </summary>
public sealed class RequestSizeLimitTests : IAsyncLifetime
{
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

        using var declared = new ByteArrayContent(new byte[(256 * 1024) + 1]);
        declared.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var declaredResponse = await client.PostAsync($"/api/v1/catalog/tenants/{tenantId}/products", declared, TestContext.Current.CancellationToken);

        using var chunked = new StreamContent(new MemoryStream(new byte[(256 * 1024) + 1]));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var chunkedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands") { Content = chunked };
        chunkedRequest.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await client.SendAsync(chunkedRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declaredResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        Assert.Equal(calls, _fixture.TenantMerchant.VerificationCalls);
    }
}
