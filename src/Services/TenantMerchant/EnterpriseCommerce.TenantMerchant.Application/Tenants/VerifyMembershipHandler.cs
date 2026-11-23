using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Application.Tenants;

/// <summary>
/// The verdict for another service (ADR-025 section 1): whether the subject has an <c>Active</c> membership in an
/// <c>Active</c> tenant, and its role. A negative verdict never says why: an unknown tenant, no membership, and a
/// suspended or removed membership look the same.
/// </summary>
public sealed record MembershipVerdict(bool Active, MerchantRole? Role)
{
    public static readonly MembershipVerdict Inactive = new(false, null);
}

/// <summary>The result of a verification request: a verdict, or validation errors for a malformed subject.</summary>
public sealed record MembershipVerificationResult(MembershipVerdict? Verdict, IReadOnlyDictionary<string, string[]>? Errors);

/// <summary>
/// Answers the internal membership verification of an allow-listed service client (ADR-025). It reads the current
/// state on every call; nothing is cached, so a suspension, removal or role change applies to the next verification.
/// </summary>
public sealed class VerifyMembershipHandler(ITenantReadStore store)
{
    public async Task<MembershipVerificationResult> HandleAsync(Guid tenantId, string? subjectId, CancellationToken cancellationToken)
    {
        if (!SubjectId.TryCreate(subjectId, out var subject))
        {
            return new MembershipVerificationResult(
                null,
                new Dictionary<string, string[]>
                {
                    ["subjectId"] = [$"The subject ID is required: 1 to {SubjectId.MaxLength} characters without white space or control characters."],
                });
        }

        var tenant = await store.GetTenantAsync(tenantId, cancellationToken);
        if (tenant is not { Status: TenantStatus.Active })
        {
            return new MembershipVerificationResult(MembershipVerdict.Inactive, null);
        }

        var membership = await store.GetCurrentMembershipAsync(tenantId, subject.Value, cancellationToken);
        var verdict = membership is { Status: MembershipStatus.Active }
            ? new MembershipVerdict(true, membership.Role)
            : MembershipVerdict.Inactive;
        return new MembershipVerificationResult(verdict, null);
    }
}
