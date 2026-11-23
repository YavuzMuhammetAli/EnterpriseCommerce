using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Application.Onboarding;

/// <summary>
/// A merchant-portal user's request to create a tenant of their own (self-service onboarding, PHASE_2_2 section
/// 5.1.2). <see cref="SubjectId"/> and <see cref="ClientId"/> come from the validated access token, never from the
/// request body: the owner is always the caller, and the request cannot choose a role or another owner.
/// </summary>
public sealed record CreateTenantCommand(
    string SubjectId,
    string ClientId,
    string IdempotencyKey,
    string? Slug,
    string? DisplayName,
    string? CorrelationId = null);

public enum CreateTenantOutcome
{
    /// <summary>The tenant, its merchant profile and the caller's owner membership were created.</summary>
    Created,

    /// <summary>The caller already created this tenant with this key and request: the tenant is returned again.</summary>
    Replayed,

    /// <summary>The request is invalid; see <see cref="CreateTenantResult.Errors"/>.</summary>
    Invalid,

    /// <summary>Another tenant already has the slug.</summary>
    SlugTaken,

    /// <summary>The caller used the key before for a different request.</summary>
    IdempotencyKeyReused,

    /// <summary>The caller has created the maximum number of tenants (<see cref="OnboardingSettings.MaxTenantsPerSubject"/>).</summary>
    TenantLimitReached,

    /// <summary>
    /// The key names a tenant the caller created but is no longer an active member of. Answered like any tenant the
    /// caller cannot see.
    /// </summary>
    NotAccessible,
}

