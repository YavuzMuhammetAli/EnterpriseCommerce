using System.Threading.RateLimiting;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "Gateway"));

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Edge rate limiting per client IP (ADR-007 edge concern; ADR-017 section 9 and ADR-018 section 2 for the Identity
// account pages). Routes opt in with "RateLimiterPolicy": "identity-account". Identity applies its own per-IP and
// per-account limits as well; the Gateway never replaces them.
var accountRequestsPerMinute = builder.Configuration.GetValue("Gateway:RateLimits:IdentityAccountRequestsPerMinute", 30);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("identity-account", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = accountRequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Routes and clusters are externalized in configuration ("ReverseProxy" section).
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSerilogRequestLogging(options =>
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information);

// The gateway's own liveness; never proxied.
app.MapHealthChecks("/health");

app.UseRateLimiter();

// Internal service-to-service endpoints (/internal/...) are never exposed at the edge, on any host (ADR-007 edge
// concern; IDENTITY_API_CONTRACT.md section 2.12). Services still authorize every internal call themselves.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments(InternalPaths.Prefix, StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    return next(context);
});

app.MapReverseProxy();

await app.RunAsync();

/// <summary>Path prefixes the Gateway never forwards.</summary>
internal static class InternalPaths
{
    public const string Prefix = "/internal";
}
