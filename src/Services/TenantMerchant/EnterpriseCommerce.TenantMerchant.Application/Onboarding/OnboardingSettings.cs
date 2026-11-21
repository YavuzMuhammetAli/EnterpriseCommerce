namespace EnterpriseCommerce.TenantMerchant.Application.Onboarding;

/// <summary>Self-service onboarding limits (<c>TenantMerchant:Onboarding</c> configuration section).</summary>
public sealed class OnboardingSettings
{
    public const string SectionName = "TenantMerchant:Onboarding";

    public const int MinTenantsPerSubject = 1;
    public const int MaxTenantsPerSubjectLimit = 10;

    /// <summary>
    /// How many tenants one Identity subject may create through self-service. 1 to 10; 1 by default (owner decision,
    /// PHASE_3_S3_SELF_SERVICE_ONBOARDING.md). Provisioned tenants and memberships from invitations do not count.
    /// </summary>
    public int MaxTenantsPerSubject { get; set; } = 1;

    public IEnumerable<string> Validate()
    {
        if (MaxTenantsPerSubject is < MinTenantsPerSubject or > MaxTenantsPerSubjectLimit)
        {
            yield return $"{SectionName}:MaxTenantsPerSubject must be between {MinTenantsPerSubject} and {MaxTenantsPerSubjectLimit}.";
        }
    }
}
