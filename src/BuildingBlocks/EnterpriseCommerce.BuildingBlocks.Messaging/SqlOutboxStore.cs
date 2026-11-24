using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// EF Core implementation of <see cref="IOutboxStore"/> on the publishing service's <typeparamref name="TContext"/>,
/// whose model maps <see cref="OutboxMessage"/> through <see cref="OutboxModel.MapOutboxMessages"/>. Each call runs in
/// its own scope, as one statement in its own implicit transaction, so no database transaction is ever held while the
/// relay waits for Kafka.
/// </summary>
/// <typeparam name="TContext">The service's own <c>DbContext</c>.</typeparam>
public sealed class SqlOutboxStore<TContext>(IServiceScopeFactory scopes) : IOutboxStore
    where TContext : DbContext
{
    public async Task<IReadOnlyList<PendingOutboxMessage>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        // Committed rows only: READPAST skips rows still locked by an open business transaction (and with
        // READ_COMMITTED_SNAPSHOT an uncommitted row is not visible either); the next poll takes them. The relay reads
        // every pending row, not a high-water mark, so a row whose identity value is lower than an already published
        // one is still found once its transaction commits.
        var rows = await context.Set<OutboxMessage>()
            .FromSql($"SELECT TOP ({batchSize}) * FROM [OutboxMessages] WITH (READPAST) WHERE [PublishedAt] IS NULL ORDER BY [Sequence]")
            .AsNoTracking()
            .Select(m => new PendingOutboxMessage(m.Sequence, m.EventId, m.Topic, m.MessageKey, m.EventType, m.EventVersion, m.Envelope, m.OccurredAt, m.Attempts, m.LastAttemptAt))
            .ToListAsync(cancellationToken);
        return [.. rows.OrderBy(row => row.Sequence)];
    }

    public async Task MarkPublishedAsync(IReadOnlyList<long> sequences, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        if (sequences.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Set<OutboxMessage>()
            .Where(m => sequences.Contains(m.Sequence) && m.PublishedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.PublishedAt, publishedAt), cancellationToken);
    }

    public async Task RecordFailureAsync(long sequence, DateTimeOffset attemptedAt, string error, CancellationToken cancellationToken)
    {
        var lastError = error.Length <= OutboxMessage.LastErrorMaxLength ? error : error[..OutboxMessage.LastErrorMaxLength];

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Set<OutboxMessage>()
            .Where(m => m.Sequence == sequence && m.PublishedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(m => m.Attempts, m => m.Attempts + 1)
                    .SetProperty(m => m.LastAttemptAt, attemptedAt)
                    .SetProperty(m => m.LastError, lastError),
                cancellationToken);
    }

    public async Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var backlog = await context.Set<OutboxMessage>()
            .Where(m => m.PublishedAt == null)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.LongCount(), Oldest = group.Min(m => (DateTimeOffset?)m.OccurredAt) })
            .SingleOrDefaultAsync(cancellationToken);
        return backlog is null ? new OutboxBacklog(0, null) : new OutboxBacklog(backlog.Count, backlog.Oldest);
    }

    public async Task<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();

        // Pending rows are never deleted (ADR-023 section 5): the filter requires a publication time.
        return await context.Database.ExecuteSqlAsync(
            $"DELETE TOP ({batchSize}) FROM [OutboxMessages] WHERE [PublishedAt] IS NOT NULL AND [PublishedAt] < {cutoff}",
            cancellationToken);
    }
}
