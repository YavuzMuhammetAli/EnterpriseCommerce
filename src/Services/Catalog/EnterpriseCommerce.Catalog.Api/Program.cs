using EnterpriseCommerce.Catalog.Api.Diagnostics;
using EnterpriseCommerce.Catalog.Api.ExceptionHandling;
using EnterpriseCommerce.Catalog.Api.Hosting;
using EnterpriseCommerce.Catalog.Api.Security;
using EnterpriseCommerce.Catalog.Application;
using EnterpriseCommerce.Catalog.Infrastructure;
using EnterpriseCommerce.Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        .Enrich.WithProperty("Service", "Catalog"),
    preserveStaticLogger: true);

// No request body above the limit reaches the application, whatever the endpoint (ADR-024 section 14).
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
builder.Services.AddHealthChecks()
    .AddCheck<CatalogStoreHealthCheck>(
        "catalog-database",
        failureStatus: HealthStatus.Unhealthy,
        tags: [CatalogInfrastructureHealth.ReadyTag],
        timeout: CatalogInfrastructureHealth.CheckTimeout);

builder.Services
    .AddCatalogApplication()
    .AddCatalogInfrastructure(builder.Configuration);
builder.AddCatalogSecurity();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Request logs carry the path only, never the query string or headers (ADR-017 section 11): search terms stay out.
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

app.MapCatalogHealthChecks();

await app.RunAsync();
