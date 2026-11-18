using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
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
    IOpenIddictAuthorizationManager authorizations) : IAuthoritativeSecurityUnit
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

    public Task<int> SupersedeSecurityTokensAsync(string userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return context.AccountSecurityTokens
            .Where(token => token.UserId == userId && token.ConsumedAt == null && token.SupersededAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.SupersededAt, now), cancellationToken);
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
