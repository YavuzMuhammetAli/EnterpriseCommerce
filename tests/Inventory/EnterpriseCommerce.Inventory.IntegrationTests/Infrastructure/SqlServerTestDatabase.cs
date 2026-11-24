using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway Inventory database on a real SQL Server, created from the migrations and dropped afterwards.
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
    public const string RequireVariable = "EC_REQUIRE_INFRA_TESTS";

    private readonly string? _serverConnectionString = Environment.GetEnvironmentVariable(ServerVariable);
    private readonly string _databaseName = $"ec_inventory_test_{Guid.NewGuid():N}";
    private ServiceProvider? _services;

    public string ConnectionString { get; private set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_serverConnectionString);

    /// <summary>Skips the calling test when no SQL Server is configured, or fails it when infrastructure tests are required.</summary>
    public void RequireAvailable()
    {
        if (IsConfigured)
        {
            return;
        }

        var message = $"SQL Server integration test: set {ServerVariable} to a SQL Server connection string (see SqlServerTestDatabase).";
        if (string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{RequireVariable} is set but {ServerVariable} is not. {message}");
        }

        Assert.Skip(message);
    }

    internal InventoryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(ConnectionString).Options);

    /// <summary>The real write gate on this database, without a web host.</summary>
    internal IInventoryWriteGate Gate()
    {
        _services ??= BuildServices();
        return _services.GetRequiredService<IInventoryWriteGate>();
    }

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

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddDbContext<InventoryDbContext>(options => options.UseSqlServer(ConnectionString));
        services.AddSingleton<IInventoryWriteGate, InventoryWriteGate>();
        return services.BuildServiceProvider();
    }
}
