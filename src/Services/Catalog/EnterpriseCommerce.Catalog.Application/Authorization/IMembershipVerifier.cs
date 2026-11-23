namespace EnterpriseCommerce.Catalog.Application.Authorization;

/// <summary>
/// Tenant &amp; Merchant's verdict for one subject in one tenant (ADR-025 section 1): an active membership and its role,
/// or nothing. The role is a string from Tenant &amp; Merchant's role set.
/// </summary>
public sealed record MembershipVerdict(bool Active, string? Role)
{
    public static readonly MembershipVerdict Inactive = new(false, null);
}

/// <summary>
/// Asks Tenant &amp; Merchant, the membership authority, for a verdict. Implementations never cache verdicts and throw
/// <see cref="MembershipUnavailableException"/> when no verdict can be obtained.
/// </summary>
public interface IMembershipVerifier
{
    Task<MembershipVerdict> VerifyAsync(Guid tenantId, string subjectId, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>The authenticated merchant-portal user of a request: the token's subject and the request's correlation ID.</summary>
public sealed record MerchantCaller(string SubjectId, string? CorrelationId);
