using EnterpriseCommerce.Identity.Api.ExceptionHandling;
using EnterpriseCommerce.Identity.Application;
using EnterpriseCommerce.Identity.Infrastructure;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: configured from the "Serilog" configuration section.
builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", "Identity"));

// Global exception handling: unhandled exceptions become RFC 9457 problem details.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}");
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// Layer composition.
builder.Services
    .AddIdentityApplication()
    .AddIdentityInfrastructure(builder.Configuration);

var app = builder.Build();

// Development convenience only, behind an explicit flag: deployed environments apply migrations as a
// separate, reviewed step and never at application startup.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Identity:Database:ApplyMigrationsAtStartup"))
{
    await app.Services.ApplyIdentityMigrationsAsync(app.Lifetime.ApplicationStopping);
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSerilogRequestLogging(options =>
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

await app.RunAsync();
