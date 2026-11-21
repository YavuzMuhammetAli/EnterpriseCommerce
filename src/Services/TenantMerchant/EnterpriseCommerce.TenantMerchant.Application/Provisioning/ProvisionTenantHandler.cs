using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Application.Provisioning;

/// <summary>
/// A trusted service caller's request to provision a tenant. <see cref="ClientId"/> is the authenticated, allow-listed
/// <c>svc-</c> client, never a request value. The owner role is fixed by the server: the request cannot choose it.
/// </summary>
public sealed record ProvisionTenantCommand(
    string ClientId,
    string ProvisioningKey,
    string? Slug,
    string? DisplayName,
    string? OwnerSubjectId,
    string? ActorReference);

public enum ProvisionTenantOutcome
{
    /// <summary>The tenant, its merchant profile and the owner membership were created.</summary>
    Created,

    /// <summary>The same client already provisioned this request with this key: the original tenant is returned.</summary>
    Replayed,

    /// <summary>The request is invalid; see <see cref="ProvisionTenantResult.Errors"/>.</summary>
    Invalid,

    /// <summary>Another tenant already has the slug.</summary>
    SlugTaken,

    /// <summary>The client used the key before for a different request.</summary>
    ProvisioningKeyReused,
}

public sealed record ProvisionTenantResult(
    ProvisionTenantOutcome Outcome,
    TenantView? Tenant = null,
    MembershipView? Owner = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// Provisions a tenant with its merchant profile and the initial <see cref="MerchantRole.MerchantOwner"/> membership,
/// atomically and idempotently per (client, <c>Idempotency-Key</c>).
/// </summary>
/// <remarks>
/// The key is checked first, so a retry is answered from the stored tenant. A race between two requests is settled by
/// the database's unique constraints, after which the outcome is classified again from the committed state.
/// The handler cannot verify that the owner subject exists in Identity: it relies on the allow-listed caller
/// (PHASE_3_TENANT_MERCHANT.md, residual risks).
/// </remarks>
public sealed partial class ProvisionTenantHandler(
    ITenantProvisioningStore store,
    ITenantReadStore reads,
    TimeProvider timeProvider,
    ILogger<ProvisionTenantHandler> logger)
{
    public async Task<ProvisionTenantResult> HandleAsync(ProvisionTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!TenantSlug.TryCreate(command.Slug, out var slug))
        {
            errors["slug"] = [$"slug must be {TenantSlug.MinLength} to {TenantSlug.MaxLength} lowercase letters, digits or single hyphens, starting and ending with a letter or digit."];
        }

        if (!MerchantDisplayName.TryCreate(command.DisplayName, out var displayName))
        {
            errors["displayName"] = [$"displayName must be 1 to {MerchantDisplayName.MaxLength} characters without control characters."];
        }

        if (!SubjectId.TryCreate(command.OwnerSubjectId, out var owner))
        {
            errors["ownerSubjectId"] = [$"ownerSubjectId must be an Identity subject identifier of 1 to {SubjectId.MaxLength} characters without whitespace."];
        }

        if (!ActorReference.TryCreate(command.ActorReference, out var actor))
        {
            errors["actorReference"] = [$"actorReference must be 1 to {ActorReference.MaxLength} characters of letters, digits, '.', '_', ':' or '-'."];
        }

        if (slug is null || displayName is null || owner is null || actor is null)
        {
            return new ProvisionTenantResult(ProvisionTenantOutcome.Invalid, Errors: errors);
        }

        var requestHash = ProvisioningRequestHash.Compute(slug, displayName, owner, actor);

        if (await store.FindByProvisioningKeyAsync(command.ClientId, command.ProvisioningKey, cancellationToken) is { } earlier)
        {
            return await ReplayAsync(command, earlier, requestHash, cancellationToken);
        }

        if (await store.SlugExistsAsync(slug.Value, cancellationToken))
        {
            return await ConflictAsync(command, requestHash, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var tenant = Tenant.Provision(slug, displayName, new ProvisioningRecord(command.ClientId, command.ProvisioningKey, requestHash, actor), now);
        var ownerMembership = Membership.CreateOwner(tenant.Id, owner, now);

        if (await store.AddAsync(tenant, ownerMembership, cancellationToken) == TenantInsertOutcome.Inserted)
        {
            LogTenantProvisioned(logger, tenant.Id, command.ClientId, actor.Value, ownerMembership.Id);
            return new ProvisionTenantResult(
                ProvisionTenantOutcome.Created,
                new TenantView(tenant.Id, tenant.Slug, tenant.Status, tenant.CreatedAt, tenant.MerchantProfile.DisplayName),
                ToView(ownerMembership));
        }

        return await ConflictAsync(command, requestHash, cancellationToken);
    }

    /// <summary>
    /// The slug or the key is held by a committed tenant. The key is read again first: a concurrent retry of this very
    /// request may have committed after the first key lookup, and it must be answered as a replay, not a conflict.
    /// </summary>
    private async Task<ProvisionTenantResult> ConflictAsync(ProvisionTenantCommand command, byte[] requestHash, CancellationToken cancellationToken)
    {
        if (await store.FindByProvisioningKeyAsync(command.ClientId, command.ProvisioningKey, cancellationToken) is { } concurrent)
        {
            return await ReplayAsync(command, concurrent, requestHash, cancellationToken);
        }

        LogSlugTaken(logger, command.ClientId);
        return new ProvisionTenantResult(ProvisionTenantOutcome.SlugTaken);
    }

    private async Task<ProvisionTenantResult> ReplayAsync(
        ProvisionTenantCommand command,
        ProvisionedTenant earlier,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!earlier.RequestHash.SequenceEqual(requestHash))
        {
            LogProvisioningKeyReused(logger, command.ClientId, earlier.Tenant.Id);
            return new ProvisionTenantResult(ProvisionTenantOutcome.ProvisioningKeyReused);
        }

        LogProvisioningReplayed(logger, earlier.Tenant.Id, command.ClientId);
        var owner = await reads.GetInitialOwnerAsync(earlier.Tenant.Id, cancellationToken);
        return new ProvisionTenantResult(ProvisionTenantOutcome.Replayed, earlier.Tenant, owner);
    }

    private static MembershipView ToView(Membership membership) =>
        new(membership.Id, membership.TenantId, membership.SubjectId, membership.Role, membership.Status, membership.CreatedAt);

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} provisioned by client {ClientId} for actor {ActorReference} with owner membership {MembershipId}")]
    private static partial void LogTenantProvisioned(ILogger logger, Guid tenantId, string clientId, string actorReference, Guid membershipId);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Tenant provisioning by client {ClientId} replayed for tenant {TenantId}")]
    private static partial void LogProvisioningReplayed(ILogger logger, Guid tenantId, string clientId);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Tenant provisioning by client {ClientId} rejected: the slug is taken")]
    private static partial void LogSlugTaken(ILogger logger, string clientId);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Tenant provisioning by client {ClientId} rejected: its provisioning key of tenant {TenantId} was reused with a different request")]
    private static partial void LogProvisioningKeyReused(ILogger logger, string clientId, Guid tenantId);
}

/// <summary>
/// SHA-256 over a canonical, versioned, length-prefixed encoding of the validated request fields. Two requests have the
/// same hash exactly when they ask for the same tenant.
/// </summary>
public static class ProvisioningRequestHash
{
    private const string Version = "tenant-provisioning/v1";

    public static byte[] Compute(TenantSlug slug, MerchantDisplayName displayName, SubjectId owner, ActorReference actor)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(actor);

        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in new[] { Version, slug.Value, displayName.Value, owner.Value, actor.Value })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }

        return SHA256.HashData(buffer.ToArray());
    }
}
