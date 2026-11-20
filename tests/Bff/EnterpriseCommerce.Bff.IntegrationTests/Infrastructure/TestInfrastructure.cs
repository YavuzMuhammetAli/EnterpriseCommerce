using Microsoft.Data.SqlClient;
using StackExchange.Redis;

namespace EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

/// <summary>
/// The infrastructure the BFF tests need, from the environment:
/// <list type="bullet">
/// <item><c>EC_TEST_SQLSERVER</c>: a SQL Server for Identity's throwaway database.</item>
/// <item><c>EC_TEST_REDIS_SESSIONS</c>: the dedicated session Redis (<c>noeviction</c>), for example
/// <c>127.0.0.1:6380,password=&lt;REDIS_SESSIONS_PASSWORD&gt;</c> with <c>docker compose --profile bff up -d --wait</c>.</item>
/// </list>
/// Without them the tests are skipped with a reason; with <c>EC_REQUIRE_INFRA_TESTS=true</c> (CI) they fail instead,
/// so these tests can never silently disappear. Mocks are not used for Redis: persistence, eviction policy and
/// concurrency are tested against a real server.
/// </summary>
public static class TestInfrastructure
{
    public const string SqlServerVariable = "EC_TEST_SQLSERVER";
    public const string RedisVariable = "EC_TEST_REDIS_SESSIONS";
    public const string RequireVariable = "EC_REQUIRE_INFRA_TESTS";

    public static string? SqlServer => Environment.GetEnvironmentVariable(SqlServerVariable) is { Length: > 0 } value ? value : null;

    public static string? Redis => Environment.GetEnvironmentVariable(RedisVariable) is { Length: > 0 } value ? value : null;

    public static bool IsConfigured => SqlServer is not null && Redis is not null;

    public static void RequireAvailable()
    {
        if (IsConfigured)
        {
            return;
        }

        var message = $"BFF integration test: set {SqlServerVariable} (SQL Server) and {RedisVariable} (session Redis with noeviction).";
        if (string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{RequireVariable} is set but the infrastructure is not configured. {message}");
        }

        Assert.Skip(message);
    }

    /// <summary>The session Redis connection string with short timeouts, optionally through another endpoint.</summary>
    public static string RedisConnection(int? port = null)
    {
        var options = ConfigurationOptions.Parse(Redis!);
        options.ConnectTimeout = 2000;
        options.SyncTimeout = 2000;
        options.AsyncTimeout = 2000;
        options.AbortOnConnectFail = false;
        if (port is not null)
        {
            options.EndPoints.Clear();
            options.EndPoints.Add("127.0.0.1", port.Value);
        }

        return options.ToString(includePassword: true);
    }

    /// <summary>The host and port of the configured session Redis.</summary>
    public static (string Host, int Port) RedisEndpoint()
    {
        var endpoint = ConfigurationOptions.Parse(Redis!).EndPoints[0];
        return endpoint switch
        {
            System.Net.DnsEndPoint dns => (dns.Host, dns.Port == 0 ? 6379 : dns.Port),
            System.Net.IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            _ => throw new InvalidOperationException("Unsupported Redis endpoint."),
        };
    }

    public static SqlConnectionStringBuilder SqlServerBuilder(string database) => new(SqlServer) { InitialCatalog = database };
}
