using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Infrastructure.Cleanup;
using EnterpriseCommerce.Cart.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway Cart database on a real SQL Server, created from the migrations and dropped afterwards.
/// </summary>
/// <remarks>
/// <para>The server comes from <c>EC_TEST_SQLSERVER</c> (a connection string without a database name), for example:</para>
/// <list type="bullet">
/// <item>Compose (<c>docker compose --profile sqlserver up -d --wait</c>):
/// <c>Server=127.0.0.1,1433;User ID=sa;Password=&lt;MSSQL_SA_PASSWORD from deploy/docker/.env&gt;;TrustServerCertificate=true</c></item>
/// <item>LocalDB: <c>Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true</c></item>
/// </list>
/// <para>
/// Without it, SQL-backed tests are skipped with a reason. CI sets <c>EC_REQUIRE_INFRA_TESTS=true</c>, which turns
/// that skip into a failure, so infrastructure tests can never silently disappear from CI.
/// </para>
/// </remarks>
public sealed class SqlServerTestDatabase : IAsyncLifetime
{
    public const string ServerVariable = "EC_TEST_SQLSERVER";

    private readonly string? _serverConnectionString = Environment.GetEnvironmentVariable(ServerVariable);
    private readonly string _databaseName = $"ec_cart_test_{Guid.NewGuid():N}";
    private ServiceProvider? _services;

    public string ConnectionString { get; private set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_serverConnectionString);

    /// <summary>The time the store and the cleanup see; tests may move it.</summary>
    public MutableTime Time { get; } = new();

    /// <summary>Skips the calling test when no SQL Server is configured, or fails it when infrastructure tests are required.</summary>
    public void RequireAvailable()
    {
        if (!IsConfigured)
        {
            Infra.SkipOrFail($"SQL Server integration test: set {ServerVariable} to a SQL Server connection string (see SqlServerTestDatabase).");
        }
    }

    internal CartDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CartDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>The real store on this database, without a web host.</summary>
    internal ICartStore Store() => Services().GetRequiredService<ICartStore>();

    internal ICartQueries Queries(out IServiceScope scope)
    {
        scope = Services().CreateScope();
        return scope.ServiceProvider.GetRequiredService<ICartQueries>();
    }

    internal CartCleanupRunner Cleanup(int batchSize = 100) =>
        new(
            Services().GetRequiredService<IServiceScopeFactory>(),
            Time,
            new CartLifecycleSettings(),
            Options.Create(new CartCleanupOptions { BatchSize = batchSize }),
            new CartCleanupState(Time),
            NullLogger<CartCleanupRunner>.Instance);

    /// <summary>Runs a SQL statement against the test database (test-only setup or a direct check of a constraint).</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask InitializeAsync()
    {
        if (!IsConfigured)
        {
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

        // Migration from an empty database: the database does not exist before this call.
        await using var context = CreateContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsConfigured)
        {
            return;
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" }.ConnectionString;

        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // The name is generated above from a GUID, never from input. QUOTENAME guards the dynamic statement anyway.
        command.CommandText = """
            DECLARE @name sysname = @databaseName;
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) =
                    N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
                EXEC (@sql);
            END
            """;
        command.Parameters.AddWithValue("@databaseName", _databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private ServiceProvider Services()
    {
        if (_services is null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<TimeProvider>(Time);
            services.AddDbContext<CartDbContext>(options => options.UseSqlServer(ConnectionString));
            services.AddSingleton<ICartStore, CartStore>();
            services.AddScoped<ICartQueries, CartQueries>();
            _services = services.BuildServiceProvider();
        }

        return _services;
    }
}

/// <summary>A clock the tests move by hand; it starts at the real time.</summary>
public sealed class MutableTime : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}

/// <summary>The skip-or-fail rule of every infrastructure test (docs/testing/README.md).</summary>
public static class Infra
{
    public const string RequireVariable = "EC_REQUIRE_INFRA_TESTS";

    public static void SkipOrFail(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{RequireVariable} is set but the infrastructure is not configured. {message}");
        }

        Assert.Skip(message);
    }
}
