using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

/// <summary>
/// The Catalog host on a throwaway MongoDB database (<see cref="MongoTestDatabase"/>). Initialization waits until the
/// host is ready, that is until every index exists, so tests start with uniqueness enforced.
/// </summary>
public class CatalogMongoFixture : CatalogApiFixture
{
    /// <summary>How long the index setup of a fresh database may take.</summary>
    public static readonly TimeSpan ReadinessDeadline = TimeSpan.FromSeconds(30);

    public MongoTestDatabase Mongo { get; } = new();

    public void RequireAvailable() => Mongo.RequireAvailable();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (Mongo.IsConfigured)
        {
            await WaitUntilReadyAsync(Factory);
        }
    }

    /// <summary>Waits until <c>/health/ready</c> answers 200.</summary>
    public static async Task WaitUntilReadyAsync(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var client = factory.CreateClient();
        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadinessDeadline,
            "the Catalog host is ready (MongoDB reachable, indexes ensured)");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Mongo.DisposeAsync();
    }

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (Mongo.IsConfigured)
        {
            builder.UseSetting("ConnectionStrings:CatalogDatabase", Mongo.ConnectionString);
            builder.UseSetting("Catalog:Database:DatabaseName", Mongo.DatabaseName);
            builder.UseSetting("Catalog:Database:ServerSelectionTimeout", "00:00:05");
        }
    }
}
