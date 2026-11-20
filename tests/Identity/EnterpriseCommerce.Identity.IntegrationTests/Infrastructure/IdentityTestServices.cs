using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using EnterpriseCommerce.Identity.Application;
using EnterpriseCommerce.Identity.Application.Tokens;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

/// <summary>Builds the real Application and Infrastructure services against a test database.</summary>
internal static class IdentityTestServices
{
    public static ServiceProvider Build(
        SqlServerTestDatabase database,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:IdentityDatabase"] = database.ConnectionString,
            ["Identity:Issuer"] = "https://localhost/",
            ["Identity:SecurityEmail:RetryBaseDelay"] = "00:00:00.010",
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddIdentityApplication(new RefreshReuseSettings("bff-admin"))
            .AddIdentityInfrastructure(configuration)
            .UseReplayProtectionLifetimes([TimeSpan.FromHours(1)]);

        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Creates an active, confirmed user with its security-version record, as registration will.</summary>
    public static async Task<string> CreateUserAsync(IServiceProvider services, long securityVersion = SecurityVersionPolicy.InitialVersion, bool emailConfirmed = true)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        var id = Guid.NewGuid().ToString();
        var email = $"{id}@example.test";

        context.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = emailConfirmed,
            LockoutEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            Status = AccountStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.SecurityVersions.Add(new SecurityVersionRecord { UserId = id, Version = securityVersion });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    public static async Task<long?> ReadSecurityVersionAsync(IServiceProvider services, string userId)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        return context.SecurityVersions.Where(r => r.UserId == userId).Select(r => (long?)r.Version).SingleOrDefault();
    }
}

/// <summary>Collects measurements of one meter, filtered by an <c>operation</c> tag unique to the test.</summary>
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, string? Operation, long Value)> _measurements = new();

    public MetricCollector(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? operation = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation")
                {
                    operation = tag.Value as string;
                }
            }

            _measurements.Enqueue((instrument.Name, operation, value));
        });
        _listener.Start();
    }

    public long Sum(string instrument, string operation) =>
        _measurements.Where(m => m.Instrument == instrument && m.Operation == operation).Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}
