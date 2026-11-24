using System.Data;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Inventory.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of <see cref="IInventoryWriteGate"/> (ADR-026 section 9). Each attempt gets a fresh scope
/// and context, and one <c>READ COMMITTED</c> transaction with <c>SET LOCK_TIMEOUT 5000</c>. The first statement reads
/// the database time used for every decision.
/// </summary>
/// <remarks>
/// <para>
/// The session's locking reads take update locks held to the end of the transaction: two writers of one item, or of
/// one reservation, are serialized while readers continue. The paths take their locks in one order (the idempotency
/// application lock, the reservation, then the items in ascending ID order), so they do not deadlock each other. A
/// deadlock with another statement is still possible and is retried as a whole.
/// </para>
/// <para>
/// A lock that cannot be obtained in time fails the operation with <c>503</c> instead of holding the request.
/// </para>
/// </remarks>
internal sealed partial class InventoryWriteGate(IServiceScopeFactory scopes, ILogger<InventoryWriteGate> logger) : IInventoryWriteGate
{
    public const int MaxAttempts = 3;
    public const int LockTimeoutMilliseconds = 5000;

    private const string SetLockTimeout = "SET LOCK_TIMEOUT 5000";

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(25);

    public async Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<IInventoryWriteSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(work, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && IsDeadlock(exception) && attempt < MaxAttempts)
            {
                LogDeadlockRetried(logger, operation, attempt);
                await Task.Delay(BaseBackoff * Math.Pow(2, attempt - 1) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25)), cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new InventoryWriteConflictException(InventoryWriteConflict.Concurrency, "A concurrent change won.", exception);
            }
            catch (DbUpdateException exception) when (SqlServerErrors.IsUniqueViolation(exception))
            {
                throw new InventoryWriteConflictException(InventoryWriteConflict.UniqueConstraint, "A unique constraint rejected the change.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsSqlServerFailure(exception))
            {
                if (SqlServerErrors.IsDeadlock(exception))
                {
                    LogDeadlockExhausted(logger, operation, attempt);
                }

                throw new DataStoreUnavailableException($"The Inventory database could not complete '{operation}'.", exception);
            }
            catch (ApplicationLockDeadlockException exception)
            {
                LogDeadlockExhausted(logger, operation, attempt);
                throw new DataStoreUnavailableException($"The Inventory database could not complete '{operation}'.", exception);
            }
        }
    }

    private static bool IsDeadlock(Exception exception) => exception is ApplicationLockDeadlockException || SqlServerErrors.IsDeadlock(exception);

    private async Task<TResult> RunOnceAsync<TResult>(
        Func<IInventoryWriteSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(SetLockTimeout, cancellationToken);
        var now = (await context.Database
            .SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), 0) AS [Value]")
            .ToListAsync(cancellationToken)).Single();

        var session = new InventoryWriteSession(context, now);
        var decision = await work(session, cancellationToken);
        if (decision.ShouldCommit)
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return decision.Result;
    }

    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Warning,
        Message = "Inventory write {Operation} was a deadlock victim on attempt {Attempt}; retrying the whole transaction")]
    private static partial void LogDeadlockRetried(ILogger logger, string operation, int attempt);

    [LoggerMessage(
        EventId = 5021,
        Level = LogLevel.Error,
        Message = "Inventory write {Operation} failed: deadlock retries exhausted after {Attempt} attempts")]
    private static partial void LogDeadlockExhausted(ILogger logger, string operation, int attempt);
}

/// <summary>The locking reads and staged changes of one write transaction. Every read is scoped by its tenant.</summary>
internal sealed class InventoryWriteSession(InventoryDbContext context, DateTimeOffset now) : IInventoryWriteSession
{
    /// <summary>The application lock resource prefix of a reservation idempotency scope (transaction-owned).</summary>
    public const string ReservationKeyLockPrefix = "inventory-reservation-key:";

    public DateTimeOffset Now { get; } = now;

    public void UseMessageMetadata(MessageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        context.UseMessageMetadata(metadata);
    }

