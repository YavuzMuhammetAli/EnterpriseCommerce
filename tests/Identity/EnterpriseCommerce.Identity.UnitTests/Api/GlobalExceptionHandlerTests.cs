using System.Text.Json;
using EnterpriseCommerce.Identity.Api.ExceptionHandling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

public sealed class GlobalExceptionHandlerTests
{
    private const string SensitiveDetail = "Sensitive internal detail that must never reach the client";

    [Fact]
    public async Task TryHandleAsync_UnhandledException_ReturnsGeneric500ProblemDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

        var httpContext = CreateHttpContext(services);
        var handler = new GlobalExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<GlobalExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException(SensitiveDetail),
            cancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        Assert.StartsWith("application/problem+json", httpContext.Response.ContentType, StringComparison.Ordinal);

        var body = await ReadBodyAsync(httpContext, cancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(StatusCodes.Status500InternalServerError, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", json.RootElement.GetProperty("title").GetString());
        Assert.True(json.RootElement.TryGetProperty("traceId", out _), "Problem details should carry a traceId for correlation.");
    }

    [Fact]
    public async Task TryHandleAsync_UnhandledException_DoesNotLeakExceptionDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();

        var httpContext = CreateHttpContext(services);
        var handler = new GlobalExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<GlobalExceptionHandler>.Instance);

        await handler.TryHandleAsync(httpContext, new InvalidOperationException(SensitiveDetail), cancellationToken);

        var body = await ReadBodyAsync(httpContext, cancellationToken);
        Assert.DoesNotContain(SensitiveDetail, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = "/any";
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }

    private static async Task<string> ReadBodyAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