public sealed record CreateTenantResult(
    CreateTenantOutcome Outcome,
    TenantView? Tenant = null,
    MembershipView? Membership = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>
/// Creates a tenant, its merchant profile and the caller's <see cref="MerchantRole.MerchantOwner"/> membership,
/// atomically, idempotently per (subject, <c>Idempotency-Key</c>), and within the per-subject limit.
/// </summary>
/// <remarks>
/// Every decision is made inside the subject's serialized transaction (<see cref="ITenantOnboardingGate"/>), so a
/// concurrent retry is answered as a replay and parallel requests cannot exceed the limit. A slug taken by another
/// subject at the same instant is rejected by the unique index; the request is then classified once more from the
/// committed state.
/// </remarks>
public sealed partial class CreateTenantHandler(
    ITenantOnboardingGate gate,
    OnboardingSettings settings,
    TimeProvider timeProvider,
    ILogger<CreateTenantHandler> logger)
{
    private const int MaxClassifications = 2;

    public async Task<CreateTenantResult> HandleAsync(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Domain.Memberships.SubjectId.TryCreate(command.SubjectId, out var subject))
        {
            throw new ArgumentException("The caller's subject must be a valid Identity subject.", nameof(command));
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!TenantSlug.TryCreate(command.Slug, out var slug))
        {
            errors["slug"] = [$"slug must be {TenantSlug.MinLength} to {TenantSlug.MaxLength} lowercase letters, digits or single hyphens, starting and ending with a letter or digit."];
        }

        if (!MerchantDisplayName.TryCreate(command.DisplayName, out var displayName))
        {
            errors["displayName"] = [$"displayName must be 1 to {MerchantDisplayName.MaxLength} characters without control characters."];
        }

        if (slug is null || displayName is null)
        {
            return new CreateTenantResult(CreateTenantOutcome.Invalid, Errors: errors);
        }

        var requestHash = OnboardingRequestHash.Compute(slug, displayName, subject);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await gate.ExecuteAsync(
                    subject,
                    (session, ct) => DecideAsync(session, command, subject, slug, displayName, requestHash, ct),
                    cancellationToken);

                // Logged only once the gate has committed: a rolled-back attempt is never reported as a creation.
                if (result is { Outcome: CreateTenantOutcome.Created, Tenant: { } tenant, Membership: { } owner })
                {
                    LogTenantCreated(logger, tenant.Id, subject.Value, command.ClientId, owner.Id);
                }

                return result;
            }
            catch (TenantWriteConflictException exception) when (exception.Conflict == TenantWriteConflict.UniqueConstraint && attempt < MaxClassifications)
            {
                // Another subject committed the slug between the check and the insert. Classify again: the next pass
                // sees the committed tenant and answers from it.
            }
            catch (TenantWriteConflictException exception) when (exception.Conflict == TenantWriteConflict.UniqueConstraint)
            {
                LogSlugTaken(logger, subject.Value);
                return new CreateTenantResult(CreateTenantOutcome.SlugTaken);
            }
        }
    }

    private async Task<WriteDecision<CreateTenantResult>> DecideAsync(
        ITenantOnboardingSession session,
        CreateTenantCommand command,
        SubjectId subject,
        TenantSlug slug,
        MerchantDisplayName displayName,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (await session.FindByKeyAsync(command.IdempotencyKey, cancellationToken) is { } earlier)
        {
            return WriteDecision.Rollback(Replay(earlier, subject, requestHash));
        }

        var created = await session.CountTenantsAsync(cancellationToken);
        if (created >= settings.MaxTenantsPerSubject)
        {
            LogTenantLimitReached(logger, subject.Value, created, settings.MaxTenantsPerSubject);
            return WriteDecision.Rollback(new CreateTenantResult(CreateTenantOutcome.TenantLimitReached));
        }

        if (await session.SlugExistsAsync(slug.Value, cancellationToken))
        {
            LogSlugTaken(logger, subject.Value);
            return WriteDecision.Rollback(new CreateTenantResult(CreateTenantOutcome.SlugTaken));
        }

        var now = timeProvider.GetUtcNow();
        var tenant = Tenant.CreateBySelfService(slug, displayName, new SelfServiceRecord(command.ClientId, command.IdempotencyKey, requestHash, subject), now);
        var owner = Membership.CreateOwner(tenant.Id, subject, now);
        session.Add(tenant, owner, MembershipAuditRecord.SelfServiceOwnerCreated(owner, command.CorrelationId, now), MessageMetadata.ForRequest(command.CorrelationId));

        return WriteDecision.Commit(new CreateTenantResult(
            CreateTenantOutcome.Created,
            new TenantView(tenant.Id, tenant.Slug, tenant.Status, tenant.CreatedAt, tenant.MerchantProfile.DisplayName),
            MembershipView.Of(owner)));
    }

    private CreateTenantResult Replay(SelfServiceTenant earlier, SubjectId subject, byte[] requestHash)
    {
        if (!earlier.RequestHash.SequenceEqual(requestHash))
        {
            LogIdempotencyKeyReused(logger, subject.Value, earlier.Tenant.Id);
            return new CreateTenantResult(CreateTenantOutcome.IdempotencyKeyReused);
        }

        if (earlier.CreatorMembership is not { Status: MembershipStatus.Active } membership)
        {
            return new CreateTenantResult(CreateTenantOutcome.NotAccessible);
        }

        LogCreationReplayed(logger, earlier.Tenant.Id, subject.Value);
        return new CreateTenantResult(CreateTenantOutcome.Replayed, earlier.Tenant, membership);
    }

    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} created by self-service for subject {Subject} through client {ClientId} with owner membership {MembershipId}")]
    private static partial void LogTenantCreated(ILogger logger, Guid tenantId, string subject, string clientId, Guid membershipId);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Information,
        Message = "Self-service tenant creation by subject {Subject} replayed for tenant {TenantId}")]
    private static partial void LogCreationReplayed(ILogger logger, Guid tenantId, string subject);

    [LoggerMessage(
        EventId = 3012,
        Level = LogLevel.Information,
        Message = "Self-service tenant creation by subject {Subject} rejected: the slug is taken")]
    private static partial void LogSlugTaken(ILogger logger, string subject);

    [LoggerMessage(
        EventId = 3013,
        Level = LogLevel.Warning,
        Message = "Self-service tenant creation by subject {Subject} rejected: its idempotency key of tenant {TenantId} was reused with a different request")]
    private static partial void LogIdempotencyKeyReused(ILogger logger, string subject, Guid tenantId);

    [LoggerMessage(
        EventId = 3014,
        Level = LogLevel.Warning,
        Message = "Self-service tenant creation by subject {Subject} rejected: {Created} of at most {Limit} tenants already created")]
    private static partial void LogTenantLimitReached(ILogger logger, string subject, int created, int limit);
}

/// <summary>
/// SHA-256 over a canonical, versioned, length-prefixed encoding of a self-service request. Its version tag differs
/// from <see cref="Provisioning.ProvisioningRequestHash"/>, so the two request kinds never share a hash.
/// </summary>
public static class OnboardingRequestHash
{
    private const string Version = "tenant-onboarding/v1";

    public static byte[] Compute(TenantSlug slug, MerchantDisplayName displayName, SubjectId subject)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(subject);

        return CanonicalRequestHash.Compute(Version, slug.Value, displayName.Value, subject.Value);
    }
}