    public Task<InventoryItem?> LockItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken) =>
        context.InventoryItems
            .FromSql($"SELECT * FROM [InventoryItems] WITH (UPDLOCK, ROWLOCK) WHERE [TenantId] = {tenantId} AND [Id] = {itemId}")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, InventoryItem>> LockItemsOfVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variantIds);

        // Resolve the IDs without locks (a variant's item never changes its variant), then lock them in ID order.
        var ids = await context.InventoryItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && variantIds.Contains(item.VariantId))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var locked = await LockItemsAsync(tenantId, ids, cancellationToken);
        return locked.Values.ToDictionary(item => item.VariantId);
    }

    public async Task<IReadOnlyDictionary<Guid, InventoryItem>> LockItemsAsync(Guid tenantId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);

        // One locking read per item, in ascending ID order: the lock order of ADR-026 section 9. A single IN query
        // would not guarantee the order in which SQL Server takes the row locks.
        var items = new Dictionary<Guid, InventoryItem>();
        foreach (var id in itemIds.Distinct().Order())
        {
            if (await LockItemAsync(tenantId, id, cancellationToken) is { } item)
            {
                items[item.Id] = item;
            }
        }

        return items;
    }

    public async Task<Reservation?> LockReservationAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        var reservation = await context.Reservations
            .FromSql($"SELECT * FROM [Reservations] WITH (UPDLOCK, ROWLOCK) WHERE [TenantId] = {tenantId} AND [Id] = {reservationId}")
            .SingleOrDefaultAsync(cancellationToken);
        return await WithLinesAsync(reservation, cancellationToken);
    }

    public async Task<Reservation?> ClaimOverdueReservationAsync(CancellationToken cancellationToken)
    {
        // READPAST skips reservations another instance's worker, or a commit or release, holds; they are re-examined on a
        // later cycle. The literal matches the filtered index of active reservations.
        var reservation = await context.Reservations
            .FromSql($"SELECT TOP (1) * FROM [Reservations] WITH (UPDLOCK, READPAST, ROWLOCK) WHERE [Status] = 'Active' AND [ExpiresAt] <= TODATETIMEOFFSET(SYSUTCDATETIME(), 0) ORDER BY [ExpiresAt]")
            .SingleOrDefaultAsync(cancellationToken);
        return await WithLinesAsync(reservation, cancellationToken);
    }

    public async Task LockReservationKeyAsync(string clientId, Guid tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);

        // A hash keeps the resource within sp_getapplock's 255 characters and the key out of lock diagnostics.
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{clientId}\n{tenantId:D}\n{idempotencyKey}")));
        var connection = (SqlConnection)context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("The reservation key lock needs the write transaction.");
        command.CommandText = "sp_getapplock";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = ReservationKeyLockPrefix + scope });
        command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
        command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Transaction" });
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = InventoryWriteGate.LockTimeoutMilliseconds });
        var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(result);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 0 and 1: granted. -1: timeout, -2: cancelled, -3: deadlock victim, -999: error.
        switch ((int)result.Value!)
        {
            case >= 0:
                return;
            case -3:
                throw new ApplicationLockDeadlockException();
            default:
                throw new TimeoutException("The reservation key lock could not be obtained in time.");
        }
    }

    public async Task<Reservation?> FindReservationByKeyAsync(string clientId, Guid tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var reservation = await context.Reservations
            .SingleOrDefaultAsync(r => r.OwnerClientId == clientId && r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey, cancellationToken);
        return await WithLinesAsync(reservation, cancellationToken);
    }

    public Task<StockMovement?> FindAdjustmentByKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken) =>
        context.StockMovements.AsNoTracking()
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.ActorId == subjectId && m.IdempotencyKey == idempotencyKey, cancellationToken);

    public void Add(InventoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        context.InventoryItems.Add(item);
    }

    public void Add(Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        context.Reservations.Add(reservation);
    }

    private async Task<Reservation?> WithLinesAsync(Reservation? reservation, CancellationToken cancellationToken)
    {
        if (reservation is not null)
        {
            await context.Entry(reservation).Collection(r => r.Lines).LoadAsync(cancellationToken);
        }

        return reservation;
    }
}

/// <summary>
/// <c>sp_getapplock</c> chose the request as a deadlock victim (result -3). The gate retries the whole transaction, as
/// for a statement deadlock (error 1205).
/// </summary>
internal sealed class ApplicationLockDeadlockException : Exception
{
    public ApplicationLockDeadlockException()
        : base("An application lock request was chosen as a deadlock victim.")
    {
    }

    public ApplicationLockDeadlockException(string message)
        : base(message)
    {
    }

    public ApplicationLockDeadlockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
