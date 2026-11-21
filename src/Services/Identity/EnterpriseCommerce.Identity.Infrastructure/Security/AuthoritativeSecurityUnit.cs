using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.Infrastructure.Security;

/// <summary>
/// The operations of one authoritative transaction attempt. Every statement runs on the attempt's
/// <see cref="IdentityServiceDbContext"/> and therefore inside its transaction. The OpenIddict manager
/// resolves the same scoped context, so the single authorization change is part of the transaction too.
/// </summary>
internal sealed class AuthoritativeSecurityUnit(
    IdentityServiceDbContext context,
    IOpenIddictAuthorizationManager authorizations,
    UserManager<ApplicationUser> users) : IAuthoritativeSecurityUnit
{
    public async Task<AccountSignInState?> ReadAccountStateAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var account = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Status, user.EmailConfirmed })
            .SingleOrDefaultAsync(cancellationToken);

        return account is null ? null : new AccountSignInState(account.Status, account.EmailConfirmed);
    }

    public async Task<Application.Accounts.AccountEmailState?> ReadAccountEmailStateAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var account = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Status, user.EmailConfirmed, user.NormalizedEmail })
            .SingleOrDefaultAsync(cancellationToken);

        return account is null ? null : new Application.Accounts.AccountEmailState(account.Status, account.EmailConfirmed, account.NormalizedEmail);
    }

    public async Task<bool> TryRecordRefreshRedemptionAsync(
        string tokenId,
        string? authorizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);

        try
        {
            await context.Database.ExecuteSqlAsync(
                $"INSERT INTO [RefreshTokenRedemptions] ([TokenId], [AuthorizationId], [RedeemedAt]) VALUES ({tokenId}, {authorizationId}, {now})",
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (SqlServerErrors.IsUniqueViolation(exception))
        {
            // The primary key on the token entry ID admits exactly one redemption: this request is a reuse.
            return false;
        }
    }

    public async Task<long?> ReadSecurityVersionAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return await context.SecurityVersions
            .AsNoTracking()
            .Where(record => record.UserId == userId)
            .Select(record => (long?)record.Version)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<long?> ReadSecurityVersionForDecisionAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // HOLDLOCK keeps the shared row lock until the transaction ends, also under READ_COMMITTED_SNAPSHOT. An
        // increment (exclusive lock) therefore waits until the decision based on this value has committed.
        var versions = await context.Database
            .SqlQuery<long>($"SELECT [Version] AS [Value] FROM [SecurityVersions] WITH (HOLDLOCK, ROWLOCK) WHERE [UserId] = {userId}")
            .ToListAsync(cancellationToken);

        return versions.Count == 0 ? null : versions[0];
    }

    public async Task<long?> IncrementSecurityVersionAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // One statement: the row lock is held until the transaction ends, and OUTPUT returns the new value.
        var versions = await context.Database
            .SqlQuery<long>($"UPDATE [SecurityVersions] SET [Version] = [Version] + 1 OUTPUT inserted.[Version] AS [Value] WHERE [UserId] = {userId}")
            .ToListAsync(cancellationToken);

        return versions.Count == 0 ? null : versions[0];
    }

    public async Task AddSecurityTokenAsync(AccountSecurityToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        await context.AccountSecurityTokens
            .Where(existing => existing.UserId == token.UserId
                && existing.Purpose == token.Purpose
                && existing.ConsumedAt == null
                && existing.SupersededAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(existing => existing.SupersededAt, token.CreatedAt), cancellationToken);

        context.AccountSecurityTokens.Add(token);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> TryConsumeSecurityTokenAsync(
        string tokenHash,
        SecurityTokenPurpose purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        var purposeValue = purpose.ToString();

        // Conditional single-row update (ADR-018): of two concurrent consumers, the second finds the row already
        // consumed and changes nothing.
        var consumedBy = await context.Database
            .SqlQuery<string>($"""
                UPDATE [AccountSecurityTokens]
                SET [ConsumedAt] = {now}
                OUTPUT inserted.[UserId] AS [Value]
                WHERE [TokenHash] = {tokenHash}
                  AND [Purpose] = {purposeValue}
                  AND [ConsumedAt] IS NULL
                  AND [SupersededAt] IS NULL
                  AND [ExpiresAt] > {now}
                """)
            .ToListAsync(cancellationToken);

        return consumedBy.Count == 1 ? consumedBy[0] : null;
    }

    public Task<int> SupersedeSecurityTokensAsync(string userId, SecurityTokenPurpose? purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var outstanding = context.AccountSecurityTokens
            .Where(token => token.UserId == userId && token.ConsumedAt == null && token.SupersededAt == null);
        if (purpose is { } onlyPurpose)
        {
            outstanding = outstanding.Where(token => token.Purpose == onlyPurpose);
        }

        return outstanding.ExecuteUpdateAsync(setters => setters.SetProperty(token => token.SupersededAt, now), cancellationToken);
    }

    public async Task<AccountCreation> CreateAccountAsync(string email, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(password);

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = false,
            Status = AccountStatus.Active,
            CreatedAt = now,
        };

        IdentityResult result;
        try
        {
            // Applies the password policy, normalizes user name and email, and inserts the user.
            result = await users.CreateAsync(user, password);
        }
        catch (Exception exception) when (SqlServerErrors.IsUniqueViolation(exception))
        {
            // A concurrent registration won the unique index. Forget the failed insert so that the rest of the
            // transaction does not try it again.
            context.ChangeTracker.Clear();
            return new AccountCreation(AccountCreationStatus.DuplicateEmail, null);
        }

        if (!result.Succeeded)
        {
            context.ChangeTracker.Clear();
            return new AccountCreation(
                result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? AccountCreationStatus.DuplicateEmail
                    : AccountCreationStatus.Rejected,
                null);
        }

        context.SecurityVersions.Add(new SecurityVersionRecord { UserId = user.Id, Version = SecurityVersionPolicy.InitialVersion });
        await context.SaveChangesAsync(cancellationToken);
        return new AccountCreation(AccountCreationStatus.Created, user.Id);
    }

    public async Task<AccountByEmail?> FindAccountByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);

        var account = await context.Users
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .Select(user => new { user.Id, user.Status, user.EmailConfirmed })
            .FirstOrDefaultAsync(cancellationToken);

        return account is null ? null : new AccountByEmail(account.Id, new AccountSignInState(account.Status, account.EmailConfirmed));
    }

    public async Task<bool> ConfirmEmailAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var concurrencyStamp = Guid.NewGuid().ToString();
        var updated = await context.Users
            .Where(user => user.Id == userId && user.Status == AccountStatus.Active && !user.EmailConfirmed)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.EmailConfirmed, true)
                    .SetProperty(user => user.ConcurrencyStamp, concurrencyStamp),
                cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TrySetPasswordHashAsync(
        string userId,
        string newPasswordHash,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);

        var account = context.Users.Where(user => user.Id == userId && user.Status == AccountStatus.Active);
        if (expectedCurrentHash is not null)
        {
            account = account.Where(user => user.PasswordHash == expectedCurrentHash);
        }

        // One statement, as UserManager would do in its update: the hash, a new security stamp (which ends
        // Data Protection tokens) and concurrency stamp, and a cleared lockout.
        var securityStamp = Guid.NewGuid().ToString();
        var concurrencyStamp = Guid.NewGuid().ToString();
        var updated = await account.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(user => user.PasswordHash, newPasswordHash)
                .SetProperty(user => user.SecurityStamp, securityStamp)
                .SetProperty(user => user.ConcurrencyStamp, concurrencyStamp)
                .SetProperty(user => user.AccessFailedCount, 0)
                .SetProperty(user => user.LockoutEnd, (DateTimeOffset?)null),
            cancellationToken);
        return updated == 1;
    }

    public async Task<DateTime?> LockAccountForSecurityEmailAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = await context.Database
            .SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value] FROM [SecurityVersions] WITH (UPDLOCK, ROWLOCK) WHERE [UserId] = {userId}")
            .ToListAsync(cancellationToken);
        return now.Count == 0 ? null : now[0];
    }

    public async Task<IReadOnlyList<DateTime>> ReadSecurityEmailRequestTimesAsync(
        string userId,
        SecurityTokenPurpose purpose,
        DateTime since,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return await context.SecurityEmailDeliveries
            .AsNoTracking()
            .Where(delivery => delivery.UserId == userId && delivery.Purpose == purpose && delivery.CreatedAt >= since)
            .Select(delivery => delivery.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> CreateSecurityEmailDeliveryAsync(string userId, SecurityTokenPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var id = Guid.NewGuid();
        var purposeValue = purpose.ToString();
        var queued = nameof(SecurityEmailDeliveryStatus.Queued);
        await context.Database.ExecuteSqlAsync(
            $"INSERT INTO [SecurityEmailDeliveries] ([Id], [UserId], [Purpose], [Status], [AttemptCount]) VALUES ({id}, {userId}, {purposeValue}, {queued}, 0)",
            cancellationToken);
        return id;
    }

    public async Task<bool> LinkSecurityEmailTokenAsync(Guid deliveryId, Guid tokenId, CancellationToken cancellationToken)
    {
        var dispatching = nameof(SecurityEmailDeliveryStatus.Dispatching);
        var updated = await context.Database.ExecuteSqlAsync(
            $"UPDATE [SecurityEmailDeliveries] SET [SecurityTokenId] = {tokenId} WHERE [Id] = {deliveryId} AND [Status] = {dispatching}",
            cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TryChangeAccountStatusAsync(
        string userId,
        AccountStatus from,
        AccountStatus to,
        AccountStatusReason reason,
        string actorReference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorReference);
        if (!AccountLifecycle.CanTransition(from, to))
        {
            throw new ArgumentException($"The account status transition {from} to {to} is not allowed.", nameof(to));
        }

        var fromValue = from.ToString();
        var toValue = to.ToString();
        var reasonValue = reason.ToString();
        var concurrencyStamp = Guid.NewGuid().ToString();

        // Disabling rotates the security stamp, so nothing derived from the old stamp survives. Reactivation does not
        // need to. One statement: the row lock is held until the transaction ends.
        var securityStamp = to == AccountStatus.Disabled ? Guid.NewGuid().ToString() : null;
        var updated = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE [AspNetUsers]
            SET [Status] = {toValue},
                [StatusReason] = {reasonValue},
                [StatusChangedBy] = {actorReference},
                [StatusChangedAt] = SYSDATETIMEOFFSET(),
                [SecurityStamp] = COALESCE({securityStamp}, [SecurityStamp]),
                [ConcurrencyStamp] = {concurrencyStamp}
            WHERE [Id] = {userId} AND [Status] = {fromValue}
            """,
            cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TryRevokeAuthorizationAsync(string authorizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);

        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null)
        {
            return false;
        }

        if (await authorizations.HasStatusAsync(authorization, OpenIddictConstants.Statuses.Revoked, cancellationToken))
        {
            return false;
        }

        return await authorizations.TryRevokeAsync(authorization, cancellationToken);
    }
}
