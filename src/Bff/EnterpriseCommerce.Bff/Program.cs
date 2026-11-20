using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Endpoints;
using EnterpriseCommerce.Bff.Hosting;
using EnterpriseCommerce.Bff.Protocol;
using EnterpriseCommerce.Bff.Proxy;
using EnterpriseCommerce.Bff.Security;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Exactly one front-end deployment per instance (ADR-016 section 2); any configuration error stops the host.
var settings = builder.Configuration.GetSection(BffSettings.SectionName).Get<BffSettings>() ?? new BffSettings();
var settingsErrors = BffSettingsValidation.Validate(settings, builder.Environment.IsDevelopment()).ToList();
var sessionStoreConnection = builder.Configuration.GetConnectionString(BffSettings.SessionStoreConnectionName);
if (string.IsNullOrWhiteSpace(sessionStoreConnection))
{
    settingsErrors.Add($"ConnectionStrings:{BffSettings.SessionStoreConnectionName} (the dedicated session Redis) is required.");
}

if (settingsErrors.Count > 0)
{
    throw new InvalidOperationException("Invalid BFF configuration: " + string.Join(" ", settingsErrors));
}

var cookieNames = BffCookieNames.For(settings);

builder.Services.AddSerilog(
    (services, logger) => logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "Bff")
        .Enrich.WithProperty("Deployment", settings.Deployment),
    preserveStaticLogger: true);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BffExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<BffSettings>(builder.Configuration.GetSection(BffSettings.SectionName));
builder.Services.AddSingleton(cookieNames);

// Data Protection: protects the OAuth state, the nonce cookie and the session record contents. One key ring per
// deployment (its own application name), shared by that deployment's instances.
// Encrypted at rest with a certificate outside Development (Phase 2.3 S6.4).
DataProtectionSetup.Configure(builder.Services, settings);

// The dedicated session Redis (ADR-016 section 3); the host refuses to start unless it uses noeviction.
builder.Services.AddSingleton<IRedisConnection>(_ => new RedisConnection(sessionStoreConnection!));
builder.Services.AddHostedService<SessionStorePolicyCheck>();
builder.Services.AddSingleton<RedisSessionStore>();
builder.Services.AddSingleton<RefreshCoordinator>();
builder.Services.AddSingleton<ClientAssertionFactory>();
builder.Services.AddSingleton<IdentityProtocolClient>();

// One back-channel client to Identity, with no retry or resilience handler (finding I3, ADR-016 section 4).
builder.Services.AddHttpClient(IdentityProtocolClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

builder.Services
    .AddAuthentication(BffSessionDefaults.Scheme)
    .AddScheme<BffSessionOptions, BffSessionHandler>(BffSessionDefaults.Scheme, _ => { })
    .AddIdentityClient();
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OidcSetup.ConfigureOptions>();
builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = BrowserRequestProtection.HeaderName;
    options.Cookie.Name = cookieNames.Antiforgery;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.SuppressXFrameOptionsHeader = true;
});
builder.Services.AddSingleton<IAntiforgeryAdditionalDataProvider, SessionBoundAntiforgeryData>();

builder.Services.AddApiForwarding(settings);
// Readiness depends on the session store; liveness on the process only (Phase 2.3 S6.5).
builder.Services.AddHealthChecks().AddCheck<SessionStoreHealthCheck>("session-store", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
builder.AddTrustedProxy();

var app = builder.Build();

// Forwarded headers only from the Gateway (TrustedProxySetup), before anything reads the scheme or host.
app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseBffSecurityHeaders();

// Request logs carry the path only, never the query string: the callback query holds the authorization code.
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

// The front end's compiled static files, when the deployment ships them in wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseBrowserRequestProtection();
app.UseAuthorization();

// /health keeps its S7 meaning (session store included). Responses carry the status word only.
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapBffEndpoints();
app.MapApiForwarding();
if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
{
    app.MapFallbackToFile("index.html");
}

await app.RunAsync();
