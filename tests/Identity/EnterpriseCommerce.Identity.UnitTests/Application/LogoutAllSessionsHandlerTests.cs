using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.Sessions;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.Identity.UnitTests.Application;

public sealed class LogoutAllSessionsHandlerTests
{
    [Fact]
    public async Task HandleAsync_CommitsVersionThenRunsCleanup()
    {
        var transaction = new FakeTransaction(currentVersion: 4);
        var cleanup = new FakeCleanup(CleanupOutcome.Completed);
        var handler = new LogoutAllSessionsHandler(transaction, cleanup, NullLogger<LogoutAllSessionsHandler>.Instance);

        var result = await handler.HandleAsync("user-1", TestContext.Current.CancellationToken);

        Assert.Equal(new LogoutAllResult(5, CleanupCompleted: true), result);
        Assert.True(transaction.Committed);
        Assert.Equal(["user-1"], cleanup.Subjects);
    }

    [Fact]
    public async Task HandleAsync_CleanupFailure_DoesNotReverseOrHideTheCommittedDecision()
    {
        var transaction = new FakeTransaction(currentVersion: 1);
        var handler = new LogoutAllSessionsHandler(transaction, new FakeCleanup(CleanupOutcome.Failed), NullLogger<LogoutAllSessionsHandler>.Instance);

        var result = await handler.HandleAsync("user-1", TestContext.Current.CancellationToken);

        Assert.True(transaction.Committed);
        Assert.Equal(2, result.SecurityVersion);
        Assert.False(result.CleanupCompleted);
    }

    [Fact]
    public async Task HandleAsync_MissingSecurityVersion_FailsClosedWithoutCleanup()
    {
        var transaction = new FakeTransaction(currentVersion: null);
        var cleanup = new FakeCleanup(CleanupOutcome.Completed);
        var handler = new LogoutAllSessionsHandler(transaction, cleanup, NullLogger<LogoutAllSessionsHandler>.Instance);

        await Assert.ThrowsAsync<SecurityStateUnavailableException>(() =>
            handler.HandleAsync("user-1", TestContext.Current.CancellationToken));

        Assert.False(transaction.Committed);
        Assert.Empty(cleanup.Subjects);
    }

    [Fact]
    public async Task HandleAsync_TransactionUnavailable_PropagatesWithoutCleanup()
    {
        var cleanup = new FakeCleanup(CleanupOutcome.Completed);
        var handler = new LogoutAllSessionsHandler(new UnavailableTransaction(), cleanup, NullLogger<LogoutAllSessionsHandler>.Instance);

        await Assert.ThrowsAsync<SecurityStateUnavailableException>(() =>
            handler.HandleAsync("user-1", TestContext.Current.CancellationToken));

        Assert.Empty(cleanup.Subjects);
    }

    private sealed class FakeTransaction(long? currentVersion) : IAuthoritativeSecurityTransaction, IAuthoritativeSecurityUnit
    {
        private long? _version = currentVersion;

        public bool Committed { get; private set; }

        public async Task<TResult> ExecuteAsync<TResult>(
            string operation,
            Func<IAuthoritativeSecurityUnit, CancellationToken, Task<TransactionDecision<TResult>>> work,
            CancellationToken cancellationToken)
        {
            var decision = await work(this, cancellationToken);
            Committed = decision.ShouldCommit;
            return decision.Result;
        }

        public Task<AccountSignInState?> ReadAccountStateAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryRecordRefreshRedemptionAsync(string tokenId, string? authorizationId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long?> ReadSecurityVersionAsync(string userId, CancellationToken cancellationToken) => Task.FromResult(_version);

        public Task<long?> IncrementSecurityVersionAsync(string userId, CancellationToken cancellationToken)
        {
            _version = _version is null ? null : _version + 1;
            return Task.FromResult(_version);
        }

        public Task AddSecurityTokenAsync(AccountSecurityToken token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string?> TryConsumeSecurityTokenAsync(string tokenHash, SecurityTokenPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> SupersedeSecurityTokensAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryRevokeAuthorizationAsync(string authorizationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnavailableTransaction : IAuthoritativeSecurityTransaction
    {
        public Task<TResult> ExecuteAsync<TResult>(
            string operation,
            Func<IAuthoritativeSecurityUnit, CancellationToken, Task<TransactionDecision<TResult>>> work,
            CancellationToken cancellationToken) =>
            throw new SecurityStateUnavailableException();
    }

    private sealed class FakeCleanup(CleanupOutcome outcome) : ISessionRevocationCleanup
    {
        public List<string> Subjects { get; } = [];

        public Task<CleanupOutcome> RevokeAllForSubjectAsync(string subject, CancellationToken cancellationToken)
        {
            Subjects.Add(subject);
            return Task.FromResult(outcome);
        }

        public Task<CleanupOutcome> RevokeAuthorizationTokensAsync(string authorizationId, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }
}
