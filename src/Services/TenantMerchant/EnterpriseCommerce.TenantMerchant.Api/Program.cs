using EnterpriseCommerce.TenantMerchant.Api.Diagnostics;
using EnterpriseCommerce.TenantMerchant.Api.ExceptionHandling;
using EnterpriseCommerce.TenantMerchant.Api.Hosting;
using EnterpriseCommerce.TenantMerchant.Api.Invitations;
using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Api.Tenants;
using EnterpriseCommerce.TenantMerchant.Application;
using EnterpriseCommerce.TenantMerchant.Infrastructure;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
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
        .Enrich.WithProperty("Service", "TenantMerchant"),
    preserveStaticLogger: true);

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
builder.Services.AddTenantMerchantHealthChecks();

builder.Services
    .AddTenantMerchantApplication()
    .AddTenantMerchantInfrastructure(builder.Configuration);
builder.AddTenantMerchantSecurity();

var app = builder.Build();

// Development convenience only, behind an explicit flag: deployed environments apply migrations as a separate,
// reviewed step and never at application startup.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("TenantMerchant:Database:ApplyMigrationsAtStartup"))
{
    await app.Services.ApplyTenantMerchantMigrationsAsync(app.Lifetime.ApplicationStopping);
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

app.MapTenantMerchantHealthChecks();
app.MapInternalTenantEndpoints();
app.MapInternalMembershipVerificationEndpoints();
app.MapTenantEndpoints();
app.MapMembershipEndpoints();
app.MapInvitationEndpoints();

await app.RunAsync();
