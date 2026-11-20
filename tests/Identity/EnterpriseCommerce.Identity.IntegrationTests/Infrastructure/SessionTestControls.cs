using System.Collections.Concurrent;
using EnterpriseCommerce.Identity.Application.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;

/// <summary>
/// Test decorator of the authoritative transaction. For a named operation it can hold every attempt <b>after its work
/// and before its commit</b> (so the attempt keeps its locks), or fail the operation as an unavailable database would.
/// Every other operation passes through unchanged.
/// </summary>
public sealed class ControllableSecurityTransaction(IAuthoritativeSecurityTransaction inner) : IAuthoritativeSecurityTransaction
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, CommitGate> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _failures = new(StringComparer.Ordinal);

    /// <summary>Holds the next attempts of <paramref name="operation"/> before commit until the gate is released.</summary>
    public CommitGate HoldBeforeCommit(string operation)
    {
        var gate = new CommitGate(() => _gates.TryRemove(operation, out _));
        Assert.True(_gates.TryAdd(operation, gate), $"A gate for {operation} already exists.");
        return gate;
    }

    /// <summary>Makes <paramref name="operation"/> fail as if the database were unavailable, until disposed.</summary>
    public IDisposable FailOperation(string operation)
    {
        _failures[operation] = true;
        return new Release(() => _failures.TryRemove(operation, out _));
    }

    public Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<IAuthoritativeSecurityUnit, CancellationToken, Task<TransactionDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        if (_failures.ContainsKey(operation))
        {
            throw new SecurityStateUnavailableException($"Test: the authoritative transaction '{operation}' is unavailable.");
        }

        if (!_gates.TryGetValue(operation, out var gate))
        {
            return inner.ExecuteAsync(operation, work, cancellationToken);
        }

        return inner.ExecuteAsync(
            operation,
            async (unit, ct) =>
            {
                var decision = await work(unit, ct);
                gate.SignalReached();
                await gate.WaitForReleaseAsync(GateTimeout, ct);
                return decision;
            },
            cancellationToken);
    }

    /// <summary>Replaces the registered transaction of a service collection with this decorator.</summary>
    public static void Decorate(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(IAuthoritativeSecurityTransaction));
        services.Remove(descriptor);
        services.AddSingleton(provider => new ControllableSecurityTransaction(
            (IAuthoritativeSecurityTransaction)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)));
        services.AddSingleton<IAuthoritativeSecurityTransaction>(provider => provider.GetRequiredService<ControllableSecurityTransaction>());
    }

    /// <summary>A pause point before commit. Disposing releases it and removes it.</summary>
    public sealed class CommitGate(Action remove) : IDisposable
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once an attempt has done its work and waits before commit.</summary>
        public Task Reached => _reached.Task.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        public void Open()
        {
            remove();
            _released.TrySetResult();
        }

        public void Dispose() => Open();

        internal void SignalReached() => _reached.TrySetResult();

        internal Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            _released.Task.WaitAsync(timeout, cancellationToken);
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>Test decorator of the post-commit cleanup: can make every cleanup step report failure without running.</summary>
public sealed class ControllableCleanup(ISessionRevocationCleanup inner) : ISessionRevocationCleanup
{
    public bool Fail { get; set; }

    public Task<CleanupOutcome> RevokeAllForSubjectAsync(string subject, CancellationToken cancellationToken) =>
        Fail ? Task.FromResult(CleanupOutcome.Failed) : inner.RevokeAllForSubjectAsync(subject, cancellationToken);

    public Task<CleanupOutcome> RevokeAuthorizationTokensAsync(string authorizationId, CancellationToken cancellationToken) =>
        Fail ? Task.FromResult(CleanupOutcome.Failed) : inner.RevokeAuthorizationTokensAsync(authorizationId, cancellationToken);

    public static void Decorate(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(ISessionRevocationCleanup));
        services.Remove(descriptor);
        services.AddSingleton(provider => new ControllableCleanup(
            (ISessionRevocationCleanup)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)));
        services.AddSingleton<ISessionRevocationCleanup>(provider => provider.GetRequiredService<ControllableCleanup>());
    }
}

/// <summary>Observes SQL Server lock waits, so a test can prove an ordering instead of assuming one from timing.</summary>
public static class SqlBlocking
{
    /// <summary>
    /// Waits until at least <paramref name="count"/> requests in the test database are blocked by another session.
    /// This polls a condition (<c>sys.dm_exec_requests.blocking_session_id</c>); it never sleeps to imply an order.
    /// </summary>
    public static async Task WaitUntilBlockedAsync(string connectionString, int count = 1, TimeSpan? timeout = null)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID(@database)";
        command.Parameters.AddWithValue("@database", database);

        while (DateTime.UtcNow < deadline)
        {
            if ((int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! >= count)
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        // Diagnostics: what every other user request was doing when the wait gave up.
        command.Parameters.Clear();
        command.CommandText = """
            SELECT CONCAT(session_id, ' blocked-by=', blocking_session_id, ' wait=', wait_type, ' db=', DB_NAME(database_id), ' cmd=', command)
            FROM sys.dm_exec_requests WHERE session_id <> @@SPID AND session_id > 50
            """;
        var requests = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                requests.Add(reader.GetString(0));
            }
        }

        Assert.Fail($"Expected {count} blocked request(s) in {database} within the timeout. Requests: [{string.Join("; ", requests)}]");
    }
}
