using Serilog.Context;

namespace EnterpriseCommerce.TenantMerchant.Api.Hosting;

/// <summary>
/// <c>X-Correlation-ID</c> (IDENTITY_API_CONTRACT.md section 1.3, the cross-service convention): optional on requests,
/// generated when absent or unusable, always echoed on the response, written to every log event of the request and
/// added to problem details.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    public const int MaxLength = 64;
    private const string ItemKey = "EnterpriseCommerce.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName] is [{ } supplied] && IsAcceptable(supplied)
            ? supplied
            : Guid.NewGuid().ToString("D");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }

    /// <summary>The request's correlation ID, once the middleware has run.</summary>
    public static string? Get(HttpContext context) => context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;

    /// <summary>
    /// A client-chosen opaque value is kept only if it is short and made of safe characters, so that it can never
    /// inject anything into headers or logs.
    /// </summary>
    private static bool IsAcceptable(string value) =>
        value.Length is > 0 and <= MaxLength
        && value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' or ':');
}
