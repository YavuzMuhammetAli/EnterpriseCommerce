using EnterpriseCommerce.Identity.Api.Accounts;
using EnterpriseCommerce.Identity.Api.Diagnostics;
using EnterpriseCommerce.Identity.Api.ExceptionHandling;
using EnterpriseCommerce.Identity.Api.Hosting;
using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Api.SecurityEmails;
using EnterpriseCommerce.Identity.Application;
using EnterpriseCommerce.Identity.Application.Tokens;
using EnterpriseCommerce.Identity.Infrastructure;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: configured from the "Serilog" configuration section. The host owns its logger instead of the
// process-wide static Log.Logger, so every host instance (including test hosts) keeps its own sinks.
builder.Services.AddSerilog(
    (services, logger) => logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "Identity"),
    preserveStaticLogger: true);

// Global exception handling: unhandled exceptions become RFC 9457 problem details.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}");
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi();
builder.Services.AddIdentityHealthChecks();
builder.Services.AddRazorPages();

// Layer composition.
var adminConsoleClientId = builder.Configuration[$"{IdentityServerSettings.SectionName}:{nameof(IdentityServerSettings.AdminConsoleClientId)}"]
    ?? new IdentityServerSettings().AdminConsoleClientId;
builder.Services
    .AddIdentityApplication(new RefreshReuseSettings(adminConsoleClientId))
    .AddIdentityInfrastructure(builder.Configuration);
// The security email guard runs first: outside Development a development mail sink must stop the host before
// anything else is composed (ADR-018 section 4).
builder.AddSecurityEmailDelivery();
builder.AddIdentityDataProtection();
builder.AddIdentityProtocolServer();
builder.AddTrustedProxy();

var app = builder.Build();

// Development convenience only, behind an explicit flag: deployed environments apply migrations as a
// separate, reviewed step and never at application startup.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Identity:Database:ApplyMigrationsAtStartup"))
{
    await app.Services.ApplyIdentityMigrationsAsync(app.Lifetime.ApplicationStopping);
}

// Forwarded headers first, explicitly: OpenIddict serves discovery from the authentication middleware, and minimal
// hosting would otherwise not apply them before it (PoC-14). Only the Gateway is trusted (TrustedProxySetup).
app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Request logs carry the path only, never the query string (ADR-017 section 11).
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

app.UseRouting();
app.UseRateLimiter();

// OpenIddict serves its protocol endpoints from the authentication middleware.
app.UseAuthentication();
app.UseAuthorization();
app.UseCredentialFormProtection();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapIdentityHealthChecks();
app.MapRazorPages();
app.MapProtocolEndpoints();
app.MapAccountApiEndpoints();
app.MapInternalAccountEndpoints();

await app.RunAsync();
