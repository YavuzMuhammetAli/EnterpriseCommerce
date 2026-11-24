using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Cart.IntegrationTests.Api;

/// <summary>
/// The 256 KiB request body limit, on real Kestrel: the in-memory test server does not enforce body size limits. As in
/// Catalog (docs/testing TS-4), the 413 contract is tested with bodies whose only fault is their size: a declared
/// oversized body, and a chunked body that is valid JSON up to the limit (leading white space). Catalog is never called.
/// </summary>
public sealed class RequestSizeLimitTests : IAsyncLifetime
{
    private const int Limit = 256 * 1024;

    private readonly CartApiFixture _fixture = new();
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
    public async Task AnOversizedBody_Is413_AndCatalogIsNotCalled()
    {
        using var client = CartApiFixture.CreateClient(_factory!, _fixture.CustomerToken(CartApiFixture.NewCustomer()));

        using var declared = new ByteArrayContent(new byte[Limit + 1]);
        declared.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var declaredResponse = await client.PostAsync("/api/v1/cart/items", declared, TestContext.Current.CancellationToken);

        var json = new string(' ', Limit) + $$"""{"productId":"{{Guid.CreateVersion7()}}","variantId":"{{Guid.CreateVersion7()}}","quantity":1}""";
        using var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/items") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declaredResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        Assert.Equal(0, _fixture.Catalog.CallCount);
    }
}
