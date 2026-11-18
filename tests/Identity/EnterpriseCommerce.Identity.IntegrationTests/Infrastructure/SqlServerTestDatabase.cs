using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway Identity database on a real SQL Server, created from the migrations and dropped afterwards.
/// </summary>
/// <remarks>
/// <para>The server comes from <c>EC_TEST_SQLSERVER</c> (a connection string without a database name), for example:</para>
/// <list type="bullet">
/// <item>LocalDB: <c>Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true</c></item>
/// <item>Compose (<c>docker compose --profile sqlserver up -d --wait</c>):
/// <c>Server=127.0.0.1,1433;User ID=sa;Password=&lt;MSSQL_SA_PASSWORD from deploy/docker/.env&gt;;TrustServerCertificate=true</c></item>
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
    private readonly string _databaseName = $"ec_identity_test_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    private bool IsConfigured => !string.IsNullOrWhiteSpace(_serverConnectionString);

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

    public async ValueTask InitializeAsync()
    {
        if (!IsConfigured)
        {
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

        var options = new DbContextOptionsBuilder<IdentityServiceDbContext>()
            .UseSqlServer(ConnectionString)
            .UseOpenIddict()
            .Options;

        await using var context = new IdentityServiceDbContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsConfigured)
        {
            return;
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
}
