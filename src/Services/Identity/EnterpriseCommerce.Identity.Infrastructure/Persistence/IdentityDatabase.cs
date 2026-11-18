using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.Infrastructure.Persistence;

/// <summary>Connection settings of Identity's SQL Server database.</summary>
internal sealed class IdentityDatabaseOptions
{
    /// <summary>Configuration key: <c>ConnectionStrings:IdentityDatabase</c> (environment variable <c>ConnectionStrings__IdentityDatabase</c>).</summary>
    public const string ConnectionStringName = "IdentityDatabase";

    public string? ConnectionString { get; set; }
}

/// <summary>SQL Server error classification used by the retry and failure policies.</summary>
internal static class SqlServerErrors
{
    /// <summary>Transaction was deadlocked and chosen as the victim.</summary>
    public const int Deadlock = 1205;

    /// <summary>Violation of a PRIMARY KEY or UNIQUE constraint.</summary>
    public const int UniqueConstraintViolation = 2627;

    /// <summary>Cannot insert a duplicate key row (unique index).</summary>
    public const int UniqueIndexViolation = 2601;

    public static bool IsDeadlock(Exception exception) => HasError(exception, number => number == Deadlock);

    public static bool IsUniqueViolation(Exception exception) =>
        HasError(exception, number => number is UniqueConstraintViolation or UniqueIndexViolation);

    /// <summary>Whether the failure came from SQL Server or the SQL client (including connection failures).</summary>
    public static bool IsSqlServerFailure(Exception exception) => FindSqlException(exception) is not null;

    private static bool HasError(Exception exception, Func<int, bool> predicate) =>
        FindSqlException(exception) is { } sqlException
        && (predicate(sqlException.Number) || sqlException.Errors.Cast<SqlError>().Any(error => predicate(error.Number)));

    private static SqlException? FindSqlException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        return null;
    }
}

/// <summary>Applies Identity's EF Core migrations. Used at startup in Development only, behind an explicit flag.</summary>
public static class IdentityDatabaseMigrator
{
    public static async Task ApplyIdentityMigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. Generating a migration needs no connection.
/// Commands that connect take the connection string from <c>EC_IDENTITY_DESIGN_CONNECTION</c> or
/// <c>--connection</c>; none is stored in the repository.
/// </summary>
internal sealed class IdentityServiceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IdentityServiceDbContext>
{
    public IdentityServiceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EC_IDENTITY_DESIGN_CONNECTION");
        var builder = new DbContextOptionsBuilder<IdentityServiceDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            builder.UseSqlServer();
        }
        else
        {
            builder.UseSqlServer(connectionString);
        }

        builder.UseOpenIddict();
        return new IdentityServiceDbContext(builder.Options);
    }
}
