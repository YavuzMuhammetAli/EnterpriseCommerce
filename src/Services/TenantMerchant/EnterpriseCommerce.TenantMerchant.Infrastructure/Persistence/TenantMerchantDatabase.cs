using EnterpriseCommerce.TenantMerchant.Application.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>Connection settings of the Tenant &amp; Merchant SQL Server database.</summary>
internal sealed class TenantMerchantDatabaseOptions
{
    /// <summary>
    /// Configuration key: <c>ConnectionStrings:TenantMerchantDatabase</c> (environment variable
    /// <c>ConnectionStrings__TenantMerchantDatabase</c>). Never committed.
    /// </summary>
    public const string ConnectionStringName = "TenantMerchantDatabase";

    public string? ConnectionString { get; set; }
}

/// <summary>SQL Server error classification.</summary>
internal static class SqlServerErrors
{
    /// <summary>Violation of a PRIMARY KEY or UNIQUE constraint.</summary>
    public const int UniqueConstraintViolation = 2627;

    /// <summary>Cannot insert a duplicate key row (unique index).</summary>
    public const int UniqueIndexViolation = 2601;

    /// <summary>The transaction was chosen as a deadlock victim and rolled back.</summary>
    public const int Deadlock = 1205;

    /// <summary>A change would leave a tenant without an active owner (<c>TR_Memberships_RequireActiveOwner</c>).</summary>
    public const int LastActiveOwnerViolation = 50001;

    /// <summary>An audit record was to be updated or deleted (<c>TR_MembershipAuditRecords_AppendOnly</c>).</summary>
    public const int AuditAppendOnlyViolation = 50002;

    public static bool IsDeadlock(Exception exception) => HasError(exception, Deadlock);

    public static bool IsLastActiveOwnerViolation(Exception exception) => HasError(exception, LastActiveOwnerViolation);

    private static bool HasError(Exception exception, int number) =>
        FindSqlException(exception) is { } sqlException
        && (sqlException.Number == number || sqlException.Errors.Cast<SqlError>().Any(error => error.Number == number));

    public static bool IsUniqueViolation(Exception exception) =>
        FindSqlException(exception) is { } sqlException
        && (sqlException.Number is UniqueConstraintViolation or UniqueIndexViolation
            || sqlException.Errors.Cast<SqlError>().Any(error => error.Number is UniqueConstraintViolation or UniqueIndexViolation));

    /// <summary>Whether the failure came from SQL Server or the SQL client (including connection failures and timeouts).</summary>
    public static bool IsSqlServerFailure(Exception exception) =>
        FindSqlException(exception) is not null || exception is TimeoutException || exception.InnerException is TimeoutException;

    /// <summary>
    /// Runs a database operation and turns every SQL Server failure into <see cref="DataStoreUnavailableException"/>
    /// (answered with <c>503</c>). Cancellation and programming errors pass through unchanged.
    /// </summary>
    public static async Task<T> GuardAsync<T>(string operation, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException && IsSqlServerFailure(exception))
        {
            throw new DataStoreUnavailableException($"The Tenant & Merchant database could not complete '{operation}'.", exception);
        }
    }

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

/// <summary>Applies the Tenant &amp; Merchant EF Core migrations. Used at startup in Development only, behind an explicit flag.</summary>
public static class TenantMerchantDatabaseMigrator
{
    public static async Task ApplyTenantMerchantMigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TenantMerchantDbContext>();
        await context.Database.MigrateAsync(cancellationToken);
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations</c>. Generating a migration needs no connection. Commands that
/// connect take the connection string from <c>EC_TENANTMERCHANT_DESIGN_CONNECTION</c> or <c>--connection</c>; none is
/// stored in the repository.
/// </summary>
internal sealed class TenantMerchantDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TenantMerchantDbContext>
{
    public TenantMerchantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EC_TENANTMERCHANT_DESIGN_CONNECTION");
        var builder = new DbContextOptionsBuilder<TenantMerchantDbContext>();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            builder.UseSqlServer();
        }
        else
        {
            builder.UseSqlServer(connectionString);
        }

        return new TenantMerchantDbContext(builder.Options);
    }
}
