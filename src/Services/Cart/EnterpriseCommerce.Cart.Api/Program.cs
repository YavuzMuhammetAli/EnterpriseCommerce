using EnterpriseCommerce.Cart.Api.Diagnostics;
using EnterpriseCommerce.Cart.Api.ExceptionHandling;
using EnterpriseCommerce.Cart.Api.Hosting;
using EnterpriseCommerce.Cart.Api.Security;
using EnterpriseCommerce.Cart.Application;
using EnterpriseCommerce.Cart.Infrastructure;
using EnterpriseCommerce.Cart.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Structured logging from the "Serilog" configuration section. The host owns its logger instead of the process-wide
// static Log.Logger, so every host instance (including test hosts) keeps its own sinks.
builder.Services.AddSerilog(
    (services, logger) => logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "Cart"),
    preserveStaticLogger: true);

// No request body above the limit reaches the application, whatever the endpoint.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = RequestLimits.MaxRequestBodyBytes);

// Unhandled exceptions and empty error responses become RFC 9457 problem details, with the correlation ID.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
        if (CorrelationIdMiddleware.Get(context.HttpContext) is { } correlationId)
        {
            context.ProblemDetails.Extensions["correlationId"] = correlationId;
        }
    });
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi();
builder.Services.AddCartHealthChecks();
builder.Services
    .AddCartApplication()
    .AddCartInfrastructure(builder.Configuration);
builder.AddCartSecurity();

var app = builder.Build();

// Development only, behind an explicit flag: deployed environments run migrations as a separate step.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Cart:Database:ApplyMigrationsAtStartup"))
{
    await app.Services.ApplyCartMigrationsAsync(CancellationToken.None);
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Request logs carry the path only, never the query string or headers (ADR-017 section 11).
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
});

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapCartHealthChecks();

await app.RunAsync();

/// <summary>The entry point, visible to the integration tests' host factory.</summary>
public partial class Program;
